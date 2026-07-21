namespace EventForge.Configuration;

public sealed class EventForgeOptions
{
    public const string Section = "EventForge";

    public string ListenUrl { get; set; } = "http://localhost:8090";
    public string AwsRegion { get; set; } = "us-east-2";
    /// <summary>Shared Event Bus ingress SQS URL (fq-events-bus-ingress).</summary>
    public string IngressQueueUrl { get; set; } = "";
    public string SqlitePath { get; set; } = "event-forge.db";
    public int EventRetentionDays { get; set; } = 7;
    /// <summary>How long completed/failed jobs stay in the in-memory cache (SQLite retains longer).</summary>
    public int JobCacheHours { get; set; } = 24;
    public int PingIntervalSeconds { get; set; } = 30;
    public int LeaseSeconds { get; set; } = 900;
    public int FlushIntervalSeconds { get; set; } = 3;
    public int MaxConcurrentUploads { get; set; } = 3;
    public string LocalArtifactDir { get; set; } = "artifacts";
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> WorkerKeys { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Ops dashboard API key (header X-EventForge-Ops-Key or Authorization Bearer).</summary>
    public string OpsKey { get; set; } = "";
    /// <summary>Public EventForge URL for worker bootstrap (EVENT_FORGE_URL).</summary>
    public string PublicUrl { get; set; } = "https://eventforge.loboforge.com";
    public string WorkerSecret { get; set; } = "";
    public string HuggingFaceToken { get; set; } = "";
    public string AgentScriptBaseUrl { get; set; } = "https://www.loboforge.com";
    public VastAiOptions VastAi { get; set; } = new();
    public S3StoreOptions S3 { get; set; } = new();
    public ArtifactS3Options Artifacts { get; set; } = new();
    public LoraAssetOptions LoraAssets { get; set; } = new();
    public PaymentOptions Payments { get; set; } = new();
}

public sealed class S3StoreOptions
{
    public bool Enabled { get; set; }
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "event-forge/store.db";
    public string Region { get; set; } = "us-east-2";
    public int BackupIntervalMinutes { get; set; } = 5;
    public bool LoadOnStartup { get; set; } = true;
}

public sealed class ArtifactS3Options
{
    public bool Enabled { get; set; }
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "event-forge/jobs";
    public string Region { get; set; } = "us-east-2";
}

/// <summary>App-scoped LoRA library (S3 or local). When Bucket is empty, inherits Artifacts bucket.</summary>
public sealed class LoraAssetOptions
{
    /// <summary>When true (and a bucket is available), use S3 + presigned PUT. Otherwise local disk + proxy upload.</summary>
    public bool Enabled { get; set; }
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "event-forge/loras";
    public string Region { get; set; } = "us-east-2";
    public string LocalDir { get; set; } = "";
    /// <summary>Max upload size in bytes (default 5 GiB).</summary>
    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int PresignTtlMinutes { get; set; } = 60;
    /// <summary>Minimum object size to mark ready (default 1 MiB).</summary>
    public long MinReadyBytes { get; set; } = 1_000_000;
}

public sealed class VastAiOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed class PaymentOptions
{
    public PayPalOptions PayPal { get; set; } = new();
    public NowPaymentsOptions NowPayments { get; set; } = new();
    public WireOptions Wire { get; set; } = new();
    public MoneroOptions Monero { get; set; } = new();
    public PlanCatalogOptions Plans { get; set; } = new();
}

public sealed class PayPalOptions
{
    public string ClientId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Mode { get; set; } = "sandbox";
    public string InvoiceNote { get; set; } = "EventForge prepaid capacity";
}

public sealed class NowPaymentsOptions
{
    public string ApiKey { get; set; } = "";
    public string IpnSecret { get; set; } = "";
}

public sealed class WireOptions
{
    public string BankName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string RoutingNumber { get; set; } = "";
    public string Swift { get; set; } = "";
    public string Iban { get; set; } = "";
    public string ReferenceTemplate { get; set; } = "EF-{request_id}";
    public string Notes { get; set; } = "";
}

public sealed class MoneroOptions
{
    public bool Enabled { get; set; }
    public string ReceiveAddress { get; set; } = "";
}

public sealed class PlanCatalogOptions
{
    public PlanOptions Starter { get; set; } = new()
    {
        Name = "Starter",
        Description = "An affordable starting point for production GPU generation.",
        PriceUsd = 29m,
        Credits = 1_000,
        Features = ["1,000 generation credits", "All public models", "Custom LoRA support"],
    };

    public PlanOptions Pro { get; set; } = new()
    {
        Name = "Pro",
        Description = "More capacity for teams and growing applications.",
        PriceUsd = 99m,
        Credits = 4_000,
        Features = ["4,000 generation credits", "All public models", "Custom LoRA support"],
    };

    public PlanOptions Scale { get; set; } = new()
    {
        Name = "Scale",
        Description = "High-volume credits for established production workloads.",
        PriceUsd = 299m,
        Credits = 14_000,
        Features = ["14,000 generation credits", "All public models", "Custom LoRA support"],
    };
}

public sealed class PlanOptions
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal PriceUsd { get; set; }
    public long Credits { get; set; }
    public List<string> Features { get; set; } = [];
}
