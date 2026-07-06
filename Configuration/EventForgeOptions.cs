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

public sealed class VastAiOptions
{
    public string ApiKey { get; set; } = "";
}
