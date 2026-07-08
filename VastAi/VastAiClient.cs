using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventForge.Configuration;
using EventForge.Infrastructure;
using Microsoft.Extensions.Options;

namespace EventForge.VastAi;

public interface IVastAiClient
{
    bool IsConfigured { get; }
    Task<List<VastOffer>> SearchOffersAsync(VastOfferFilter filter, CancellationToken ct = default);
    Task<VastAccount?> GetAccountAsync(CancellationToken ct = default);
    Task<List<VastInstance>> GetMyInstancesAsync(CancellationToken ct = default);
    Task<CreateInstanceResult> CreateInstanceAsync(CreateInstanceParams p, CancellationToken ct = default);
    Task<(bool ok, string error)> DestroyInstanceAsync(long instanceId, CancellationToken ct = default);
    Task<(bool ok, string error)> StopInstanceAsync(long instanceId, CancellationToken ct = default);
    Task<(bool ok, string error)> StartInstanceAsync(long instanceId, CancellationToken ct = default);
    Task<VastExecuteResult> ExecuteCommandAsync(long instanceId, string command, CancellationToken ct = default);
}

public sealed class VastExecuteResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string? ResultUrl { get; set; }
    public string? Message { get; set; }
}

public class CreateInstanceParams
{
    public long OfferId { get; set; }
    public string Mode { get; set; } = "both";
    public string HfToken { get; set; } = "";
    public string LoboSecret { get; set; } = "";
    public string LoboServer { get; set; } = "wss://www.loboforge.com";
    public string LoboBaseUrl { get; set; } = "https://www.loboforge.com";
    public string DockerImage { get; set; } = "vastai/comfy:v0.15.1-cuda-12.9-py312";
    public int DiskGb { get; set; } = 120;
    public string Label { get; set; } = "";
}

public class CreateInstanceResult
{
    public bool Ok { get; set; }
    public long InstanceId { get; set; }
    public string Error { get; set; } = "";
    public string RawResponse { get; set; } = "";
}

public class VastAiClient : IVastAiClient
{
    private const string ApiBase = "https://console.vast.ai/api/v0";
    private static readonly ConcurrentDictionary<long, DateTime> RecentSkip = new();
    private static readonly TimeSpan SkipTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly EventForgeOptions _opts;
    private readonly ILogger<VastAiClient> _log;

    public VastAiClient(HttpClient http, IOptions<EventForgeOptions> options, ILogger<VastAiClient> log)
    {
        _http = http;
        _opts = options.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.VastAi.ApiKey);

    public static void MarkOfferSkipped(long offerId) =>
        RecentSkip[offerId] = DateTime.UtcNow + SkipTtl;

    private static bool IsSkipped(long offerId) =>
        RecentSkip.TryGetValue(offerId, out var until) && until > DateTime.UtcNow;

    private void Authenticate(HttpRequestMessage req)
    {
        var key = _opts.VastAi.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<List<VastOffer>> SearchOffersAsync(VastOfferFilter filter, CancellationToken ct = default)
    {
        filter ??= new VastOfferFilter();
        var orderField = filter.SortBy switch
        {
            "cheap" => "dph_total",
            "fast" => "dlperf",
            "bang" => "dlperf_per_dphtotal",
            _ => "dlperf_per_dphtotal",
        };
        var orderDir = filter.SortBy == "cheap" ? "asc" : "desc";
        var minVramMb = filter.MinGpuRamGb * 1024;
        var maxDph = filter.MaxDollarsPerHr.ToString("0.0000", CultureInfo.InvariantCulture);
        var minReliab = filter.MinReliability.ToString("0.000", CultureInfo.InvariantCulture);
        var limit = Math.Clamp(filter.GpuNameContains.Length > 0 ? filter.Limit * 3 : filter.Limit, 1, 200);
        var verifiedClause = filter.VerifiedOnly ? "\"verified\":{\"eq\":true}," : "";
        var queryJson =
            "{" +
            "\"rentable\":{\"eq\":true}," +
            verifiedClause +
            $"\"gpu_ram\":{{\"gte\":{minVramMb}}}," +
            $"\"dph_total\":{{\"lte\":{maxDph}}}," +
            $"\"reliability2\":{{\"gte\":{minReliab}}}," +
            $"\"disk_space\":{{\"gte\":{filter.MinHostDiskGb}}}," +
            "\"num_gpus\":{\"gte\":1}," +
            $"\"order\":[[\"{orderField}\",\"{orderDir}\"]]," +
            $"\"limit\":{limit}" +
            "}";
        var url = $"{ApiBase}/bundles/?q={Uri.EscapeDataString(queryJson)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var jsonOpts = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
            };
            var list = await resp.Content.ReadFromJsonAsync<VastOfferList>(jsonOpts, ct);
            var offers = list?.Offers ?? [];
            offers = offers.Where(o => !IsSkipped(o.Id)).ToList();
            offers = offers.Where(o => GpuNodeCompatibility.GpuNameCanExecuteJobs(o.GpuName)).ToList();
            if (!string.IsNullOrWhiteSpace(filter.GpuNameContains))
            {
                var needle = filter.GpuNameContains.Trim();
                offers = offers
                    .Where(o => (o.GpuName ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .Take(filter.Limit)
                    .ToList();
            }
            return offers;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vast.ai search threw");
            return [];
        }
    }

    public async Task<VastAccount?> GetAccountAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users/current/");
            Authenticate(req);
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<VastAccount>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vast.ai get-account threw");
            return null;
        }
    }

    public async Task<List<VastInstance>> GetMyInstancesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/instances/");
            Authenticate(req);
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var list = await resp.Content.ReadFromJsonAsync<VastInstanceList>(cancellationToken: ct);
            return list?.Instances ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vast.ai get-instances threw");
            return [];
        }
    }

    public async Task<CreateInstanceResult> CreateInstanceAsync(CreateInstanceParams p, CancellationToken ct = default)
    {
        if (!IsConfigured) return new() { Ok = false, Error = "Vast.ai API key not configured." };
        if (p.OfferId <= 0) return new() { Ok = false, Error = "Missing offer ID." };
        var mode = VastAiDiskRequirements.NormalizeMode(p.Mode);
        var allowed = new[] { "image", "video", "music", "all", "ltx-native", "wan-native" };
        if (!allowed.Contains(mode))
            return new() { Ok = false, Error = $"Invalid mode '{p.Mode}'." };

        var diskGb = VastAiDiskRequirements.ClampRentDiskGb(mode, p.DiskGb);
        var nativeLtx = VastAiDiskRequirements.IsNativeLtxMode(mode);
        var nativeWan = VastAiDiskRequirements.IsNativeWanMode(mode);
        var wanEnabled = nativeWan || mode is not "image" and not "music";
        var ltx23Enabled = nativeLtx || mode is "all" or "both" or "ltx-native" or "ltx";
        var musicEnabled = nativeWan ? false : mode is not "image";
        var loboSecret = WorkerBootstrapDefaults.ResolveWorkerSecret(_opts, p.LoboSecret);
        var hfToken = WorkerBootstrapDefaults.ResolveHfToken(_opts, p.HfToken);
        var loboBaseUrl = (p.LoboBaseUrl ?? _opts.AgentScriptBaseUrl).TrimEnd('/');
        var (scriptPrimary, scriptFallback) = WorkerBootstrapDefaults.ResolveBootstrapScriptBases(_opts);
        var loboServer = p.LoboServer ?? "wss://www.loboforge.com";

        string onstart;
        if (nativeLtx)
        {
            var ltxOnstart = new List<string>
            {
                "mkdir -p /workspace", "cd /workspace",
                WorkerBootstrapDefaults.BashCurlAgentFile(scriptPrimary, scriptFallback, "provision_ltx_native.sh", "provision_ltx_native.sh"),
                "chmod +x provision_ltx_native.sh",
                $"export LOBO_SECRET={WorkerBootstrapDefaults.BashQuote(loboSecret)}",
                $"export LOBO_SERVER={WorkerBootstrapDefaults.BashQuote(loboServer)}",
                $"export LOBO_BASE_URL={WorkerBootstrapDefaults.BashQuote(loboBaseUrl)}",
                $"export HF_TOKEN={WorkerBootstrapDefaults.BashQuote(hfToken)}",
            };
            ltxOnstart.AddRange(WorkerBootstrapDefaults.EventForgeOnstartExportLines(
                _opts, mode, wanEnabled, ltx23Enabled: true, musicEnabled: false));
            ltxOnstart.Add("nohup bash provision_ltx_native.sh >> /workspace/provision.log 2>&1 &");
            onstart = string.Join("\n", new[] { "#!/bin/bash" }.Concat(ltxOnstart));
        }
        else if (nativeWan)
        {
            var wanOnstart = new List<string>
            {
                "mkdir -p /workspace", "cd /workspace",
                WorkerBootstrapDefaults.BashCurlAgentFile(scriptPrimary, scriptFallback, "provision_wan_native.sh", "provision_wan_native.sh"),
                "chmod +x provision_wan_native.sh",
                $"export LOBO_SECRET={WorkerBootstrapDefaults.BashQuote(loboSecret)}",
                $"export LOBO_SERVER={WorkerBootstrapDefaults.BashQuote(loboServer)}",
                $"export LOBO_BASE_URL={WorkerBootstrapDefaults.BashQuote(loboBaseUrl)}",
                $"export HF_TOKEN={WorkerBootstrapDefaults.BashQuote(hfToken)}",
            };
            wanOnstart.AddRange(WorkerBootstrapDefaults.EventForgeOnstartExportLines(
                _opts, mode, wanEnabled: true, ltx23Enabled: false, musicEnabled: false));
            wanOnstart.Add("nohup bash provision_wan_native.sh >> /workspace/provision.log 2>&1 &");
            onstart = string.Join("\n", new[] { "#!/bin/bash" }.Concat(wanOnstart));
        }
        else
        {
            var workerOnstart = new List<string>
            {
                "set -e", "mkdir -p /workspace", "cd /workspace",
                $"export LOBO_SECRET={WorkerBootstrapDefaults.BashQuote(loboSecret)}",
                $"export LOBO_SERVER={WorkerBootstrapDefaults.BashQuote(loboServer)}",
                $"export LOBO_BASE_URL={WorkerBootstrapDefaults.BashQuote(loboBaseUrl)}",
                $"export HF_TOKEN={WorkerBootstrapDefaults.BashQuote(hfToken)}",
            };
            workerOnstart.AddRange(WorkerBootstrapDefaults.EventForgeOnstartExportLines(
                _opts, mode, wanEnabled, ltx23Enabled, musicEnabled));
            workerOnstart.Add(WorkerBootstrapDefaults.BashCurlAgentFile(scriptPrimary, scriptFallback, "provision_worker.sh", "provision_worker.sh"));
            workerOnstart.Add("chmod +x provision_worker.sh");
            workerOnstart.Add($"bash provision_worker.sh --mode '{mode}' 2>&1 | tee /workspace/provision.log");
            onstart = string.Join("\n", new[] { "#!/bin/bash" }.Concat(workerOnstart));
        }

        var env = new Dictionary<string, string>
        {
            ["MODE"] = mode,
            ["LOBO_SECRET"] = loboSecret,
            ["LOBO_SERVER"] = loboServer,
            ["LOBO_BASE_URL"] = loboBaseUrl,
            ["HF_TOKEN"] = hfToken,
        };
        WorkerBootstrapDefaults.ApplyEventForgeVastExtraEnv(env, _opts, mode, wanEnabled, ltx23Enabled, musicEnabled);
        var label = string.IsNullOrWhiteSpace(p.Label)
            ? (nativeLtx ? "loboforge-ltx" : nativeWan ? "loboforge-wan-native" : $"loboforge-{mode}")
            : p.Label.Trim();
        env["LOBO_LABEL"] = label;
        env["LOBO_HOSTNAME"] = label;

        var body = new Dictionary<string, object?>
        {
            ["client_id"] = "me",
            ["image"] = p.DockerImage,
            ["disk"] = diskGb,
            ["env"] = env,
            ["onstart"] = onstart,
            ["ports"] = new Dictionary<string, object> { ["8188/tcp"] = new { }, ["8080/tcp"] = new { }, ["22/tcp"] = new { } },
            ["runtype"] = "ssh",
            ["label"] = label,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/asks/{p.OfferId}/")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            Authenticate(req);
            var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var bodyLower = raw.ToLowerInvariant();
                if (bodyLower.Contains("no_such_ask") || bodyLower.Contains("already rented"))
                    MarkOfferSkipped(p.OfferId);
                return new() { Ok = false, Error = $"{(int)resp.StatusCode}: {Truncate(raw, 300)}", RawResponse = raw };
            }
            long newId = 0;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("new_contract", out var idEl) && idEl.TryGetInt64(out var n))
                    newId = n;
            }
            catch { /* ok */ }
            MarkOfferSkipped(p.OfferId);
            return new() { Ok = true, InstanceId = newId, RawResponse = raw };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Vast.ai create-instance threw");
            return new() { Ok = false, Error = ex.Message };
        }
    }

    public Task<(bool ok, string error)> DestroyInstanceAsync(long instanceId, CancellationToken ct = default) =>
        SimpleAsync(HttpMethod.Delete, $"{ApiBase}/instances/{instanceId}/", null, ct);

    public Task<(bool ok, string error)> StopInstanceAsync(long instanceId, CancellationToken ct = default) =>
        SimpleAsync(HttpMethod.Put, $"{ApiBase}/instances/{instanceId}/", "{\"state\":\"stopped\"}", ct);

    public Task<(bool ok, string error)> StartInstanceAsync(long instanceId, CancellationToken ct = default) =>
        SimpleAsync(HttpMethod.Put, $"{ApiBase}/instances/{instanceId}/", "{\"state\":\"running\"}", ct);

    public async Task<VastExecuteResult> ExecuteCommandAsync(long instanceId, string command, CancellationToken ct = default)
    {
        if (!IsConfigured) return new() { Ok = false, Error = "Vast.ai API key not configured." };
        if (instanceId <= 0 || string.IsNullOrWhiteSpace(command))
            return new() { Ok = false, Error = "Invalid request." };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/instances/command/{instanceId}/")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { command }), Encoding.UTF8, "application/json"),
            };
            Authenticate(req);
            var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new() { Ok = false, Error = $"{(int)resp.StatusCode}: {Truncate(raw, 300)}" };
            return new() { Ok = true, Message = raw };
        }
        catch (Exception ex)
        {
            return new() { Ok = false, Error = ex.Message };
        }
    }

    private async Task<(bool, string)> SimpleAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        if (!IsConfigured) return (false, "Vast.ai API key not configured.");
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            Authenticate(req);
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"{(int)resp.StatusCode}: {Truncate(body, 200)}");
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
