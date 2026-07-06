namespace EventForge.Auth;

public interface IOpsKeyValidator
{
    bool TryValidate(string token, out bool valid);
}
