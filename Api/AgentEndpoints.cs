using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace EventForge.Api;

/// <summary>Serves GPU worker bootstrap scripts at /agent/* (primary for Vast onstart).</summary>
public static class AgentEndpoints
{
    private static readonly HashSet<string> AllowedScripts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "provision_gpu.sh",
            "provision_worker.sh",
            "provision_ltx_native.sh",
            "provision_wan_native.sh",
            "provision_ollama.sh",
            "ensure_ops_ssh.sh",
            "loboforge_agent.py",
            "loboforge_agent_sqs.py",
            "loboforge_agent_eventforge.py",
            "loboforge_agent_common.py",
            "loboforge_ollama_agent_eventforge.py",
            "vast-agent-only-onstart.sh",
            "vast-heal-agent.sh",
            "worker-bootstrap-env.sh",
            "install_ace_step.sh",
            "ltx-agent-watchdog.sh",
            "loboforge_worker.tar.gz",
            "forge-queue-sdk.tar.gz",
        };

    private static readonly HashSet<string> JoycaptionScripts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "joycaption/joycaption_eventforge_worker.py",
            "joycaption/joycaption_server.py",
            "joycaption/joycaption_prompt.json",
            "joycaption/joycaption_cli.py",
            "joycaption/vast_joycaption_onstart.sh",
            "joycaption/vast_joycaption_minimal_onstart.sh",
            "joycaption/vast_joycaption_eventforge_worker.sh",
            "joycaption/vast_joycaption_health.sh",
            "joycaption/vast_joycaption_healthcheck.sh",
            "joycaption/vast_joycaption_watchdog.sh",
        };

    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet("/agent/{*file}", (string file, IWebHostEnvironment env, IConfiguration cfg, ILoggerFactory loggerFactory) =>
        {
            if (!AllowedScripts.Contains(file) && !JoycaptionScripts.Contains(file))
                return Results.NotFound();

            var log = loggerFactory.CreateLogger("EventForge.AgentScripts");
            var dirs = new List<string>();
            var configured = cfg["EventForge:AgentScriptsDir"];
            if (!string.IsNullOrWhiteSpace(configured))
                dirs.Add(configured);
            dirs.Add(env.ContentRootPath);
            dirs.Add(Path.Combine(env.ContentRootPath, "agent"));
            dirs.Add(Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "agent")));
            dirs.Add(Path.GetFullPath(Path.Combine(env.ContentRootPath, "..")));

            string? path = null;
            foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }

            if (path == null)
            {
                log.LogWarning("Agent file {File} not found (searched: {Dirs})", file, string.Join(", ", dirs));
                return Results.NotFound();
            }

            var contentTypeProvider = new FileExtensionContentTypeProvider();
            if (!contentTypeProvider.TryGetContentType(file, out var contentType))
                contentType = "text/plain";

            var stream = File.OpenRead(path);
            return Results.File(stream, contentType, file, enableRangeProcessing: false);
        });
    }
}
