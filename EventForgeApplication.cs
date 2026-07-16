using System.Net.WebSockets;
using System.Text.Json;
using EventForge.Api;
using EventForge.Auth;
using EventForge.Configuration;
using EventForge.Infrastructure;
using EventForge.Models;
using EventForge.Persistence;
using EventForge.Queue;
using EventForge.Services;
using EventForge.Storage;
using EventForge.VastAi;
using EventForge.WebSocket;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace EventForge;

public static class EventForgeApplication
{
    public static WebApplication Create(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        var appSecretsJson = Environment.GetEnvironmentVariable("APP_SECRETS_JSON");
        if (!string.IsNullOrWhiteSpace(appSecretsJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appSecretsJson));
            builder.Configuration.AddJsonStream(stream);
        }

        configureBuilder?.Invoke(builder);

        builder.Services.Configure<EventForgeOptions>(
            builder.Configuration.GetSection(EventForgeOptions.Section));
        builder.Services.PostConfigure<EventForgeOptions>(opts =>
            EventForgeSecretsBinder.Apply(builder.Configuration, opts));

        builder.Services.AddSingleton<IApiKeyValidator, ConfigApiKeyValidator>();
        builder.Services.AddSingleton<IWorkerKeyValidator, ConfigWorkerKeyValidator>();
        builder.Services.AddSingleton<IOpsKeyValidator, ConfigOpsKeyValidator>();
        builder.Services.AddHttpClient<IVastAiClient, VastAiClient>();
        builder.Services.AddSingleton<InMemoryJobQueue>();
        builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
        builder.Services.AddSingleton<ISqliteS3Persistence, SqliteS3Persistence>();
        builder.Services.AddSingleton<IArtifactStore, ArtifactStore>();
        builder.Services.AddSingleton<ILoraAssetBlobStore, LoraAssetBlobStore>();
        builder.Services.AddSingleton<LoraAssetCatalog>();
        builder.Services.AddSingleton<LoraAssetService>();
        builder.Services.AddSingleton<WorkerFleetTracker>();
        builder.Services.AddSingleton<OpsEventHub>();
        builder.Services.AddSingleton<ConsumerAppRegistry>();
        builder.Services.AddSingleton<OpsMetricsHistory>();
        builder.Services.AddSingleton<JobService>();
        builder.Services.AddHostedService<LeaseMonitorService>();
        builder.Services.AddSingleton<WsConnectionManager>();
        builder.Services.AddSingleton<WsProtocolHandler>();
        builder.Services.AddSingleton<WriteBehindPersistence>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WriteBehindPersistence>());
        builder.Services.AddHostedService<StartupInitializationService>();
        builder.Services.AddHostedService<EventStoreMaintenanceService>();
        builder.Services.AddHostedService<SqliteS3BackupService>();
        builder.Services.AddHostedService<SqsIngressConsumer>();

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseWebSockets();

        var sqliteS3 = app.Services.GetRequiredService<ISqliteS3Persistence>();
        var persist = app.Services.GetRequiredService<WriteBehindPersistence>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            try { persist.FlushAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            try { sqliteS3.BackupAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        });

        var listenUrl = app.Configuration[$"{EventForgeOptions.Section}:ListenUrl"] ?? "http://localhost:8090";
        app.Urls.Add(listenUrl);

        app.MapHealthEndpoints();
        app.MapAgentEndpoints();
        app.MapJobEndpoints();
        app.MapConsumerEndpoints();
        app.MapAssetEndpoints();
        app.MapFleetEndpoints();
        app.MapWorkerEndpoints();
        app.MapOpsEndpoints();
        app.MapVastEndpoints();

        app.MapGet("/api/v1/events", async (
            HttpContext ctx,
            string? since,
            IApiKeyValidator auth,
            IEventStore eventStore,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            if (!DateTimeOffset.TryParse(since, out var sinceAt))
                return Results.BadRequest(new { error = "since query parameter required (ISO8601)" });
            var records = await eventStore.QuerySinceAsync(appId, sinceAt, ct);
            var events = records.Select(ManifestParser.ToServerMessage).ToList();
            return Results.Ok(new { count = events.Count, events });
        });

        app.Map("/v1/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var auth = ctx.RequestServices.GetRequiredService<IApiKeyValidator>();
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var connections = ctx.RequestServices.GetRequiredService<WsConnectionManager>();
            var protocol = ctx.RequestServices.GetRequiredService<WsProtocolHandler>();
            var opts = ctx.RequestServices.GetRequiredService<IOptions<EventForgeOptions>>().Value;

            var session = new WsSession(socket, appId);
            connections.Add(session);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            var pingTask = RunPingLoopAsync(session, TimeSpan.FromSeconds(Math.Max(5, opts.PingIntervalSeconds)), cts.Token);

            try { await protocol.RunSessionAsync(session, cts.Token); }
            finally
            {
                cts.Cancel();
                try { await pingTask; } catch { }
                connections.Remove(session);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        });

        app.Map("/v1/ops/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var opsAuth = ctx.RequestServices.GetRequiredService<IOpsKeyValidator>();
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var opsHub = ctx.RequestServices.GetRequiredService<OpsEventHub>();
            var fleet = ctx.RequestServices.GetRequiredService<WorkerFleetTracker>();
            var queue = ctx.RequestServices.GetRequiredService<InMemoryJobQueue>();
            var apps = ctx.RequestServices.GetRequiredService<ConsumerAppRegistry>();
            var session = new OpsSession(socket);
            opsHub.Add(session);

            await session.SendJsonAsync(new { type = "ops.hello", at = DateTimeOffset.UtcNow.ToString("O") }, ctx.RequestAborted);
            await opsHub.PublishFleetSnapshotAsync(new
            {
                type = "ops.fleet.snapshot",
                snapshot = OpsEndpoints.BuildSnapshot(fleet, queue, apps),
            }, ctx.RequestAborted);

            var buffer = new byte[8192];
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var text = await session.ReceiveTextAsync(buffer, ctx.RequestAborted);
                    if (text == null) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type == "subscribe" && doc.RootElement.TryGetProperty("events", out var eventsEl)
                            && eventsEl.ValueKind == JsonValueKind.Array)
                        {
                            session.SetSubscriptions(eventsEl.EnumerateArray()
                                .Where(e => e.ValueKind == JsonValueKind.String)
                                .Select(e => e.GetString() ?? "")
                                .Where(s => s.Length > 0));
                        }
                        else if (type == "ping")
                        {
                            await session.SendJsonAsync(new { type = "pong" }, ctx.RequestAborted);
                        }
                    }
                    catch { /* ignore malformed client messages */ }
                }
            }
            finally
            {
                opsHub.Remove(session);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        });

        var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                RequestPath = "",
                OnPrepareResponse = ctx =>
                {
                    var path = ctx.File.Name;
                    if (path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    else if (path.StartsWith("index-", StringComparison.Ordinal) && path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                },
            });
            app.MapFallbackToFile("index.html");
        }

        app.Logger.LogInformation("EventForge listening on {Url}", listenUrl);
        return app;
    }

    private static async Task RunPingLoopAsync(WsSession session, TimeSpan interval, CancellationToken ct)
    {
        var tick = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(tick, ct);
            if (DateTimeOffset.UtcNow - session.LastActivityUtc >= interval)
                await session.SendJsonAsync(new { type = "ping" }, ct);
        }
    }
}
