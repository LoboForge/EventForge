using EventForge.Configuration;
using EventForge.VastAi;
using Microsoft.Extensions.Options;

namespace EventForge.Infrastructure;

public static class WorkerBootstrapDefaults
{
    public const string AgentUserAgent = "LoboForge-Worker/1.1";

    public static string ResolveHfToken(EventForgeOptions opts, string? explicitToken = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
        if (!string.IsNullOrWhiteSpace(opts.HuggingFaceToken)) return opts.HuggingFaceToken.Trim();
        return "";
    }

    public static string ResolveWorkerSecret(EventForgeOptions opts, string? explicitSecret = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitSecret)) return explicitSecret.Trim();
        if (!string.IsNullOrWhiteSpace(opts.WorkerSecret)) return opts.WorkerSecret.Trim();
        return "";
    }

    public static string BashQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    public static string AgentFetchPython(string url, string destFile) =>
        "import urllib.request; req=urllib.request.Request(" +
        BashQuote(url) + ", headers={'User-Agent':'" + AgentUserAgent + "'}); " +
        "open(" + BashQuote(destFile) + ",'wb').write(urllib.request.urlopen(req,timeout=120).read())";

    /// <summary>EventForge PublicUrl first; LoboForge AgentScriptBaseUrl fallback.</summary>
    public static (string Primary, string Fallback) ResolveBootstrapScriptBases(EventForgeOptions opts) =>
        (opts.PublicUrl.TrimEnd('/'), opts.AgentScriptBaseUrl.TrimEnd('/'));

    public static string BashCurlAgentFile(string primaryBaseUrl, string fallbackBaseUrl, string fileName, string destFile, bool optional = false)
    {
        var fail = optional ? " || true" : "";
        var ua = AgentUserAgent;
        var primary = $"{primaryBaseUrl.TrimEnd('/')}/agent/{fileName}";
        var fallback = $"{fallbackBaseUrl.TrimEnd('/')}/agent/{fileName}";
        return $"curl -fsSL -A '{ua}' '{primary}' -o {destFile}{fail} || curl -fsSL -A '{ua}' '{fallback}' -o {destFile}";
    }

    public static IEnumerable<string> EventForgeOnstartExportLines(
        EventForgeOptions opts,
        string? provisionMode = null,
        bool wanEnabled = true,
        bool ltx23Enabled = false,
        bool musicEnabled = true)
    {
        yield return $"export LOBO_GEN_QUEUE=eventforge";
        yield return $"export EVENT_FORGE_URL={BashQuote(opts.PublicUrl.TrimEnd('/'))}";
        var workerKey = ResolveFirstWorkerKey(opts);
        if (!string.IsNullOrWhiteSpace(workerKey))
            yield return $"export EVENT_FORGE_WORKER_KEY={BashQuote(workerKey)}";
        foreach (var line in ForgeQueueCapabilityExportLines(provisionMode, wanEnabled, ltx23Enabled, musicEnabled))
            yield return line;
    }

    public static void ApplyEventForgeVastExtraEnv(
        Dictionary<string, string> env,
        EventForgeOptions opts,
        string? provisionMode = null,
        bool wanEnabled = true,
        bool ltx23Enabled = false,
        bool musicEnabled = true)
    {
        env["LOBO_GEN_QUEUE"] = "eventforge";
        env["EVENT_FORGE_URL"] = opts.PublicUrl.TrimEnd('/');
        var workerKey = ResolveFirstWorkerKey(opts);
        if (!string.IsNullOrWhiteSpace(workerKey))
            env["EVENT_FORGE_WORKER_KEY"] = workerKey;
        env["FORGE_QUEUE_CAPABILITY"] = GenQueueCapabilities.EnvValueForProvisionMode(
            provisionMode, wanEnabled, ltx23Enabled, musicEnabled);
    }

    public static IEnumerable<string> ForgeQueueCapabilityExportLines(
        string? provisionMode,
        bool wanEnabled = true,
        bool ltx23Enabled = false,
        bool musicEnabled = true)
    {
        var caps = GenQueueCapabilities.EnvValueForProvisionMode(
            provisionMode, wanEnabled, ltx23Enabled, musicEnabled);
        if (!string.IsNullOrWhiteSpace(caps))
            yield return $"export FORGE_QUEUE_CAPABILITY={BashQuote(caps)}";
    }

    public static string BuildEventForgeExtraEnvTemplate(EventForgeOptions opts, string? provisionMode = null)
    {
        var normMode = VastAiDiskRequirements.NormalizeMode(provisionMode ?? "all");
        var nativeLtx = VastAiDiskRequirements.IsNativeLtxMode(normMode);
        var wanEnabled = normMode is not "image" and not "music";
        var ltx23Enabled = nativeLtx || normMode is "all" or "both" or "ltx-native" or "ltx";
        var musicEnabled = normMode is not "image";
        var workerKey = ResolveFirstWorkerKey(opts);
        var lines = new List<string>
        {
            "# Vast.ai extra_env — EventForge worker transport",
            $"EVENT_FORGE_URL={opts.PublicUrl.TrimEnd('/')}",
        };
        if (!string.IsNullOrWhiteSpace(workerKey))
            lines.Add($"EVENT_FORGE_WORKER_KEY={workerKey}");
        lines.Add("LOBO_GEN_QUEUE=eventforge");
        var caps = GenQueueCapabilities.EnvValueForProvisionMode(
            normMode, wanEnabled, ltx23Enabled, musicEnabled);
        if (!string.IsNullOrWhiteSpace(caps))
            lines.Add($"FORGE_QUEUE_CAPABILITY={caps}");
        return string.Join("\n", lines);
    }

    public static string BuildManualProvisionCommand(
        EventForgeOptions opts,
        string instanceId,
        string mode,
        string workerSecret,
        string hubUrl,
        string baseUrl,
        string? hfToken = null)
    {
        var (scriptPrimary, scriptFallback) = ResolveBootstrapScriptBases(opts);
        var loboBaseUrl = (baseUrl ?? opts.AgentScriptBaseUrl).TrimEnd('/');
        hubUrl = (hubUrl ?? "wss://www.loboforge.com").Trim();
        var normMode = VastAiDiskRequirements.NormalizeMode(mode);
        var nativeLtx = VastAiDiskRequirements.IsNativeLtxMode(normMode);
        var nativeWan = VastAiDiskRequirements.IsNativeWanMode(normMode);
        var wanEnabled = nativeWan || normMode is not "image" and not "music";
        var ltx23Enabled = nativeLtx || normMode is "all" or "both" or "ltx-native" or "ltx";
        var musicEnabled = nativeWan ? false : normMode is not "image";
        var agentFile = nativeLtx ? "provision_ltx_native.sh" : nativeWan ? "provision_wan_native.sh" : "provision_worker.sh";
        var agentCurl = BashCurlAgentFile(scriptPrimary, scriptFallback, agentFile, "-");

        var sb = new System.Text.StringBuilder();
        sb.Append("LOBO_INSTANCE_ID=").Append(BashQuote(instanceId));
        sb.Append(" LOBO_SECRET=").Append(BashQuote(workerSecret));
        sb.Append(" LOBO_SERVER=").Append(BashQuote(hubUrl));
        sb.Append(" LOBO_BASE_URL=").Append(BashQuote(loboBaseUrl));
        foreach (var line in EventForgeOnstartExportLines(opts, normMode, wanEnabled, ltx23Enabled, musicEnabled))
            sb.Append(' ').Append(line.Replace("export ", "", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(hfToken))
            sb.Append(" HF_TOKEN=").Append(BashQuote(hfToken));
        if (nativeLtx)
        {
            sb.Append(" LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=0 LOBO_LTX23=1 LOBO_MUSIC=0");
            sb.Append(" MODE=ltx-native LOBO_MODE=ltx-native");
            sb.Append(" bash -c ").Append(BashQuote($"{agentCurl} | bash"));
        }
        else if (nativeWan)
        {
            sb.Append(" LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=1 LOBO_LTX23=0 LOBO_MUSIC=0 LOBO_UNLOAD_MODELS=0");
            sb.Append(" MODE=wan-native LOBO_MODE=wan-native WAN_MODEL_ROOT=/workspace/wan-models WAN_REPO=/workspace/Wan2.2");
            sb.Append(" bash -c ").Append(BashQuote($"{agentCurl} | bash"));
        }
        else
        {
            sb.Append(' ').Append(agentCurl)
                .Append(" | bash -s -- --mode ").Append(BashQuote(normMode));
        }
        return sb.ToString();
    }

    /// <summary>Production worker bearer token for Vast onstart (not dev placeholder from appsettings.json).</summary>
    private static string? ResolveFirstWorkerKey(EventForgeOptions opts)
    {
        foreach (var k in opts.WorkerKeys.Keys)
        {
            if (string.IsNullOrWhiteSpace(k) || k == "wrath-worker-key")
                continue;
            return k;
        }
        return opts.WorkerKeys.Keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
    }
}
