using System.Text.Json.Serialization;

namespace EventForge.VastAi;

// Wire-shape DTOs for the Vast.ai HTTP API. JsonPropertyName attributes match
// vast.ai's exact field names so we can deserialize their responses as-is.
// Field set kept narrow: just what the admin UI needs to render + what the
// rent flow needs to submit. Add more when we surface them.

public class VastOfferList
{
    [JsonPropertyName("offers")] public List<VastOffer> Offers { get; set; } = new();
}

public class VastOffer
{
    [JsonPropertyName("id")]                public long    Id            { get; set; }
    [JsonPropertyName("machine_id")]        public long    MachineId     { get; set; }
    [JsonPropertyName("gpu_name")]          public string  GpuName       { get; set; } = "";
    [JsonPropertyName("num_gpus")]          public int     NumGpus       { get; set; }
    [JsonPropertyName("gpu_ram")]           public long    GpuRamMb      { get; set; }
    [JsonPropertyName("dph_total")]         public decimal DphTotal      { get; set; }
    [JsonPropertyName("reliability2")]      public decimal Reliability   { get; set; }
    [JsonPropertyName("dlperf")]            public decimal DlPerf        { get; set; }
    [JsonPropertyName("verified")]          public bool    Verified      { get; set; }
    [JsonPropertyName("rentable")]          public bool    Rentable      { get; set; }
    [JsonPropertyName("cpu_cores")]         public int     CpuCores      { get; set; }
    [JsonPropertyName("cpu_ram")]           public long    CpuRamMb      { get; set; }
    [JsonPropertyName("disk_space")]        public decimal DiskSpace     { get; set; }
    [JsonPropertyName("inet_up")]           public decimal InetUpMbps    { get; set; }
    [JsonPropertyName("inet_down")]         public decimal InetDownMbps  { get; set; }
    [JsonPropertyName("geolocation")]       public string  Geolocation   { get; set; } = "";
    [JsonPropertyName("cuda_max_good")]     public decimal CudaMaxGood   { get; set; }
}

public class VastAccount
{
    [JsonPropertyName("credit")]            public decimal Credit        { get; set; }
    [JsonPropertyName("email")]             public string  Email         { get; set; } = "";
    [JsonPropertyName("balance")]           public decimal Balance       { get; set; }
}

public class VastInstanceList
{
    [JsonPropertyName("instances")] public List<VastInstance> Instances { get; set; } = new();
}

public class VastInstance
{
    [JsonPropertyName("id")]               public long    Id              { get; set; }
    [JsonPropertyName("machine_id")]       public long    MachineId       { get; set; }
    [JsonPropertyName("actual_status")]    public string  ActualStatus    { get; set; } = "";
    [JsonPropertyName("intended_status")]  public string  IntendedStatus  { get; set; } = "";
    [JsonPropertyName("gpu_name")]         public string  GpuName         { get; set; } = "";
    [JsonPropertyName("dph_total")]        public decimal DphTotal        { get; set; }
    [JsonPropertyName("public_ipaddr")]    public string  PublicIp        { get; set; } = "";
    [JsonPropertyName("ssh_host")]         public string  SshHost         { get; set; } = "";
    [JsonPropertyName("ssh_port")]         public int     SshPort         { get; set; }
    [JsonPropertyName("label")]            public string  Label           { get; set; } = "";
}

public class VastOfferFilter
{
    public int     MinGpuRamGb     { get; set; } = 16;
    public decimal MaxDollarsPerHr { get; set; } = 0.75m;
    public decimal MinReliability  { get; set; } = 0.95m;
    // REWRITE_GOTCHAS: "Disk param sent but host can't allocate" — vast.ai
    // hosts have per-container disk caps; cheap V100 boxes are commonly ~32GB.
    // The request-side disk knob is honored only up to the host's quota, so
    // we MUST filter by host disk floor or we'll keep matching tiny boxes.
    public int     MinHostDiskGb   { get; set; } = 50;
    public bool    VerifiedOnly    { get; set; } = true;
    public string  GpuNameContains { get; set; } = "";
    public string  SortBy          { get; set; } = "best";   // best | cheap | fast
    public int     Limit           { get; set; } = 50;
}
