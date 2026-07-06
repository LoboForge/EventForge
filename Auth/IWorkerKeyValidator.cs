namespace EventForge.Auth;

public interface IWorkerKeyValidator
{
    bool TryValidate(string? apiKey, out string workerId);
}
