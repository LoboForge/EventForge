namespace EventForge.Configuration;

/// <summary>
/// Maps shared <c>APP_SECRETS_JSON</c> (LoboForge-shaped) into <see cref="EventForgeOptions"/>.
/// EventForge ECS uses the same secret as LoboForge; Vast/HF/worker secrets live at the root.
/// </summary>
public static class EventForgeSecretsBinder
{
    public static void Apply(IConfiguration configuration, EventForgeOptions opts)
    {
        var cfg = configuration.GetSection(EventForgeOptions.Section);

        var apiKey = cfg["ApiKey"];
        var workerKey = cfg["WorkerKey"];
        var opsKey = cfg["OpsKey"];
        var appId = FirstNonEmpty(cfg["AppId"], "loboforge");
        if (!string.IsNullOrWhiteSpace(apiKey))
            opts.ApiKeys[apiKey.Trim()] = appId;
        if (!string.IsNullOrWhiteSpace(workerKey))
            opts.WorkerKeys[workerKey.Trim()] = "wrath";
        if (!string.IsNullOrWhiteSpace(opsKey))
            opts.OpsKey = opsKey.Trim();

        opts.PublicUrl = FirstNonEmpty(
            opts.PublicUrl,
            cfg["PublicUrl"],
            cfg["BaseUrl"],
            "https://eventforge.loboforge.com");

        opts.VastAi.ApiKey = FirstNonEmpty(
            opts.VastAi.ApiKey,
            cfg["VastAi:ApiKey"],
            configuration["VastAi:ApiKey"]);

        opts.WorkerSecret = FirstNonEmpty(
            opts.WorkerSecret,
            cfg["WorkerSecret"],
            configuration["Workers:Secret"],
            configuration["LoboForge:WorkersSecret"]);

        opts.HuggingFaceToken = FirstNonEmpty(
            opts.HuggingFaceToken,
            cfg["HuggingFaceToken"],
            configuration["HuggingFace:Token"]);

        var agentBase = FirstNonEmpty(cfg["AgentScriptBaseUrl"], configuration["LoboForge:BaseUrl"]);
        if (!string.IsNullOrWhiteSpace(agentBase))
            opts.AgentScriptBaseUrl = agentBase.Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }
}
