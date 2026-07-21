using EventForge.Configuration;
using EventForge.Services;
using Microsoft.Extensions.Options;

namespace EventForge.Auth;

public sealed class ConfigApiKeyValidator : IApiKeyValidator
{
    private readonly IReadOnlyDictionary<string, string> _keys;
    private readonly AccountStore _accounts;

    public ConfigApiKeyValidator(IOptions<EventForgeOptions> options, AccountStore accounts)
    {
        _keys = options.Value.ApiKeys;
        _accounts = accounts;
    }

    public bool TryValidate(string? apiKey, out string appId)
    {
        appId = "";
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var key = apiKey.Trim();
        return (_keys.TryGetValue(key, out appId!) && !string.IsNullOrWhiteSpace(appId))
               || _accounts.TryValidateApiKey(key, out appId);
    }
}
