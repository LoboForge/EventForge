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

        opts.Payments.PayPal.ClientId = FirstNonEmpty(
            opts.Payments.PayPal.ClientId,
            cfg["Payments:PayPal:ClientId"],
            configuration["Payments:PayPal:ClientId"]);
        opts.Payments.PayPal.Secret = FirstNonEmpty(
            opts.Payments.PayPal.Secret,
            cfg["Payments:PayPal:Secret"],
            configuration["Payments:PayPal:Secret"]);
        opts.Payments.PayPal.Mode = FirstNonEmpty(
            cfg["Payments:PayPal:Mode"],
            configuration["Payments:PayPal:Mode"],
            opts.Payments.PayPal.Mode,
            "sandbox");
        opts.Payments.NowPayments.ApiKey = FirstNonEmpty(
            opts.Payments.NowPayments.ApiKey,
            cfg["Payments:NowPayments:ApiKey"],
            configuration["Payments:NowPayments:ApiKey"]);
        opts.Payments.NowPayments.IpnSecret = FirstNonEmpty(
            opts.Payments.NowPayments.IpnSecret,
            cfg["Payments:NowPayments:IpnSecret"],
            configuration["Payments:NowPayments:IpnSecret"]);
        opts.Payments.Wire.BankName = FirstNonEmpty(opts.Payments.Wire.BankName, cfg["Payments:Wire:BankName"]);
        opts.Payments.Wire.AccountName = FirstNonEmpty(opts.Payments.Wire.AccountName, cfg["Payments:Wire:AccountName"]);
        opts.Payments.Wire.AccountNumber = FirstNonEmpty(opts.Payments.Wire.AccountNumber, cfg["Payments:Wire:AccountNumber"]);
        opts.Payments.Wire.RoutingNumber = FirstNonEmpty(opts.Payments.Wire.RoutingNumber, cfg["Payments:Wire:RoutingNumber"]);
        opts.Payments.Wire.Swift = FirstNonEmpty(opts.Payments.Wire.Swift, cfg["Payments:Wire:Swift"]);
        opts.Payments.Wire.Iban = FirstNonEmpty(opts.Payments.Wire.Iban, cfg["Payments:Wire:Iban"]);
        opts.Payments.Wire.ReferenceTemplate = FirstNonEmpty(
            cfg["Payments:Wire:ReferenceTemplate"],
            opts.Payments.Wire.ReferenceTemplate,
            "EF-{request_id}");
        opts.Payments.Wire.Notes = FirstNonEmpty(opts.Payments.Wire.Notes, cfg["Payments:Wire:Notes"]);
        opts.Payments.Monero.ReceiveAddress = FirstNonEmpty(
            opts.Payments.Monero.ReceiveAddress,
            cfg["Payments:Monero:ReceiveAddress"]);

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
