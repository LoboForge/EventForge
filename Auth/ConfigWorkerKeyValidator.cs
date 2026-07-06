using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Auth;

public sealed class ConfigWorkerKeyValidator : IWorkerKeyValidator
{
    private readonly IReadOnlyDictionary<string, string> _keys;

    public ConfigWorkerKeyValidator(IOptions<EventForgeOptions> options)
    {
        _keys = options.Value.WorkerKeys;
    }

    public bool TryValidate(string? apiKey, out string workerId)
    {
        workerId = "";
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        return _keys.TryGetValue(apiKey.Trim(), out workerId!) && !string.IsNullOrWhiteSpace(workerId);
    }
}
