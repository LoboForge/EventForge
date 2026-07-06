using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Auth;

public sealed class ConfigOpsKeyValidator : IOpsKeyValidator
{
    private readonly EventForgeOptions _opts;

    public ConfigOpsKeyValidator(IOptions<EventForgeOptions> options) =>
        _opts = options.Value;

    public bool TryValidate(string token, out bool valid)
    {
        valid = false;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var expected = (_opts.OpsKey ?? "").Trim();
        if (expected.Length == 0) return false;
        valid = string.Equals(expected, token.Trim(), StringComparison.Ordinal);
        return valid;
    }
}
