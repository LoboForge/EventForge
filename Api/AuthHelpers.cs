namespace EventForge.Api;

using EventForge.Auth;

public static class AuthHelpers
{
    public static bool TryReadApiKey(HttpContext ctx, out string token)
    {
        token = (ctx.Request.Query["token"].ToString() ?? "").Trim();
        if (token.Length > 0) return true;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = auth["Bearer ".Length..].Trim();
            return token.Length > 0;
        }
        return false;
    }

    public static bool TryReadOpsKey(HttpContext ctx, out string token)
    {
        var header = ctx.Request.Headers["X-EventForge-Ops-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            token = header.Trim();
            return true;
        }
        return TryReadApiKey(ctx, out token);
    }

    public static bool TryAuthorizeOps(HttpContext ctx, IOpsKeyValidator opsAuth, out bool authorized)
    {
        authorized = false;
        if (!TryReadOpsKey(ctx, out var token)) return false;
        if (!opsAuth.TryValidate(token, out var valid) || !valid) return false;
        authorized = true;
        return true;
    }
}
