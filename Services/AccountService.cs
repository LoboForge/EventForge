using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EventForge.Services;

public sealed record LoginResult(string SessionToken, AccountRecord Account);

public sealed class AccountService
{
    private const int PasswordIterations = 210_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private readonly AccountStore _store;

    public AccountService(AccountStore store)
    {
        _store = store;
    }

    public async Task<AccountRecord?> RegisterAsync(
        string email,
        string password,
        string? company,
        CancellationToken ct)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedCompany = string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        return await _store.CreateAccountAsync(
            Guid.NewGuid().ToString("N"),
            normalizedEmail,
            HashPassword(password),
            normalizedCompany,
            DateTimeOffset.UtcNow,
            ct);
    }

    public async Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var account = await _store.GetByEmailAsync(email.Trim().ToLowerInvariant(), ct);
        if (account == null || !VerifyPassword(password, account.PasswordHash))
            return null;

        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await _store.CreateSessionAsync(
            HashToken(token),
            account.AccountId,
            DateTimeOffset.UtcNow.Add(SessionLifetime),
            ct);
        return new LoginResult(token, account);
    }

    public Task<AccountRecord?> AuthenticateSessionAsync(string token, CancellationToken ct) =>
        _store.GetBySessionAsync(HashToken(token), DateTimeOffset.UtcNow, ct);

    public async Task<CapacityRequestRecord> CreateCapacityRequestAsync(
        string? accountId,
        string email,
        string? company,
        string? name,
        IReadOnlyList<string> models,
        long estimatedJobs,
        string? notes,
        string preferredPayment,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new CapacityRequestRecord(
            Guid.NewGuid().ToString("N"),
            accountId,
            email.Trim().ToLowerInvariant(),
            NormalizeOptional(company),
            NormalizeOptional(name),
            JsonSerializer.Serialize(models.Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            estimatedJobs,
            NormalizeOptional(notes),
            preferredPayment.Trim().ToLowerInvariant(),
            "received",
            null,
            null,
            null,
            null,
            now,
            now);
        await _store.CreateCapacityRequestAsync(request, ct);
        return request;
    }

    public Task<CapacityApproval?> ApproveCapacityRequestAsync(
        string requestId,
        long credits,
        CancellationToken ct) =>
        _store.ApproveCapacityRequestAsync(
            requestId,
            credits,
            () => Guid.NewGuid().ToString("N"),
            () => HashPassword(Base64UrlEncode(RandomNumberGenerator.GetBytes(32))),
            CreateApiKey,
            ct);

    public static string CreateApiKey() =>
        "efk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        return $"pbkdf2-sha256.{PasswordIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var iterations)
            || iterations < 100_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
