using System.Collections.Concurrent;
using EventForge.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EventForge.Services;

public sealed record AccountRecord(
    string AccountId,
    string Email,
    string PasswordHash,
    string? Company,
    long Credits,
    string? ApiKey,
    DateTimeOffset CreatedAt);

public sealed record PurchaseRecord(
    string CheckoutId,
    string AccountId,
    string PlanId,
    string Provider,
    string? ProviderOrderId,
    long Credits,
    decimal PriceUsd,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record PurchaseCompletion(long Credits, string ApiKey, bool NewlyCompleted);

public sealed record CapacityRequestRecord(
    string RequestId,
    string? AccountId,
    string Email,
    string? Company,
    string? Name,
    string ModelsJson,
    long EstimatedJobs,
    string? Notes,
    string PreferredPayment,
    string Status,
    string? PaymentMethod,
    string? PaymentInstructionsJson,
    string? RejectionReason,
    long? CreditsGranted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CapacityApproval(
    CapacityRequestRecord Request,
    string AccountId,
    string ApiKey,
    long Credits,
    bool AccountCreated);

/// <summary>SQLite-backed account, session, purchase, and capacity-request store.</summary>
public sealed class AccountStore
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, string> _accountByApiKey = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _initialized;

    public AccountStore(IOptions<EventForgeOptions> options)
    {
        var path = Path.GetFullPath(options.Value.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await EnsureSchemaAsync(conn, ct);
            await ReloadApiKeysAsync(conn, ct);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryValidateApiKey(string apiKey, out string accountId) =>
        _accountByApiKey.TryGetValue(apiKey, out accountId!);

    public async Task<AccountRecord?> CreateAccountAsync(
        string accountId,
        string email,
        string passwordHash,
        string? company,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts (
                  account_id, email, password_hash, company, credits, api_key, created_at
                ) VALUES (
                  $id, $email, $password, $company, 0, NULL, $created
                )
                """;
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.Parameters.AddWithValue("$email", email);
            cmd.Parameters.AddWithValue("$password", passwordHash);
            cmd.Parameters.AddWithValue("$company", (object?)company ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return null;
            }
            return new AccountRecord(accountId, email, passwordHash, company, 0, null, createdAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AccountRecord?> GetByEmailAsync(string email, CancellationToken ct) =>
        ReadAccountAsync("email = $value", email, ct);

    public Task<AccountRecord?> GetByIdAsync(string accountId, CancellationToken ct) =>
        ReadAccountAsync("account_id = $value", accountId, ct);

    public async Task CreateSessionAsync(
        string tokenHash,
        string accountId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO account_sessions (token_hash, account_id, expires_at, created_at)
                VALUES ($token, $account, $expires, $created)
                """;
            cmd.Parameters.AddWithValue("$token", tokenHash);
            cmd.Parameters.AddWithValue("$account", accountId);
            cmd.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountRecord?> GetBySessionAsync(
        string tokenHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT a.account_id, a.email, a.password_hash, a.company, a.credits, a.api_key, a.created_at
                FROM account_sessions s
                JOIN accounts a ON a.account_id = s.account_id
                WHERE s.token_hash = $token AND s.expires_at > $now
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$token", tokenHash);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadAccount(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreatePurchaseAsync(PurchaseRecord purchase, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO account_purchases (
                  checkout_id, account_id, plan_id, provider, provider_order_id,
                  credits, price_usd, status, created_at, completed_at
                ) VALUES (
                  $checkout, $account, $plan, $provider, $providerOrder,
                  $credits, $price, $status, $created, $completed
                )
                """;
            BindPurchase(cmd, purchase);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPurchaseProviderOrderAsync(
        string checkoutId,
        string providerOrderId,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE account_purchases
                SET provider_order_id = $providerOrder
                WHERE checkout_id = $checkout AND status = 'pending'
                """;
            cmd.Parameters.AddWithValue("$providerOrder", providerOrderId);
            cmd.Parameters.AddWithValue("$checkout", checkoutId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PurchaseRecord?> GetPurchaseAsync(string checkoutId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT checkout_id, account_id, plan_id, provider, provider_order_id,
                       credits, price_usd, status, created_at, completed_at
                FROM account_purchases
                WHERE checkout_id = $checkout
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$checkout", checkoutId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadPurchase(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PurchaseCompletion?> CompletePurchaseAsync(
        string checkoutId,
        string? expectedAccountId,
        Func<string> apiKeyFactory,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            PurchaseRecord? purchase;
            await using (var readPurchase = conn.CreateCommand())
            {
                readPurchase.Transaction = tx;
                readPurchase.CommandText = """
                    SELECT checkout_id, account_id, plan_id, provider, provider_order_id,
                           credits, price_usd, status, created_at, completed_at
                    FROM account_purchases WHERE checkout_id = $checkout LIMIT 1
                    """;
                readPurchase.Parameters.AddWithValue("$checkout", checkoutId);
                await using var reader = await readPurchase.ExecuteReaderAsync(ct);
                purchase = await reader.ReadAsync(ct) ? ReadPurchase(reader) : null;
            }

            if (purchase == null
                || (expectedAccountId != null
                    && !string.Equals(purchase.AccountId, expectedAccountId, StringComparison.Ordinal)))
            {
                return null;
            }

            AccountRecord account;
            await using (var readAccount = conn.CreateCommand())
            {
                readAccount.Transaction = tx;
                readAccount.CommandText = """
                    SELECT account_id, email, password_hash, company, credits, api_key, created_at
                    FROM accounts WHERE account_id = $account LIMIT 1
                    """;
                readAccount.Parameters.AddWithValue("$account", purchase.AccountId);
                await using var reader = await readAccount.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                account = ReadAccount(reader);
            }

            if (string.Equals(purchase.Status, "completed", StringComparison.Ordinal))
            {
                await tx.CommitAsync(ct);
                return account.ApiKey == null
                    ? null
                    : new PurchaseCompletion(account.Credits, account.ApiKey, false);
            }

            var apiKey = account.ApiKey ?? apiKeyFactory();
            var newCredits = checked(account.Credits + purchase.Credits);
            await using (var updateAccount = conn.CreateCommand())
            {
                updateAccount.Transaction = tx;
                updateAccount.CommandText = """
                    UPDATE accounts SET credits = $credits, api_key = $apiKey
                    WHERE account_id = $account
                    """;
                updateAccount.Parameters.AddWithValue("$credits", newCredits);
                updateAccount.Parameters.AddWithValue("$apiKey", apiKey);
                updateAccount.Parameters.AddWithValue("$account", purchase.AccountId);
                await updateAccount.ExecuteNonQueryAsync(ct);
            }
            await using (var updatePurchase = conn.CreateCommand())
            {
                updatePurchase.Transaction = tx;
                updatePurchase.CommandText = """
                    UPDATE account_purchases SET status = 'completed', completed_at = $completed
                    WHERE checkout_id = $checkout AND status = 'pending'
                    """;
                updatePurchase.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
                updatePurchase.Parameters.AddWithValue("$checkout", checkoutId);
                await updatePurchase.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _accountByApiKey[apiKey] = purchase.AccountId;
            return new PurchaseCompletion(newCredits, apiKey, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateCapacityRequestAsync(CapacityRequestRecord request, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO capacity_requests (
                  request_id, account_id, email, company, name, models_json, estimated_jobs,
                  notes, preferred_payment, status, payment_method, payment_instructions_json,
                  rejection_reason, credits_granted, created_at, updated_at
                ) VALUES (
                  $id, $account, $email, $company, $name, $models, $jobs,
                  $notes, $preferred, $status, $method, $instructions,
                  $rejection, $credits, $created, $updated
                )
                """;
            BindCapacityRequest(cmd, request);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CapacityRequestRecord>> ListCapacityRequestsAsync(
        string? status,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT request_id, account_id, email, company, name, models_json, estimated_jobs,
                       notes, preferred_payment, status, payment_method, payment_instructions_json,
                       rejection_reason, credits_granted, created_at, updated_at
                FROM capacity_requests
                WHERE $status IS NULL OR status = $status
                ORDER BY created_at DESC
                """;
            cmd.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim().ToLowerInvariant());
            var rows = new List<CapacityRequestRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadCapacityRequest(reader));
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CapacityRequestRecord?> GetCapacityRequestAsync(string requestId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT request_id, account_id, email, company, name, models_json, estimated_jobs,
                       notes, preferred_payment, status, payment_method, payment_instructions_json,
                       rejection_reason, credits_granted, created_at, updated_at
                FROM capacity_requests WHERE request_id = $id LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", requestId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadCapacityRequest(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CapacityRequestRecord?> SetCapacityPaymentInstructionsAsync(
        string requestId,
        string method,
        string instructionsJson,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE capacity_requests
                SET payment_method = $method, payment_instructions_json = $instructions,
                    status = CASE WHEN status = 'received' THEN 'payment_pending' ELSE status END,
                    updated_at = $updated
                WHERE request_id = $id AND status NOT IN ('approved', 'rejected')
                """;
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$instructions", instructionsJson);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", requestId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return null;
        }
        finally
        {
            _gate.Release();
        }
        return await GetCapacityRequestAsync(requestId, ct);
    }

    public async Task<CapacityRequestRecord?> RejectCapacityRequestAsync(
        string requestId,
        string reason,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE capacity_requests
                SET status = 'rejected', rejection_reason = $reason, updated_at = $updated
                WHERE request_id = $id AND status <> 'approved'
                """;
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", requestId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return null;
        }
        finally
        {
            _gate.Release();
        }
        return await GetCapacityRequestAsync(requestId, ct);
    }

    public async Task<CapacityApproval?> ApproveCapacityRequestAsync(
        string requestId,
        long credits,
        Func<string> accountIdFactory,
        Func<string> passwordHashFactory,
        Func<string> apiKeyFactory,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            CapacityRequestRecord? request;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT request_id, account_id, email, company, name, models_json, estimated_jobs,
                           notes, preferred_payment, status, payment_method, payment_instructions_json,
                           rejection_reason, credits_granted, created_at, updated_at
                    FROM capacity_requests WHERE request_id = $id LIMIT 1
                    """;
                read.Parameters.AddWithValue("$id", requestId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                request = await reader.ReadAsync(ct) ? ReadCapacityRequest(reader) : null;
            }
            if (request == null || request.Status == "rejected") return null;

            AccountRecord? account = null;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT account_id, email, password_hash, company, credits, api_key, created_at
                    FROM accounts
                    WHERE account_id = $account OR email = $email
                    ORDER BY CASE WHEN account_id = $account THEN 0 ELSE 1 END
                    LIMIT 1
                    """;
                read.Parameters.AddWithValue("$account", (object?)request.AccountId ?? DBNull.Value);
                read.Parameters.AddWithValue("$email", request.Email);
                await using var reader = await read.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) account = ReadAccount(reader);
            }

            if (request.Status == "approved")
            {
                await tx.CommitAsync(ct);
                return account?.ApiKey == null
                    ? null
                    : new CapacityApproval(request, account.AccountId, account.ApiKey, account.Credits, false);
            }

            var accountCreated = account == null;
            if (account == null)
            {
                var accountId = accountIdFactory();
                var created = DateTimeOffset.UtcNow;
                await using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO accounts (account_id, email, password_hash, company, credits, api_key, created_at)
                    VALUES ($id, $email, $password, $company, 0, NULL, $created)
                    """;
                insert.Parameters.AddWithValue("$id", accountId);
                insert.Parameters.AddWithValue("$email", request.Email);
                insert.Parameters.AddWithValue("$password", passwordHashFactory());
                insert.Parameters.AddWithValue("$company", (object?)request.Company ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created", created.ToString("O"));
                await insert.ExecuteNonQueryAsync(ct);
                account = new AccountRecord(accountId, request.Email, "", request.Company, 0, null, created);
            }

            var apiKey = account.ApiKey ?? apiKeyFactory();
            var totalCredits = checked(account.Credits + credits);
            await using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE accounts SET credits = $credits, api_key = $key WHERE account_id = $id";
                update.Parameters.AddWithValue("$credits", totalCredits);
                update.Parameters.AddWithValue("$key", apiKey);
                update.Parameters.AddWithValue("$id", account.AccountId);
                await update.ExecuteNonQueryAsync(ct);
            }
            var now = DateTimeOffset.UtcNow;
            await using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE capacity_requests
                    SET account_id = $account, status = 'approved', credits_granted = $credits,
                        rejection_reason = NULL, updated_at = $updated
                    WHERE request_id = $id
                    """;
                update.Parameters.AddWithValue("$account", account.AccountId);
                update.Parameters.AddWithValue("$credits", credits);
                update.Parameters.AddWithValue("$updated", now.ToString("O"));
                update.Parameters.AddWithValue("$id", requestId);
                await update.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            _accountByApiKey[apiKey] = account.AccountId;
            var approved = request with
            {
                AccountId = account.AccountId,
                Status = "approved",
                CreditsGranted = credits,
                RejectionReason = null,
                UpdatedAt = now,
            };
            return new CapacityApproval(approved, account.AccountId, apiKey, totalCredits, accountCreated);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AccountRecord?> ReadAccountAsync(
        string predicate,
        string value,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT account_id, email, password_hash, company, credits, api_key, created_at
                FROM accounts WHERE {predicate} LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$value", value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadAccount(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized) == 1) return;
        await InitializeAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
              account_id TEXT PRIMARY KEY,
              email TEXT NOT NULL UNIQUE,
              password_hash TEXT NOT NULL,
              company TEXT,
              credits INTEGER NOT NULL DEFAULT 0,
              api_key TEXT UNIQUE,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS account_sessions (
              token_hash TEXT PRIMARY KEY,
              account_id TEXT NOT NULL REFERENCES accounts(account_id) ON DELETE CASCADE,
              expires_at TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_account_sessions_expiry
              ON account_sessions(expires_at);
            CREATE TABLE IF NOT EXISTS account_purchases (
              checkout_id TEXT PRIMARY KEY,
              account_id TEXT NOT NULL REFERENCES accounts(account_id),
              plan_id TEXT NOT NULL,
              provider TEXT NOT NULL,
              provider_order_id TEXT,
              credits INTEGER NOT NULL,
              price_usd TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              completed_at TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_account_purchases_provider_order
              ON account_purchases(provider, provider_order_id)
              WHERE provider_order_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS capacity_requests (
              request_id TEXT PRIMARY KEY,
              account_id TEXT REFERENCES accounts(account_id),
              email TEXT NOT NULL,
              company TEXT,
              name TEXT,
              models_json TEXT NOT NULL,
              estimated_jobs INTEGER NOT NULL,
              notes TEXT,
              preferred_payment TEXT NOT NULL,
              status TEXT NOT NULL,
              payment_method TEXT,
              payment_instructions_json TEXT,
              rejection_reason TEXT,
              credits_granted INTEGER,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_capacity_requests_status_created
              ON capacity_requests(status, created_at DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ReloadApiKeysAsync(SqliteConnection conn, CancellationToken ct)
    {
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT api_key, account_id FROM accounts WHERE api_key IS NOT NULL";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            loaded[reader.GetString(0)] = reader.GetString(1);
        _accountByApiKey.Clear();
        foreach (var (key, account) in loaded) _accountByApiKey[key] = account;
    }

    private static void BindPurchase(SqliteCommand cmd, PurchaseRecord p)
    {
        cmd.Parameters.AddWithValue("$checkout", p.CheckoutId);
        cmd.Parameters.AddWithValue("$account", p.AccountId);
        cmd.Parameters.AddWithValue("$plan", p.PlanId);
        cmd.Parameters.AddWithValue("$provider", p.Provider);
        cmd.Parameters.AddWithValue("$providerOrder", (object?)p.ProviderOrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$credits", p.Credits);
        cmd.Parameters.AddWithValue("$price", p.PriceUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$status", p.Status);
        cmd.Parameters.AddWithValue("$created", p.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", p.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static AccountRecord ReadAccount(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6)));

    private static PurchaseRecord ReadPurchase(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5),
        decimal.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)));

    private static void BindCapacityRequest(SqliteCommand cmd, CapacityRequestRecord r)
    {
        cmd.Parameters.AddWithValue("$id", r.RequestId);
        cmd.Parameters.AddWithValue("$account", (object?)r.AccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$email", r.Email);
        cmd.Parameters.AddWithValue("$company", (object?)r.Company ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)r.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$models", r.ModelsJson);
        cmd.Parameters.AddWithValue("$jobs", r.EstimatedJobs);
        cmd.Parameters.AddWithValue("$notes", (object?)r.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preferred", r.PreferredPayment);
        cmd.Parameters.AddWithValue("$status", r.Status);
        cmd.Parameters.AddWithValue("$method", (object?)r.PaymentMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$instructions", (object?)r.PaymentInstructionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rejection", (object?)r.RejectionReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$credits", (object?)r.CreditsGranted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", r.UpdatedAt.ToString("O"));
    }

    private static CapacityRequestRecord ReadCapacityRequest(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        DateTimeOffset.Parse(reader.GetString(14)),
        DateTimeOffset.Parse(reader.GetString(15)));
}
