namespace EventForge.Auth;

public interface IApiKeyValidator
{
    bool TryValidate(string? apiKey, out string appId);
}
