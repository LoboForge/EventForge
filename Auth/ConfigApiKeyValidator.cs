using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Auth;

public sealed class ConfigApiKeyValidator : IApiKeyValidator
{
    private readonly IReadOnlyDictionary<string, string> _keys;

    public ConfigApiKeyValidator(IOptions<EventForgeOptions> options)
    {
        _keys = options.Value.ApiKeys;
    }

    public bool TryValidate(string? apiKey, out string appId)
    {
        appId = "";
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        return _keys.TryGetValue(apiKey.Trim(), out appId!) && !string.IsNullOrWhiteSpace(appId);
    }
}
