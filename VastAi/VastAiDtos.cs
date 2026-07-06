namespace EventForge.VastAi;

// CamelCase wire DTOs for the React admin UI. Internal VastOffer/VastInstance
// types keep [JsonPropertyName] for vast.ai upstream deserialization — those
// names must NOT leak to our own API responses.

public record VastOfferDto(
    long Id,
    long MachineId,
    string GpuName,
    int NumGpus,
    long GpuRamMb,
    decimal DphTotal,
    decimal Reliability,
    decimal DlPerf,
    decimal DlPerfPerDollar,
    bool Verified,
    int CpuCores,
    long CpuRamMb,
    decimal DiskSpace,
    decimal InetUpMbps,
    decimal InetDownMbps,
    string Geolocation,
    string OfferUrl);

public record VastAccountDto(decimal Credit, string Email, decimal Balance);

public record VastInstanceDto(
    long Id,
    long MachineId,
    string ActualStatus,
    string IntendedStatus,
    string GpuName,
    decimal DphTotal,
    string PublicIp,
    string SshHost,
    int SshPort,
    string Label);

public static class VastAiDtoMapper
{
    public static VastOfferDto ToDto(VastOffer o) => new(
        o.Id, o.MachineId, o.GpuName, o.NumGpus, o.GpuRamMb,
        o.DphTotal, o.Reliability, o.DlPerf,
        o.DphTotal > 0 ? o.DlPerf / o.DphTotal : 0,
        o.Verified,
        o.CpuCores, o.CpuRamMb, o.DiskSpace,
        o.InetUpMbps, o.InetDownMbps, o.Geolocation,
        $"https://cloud.vast.ai/?ref_id=&search_id=&offer_id={o.Id}");

    public static VastAccountDto ToDto(VastAccount a) => new(a.Credit, a.Email, a.Balance);

    public static VastInstanceDto ToDto(VastInstance i) => new(
        i.Id, i.MachineId, i.ActualStatus, i.IntendedStatus, i.GpuName,
        i.DphTotal, i.PublicIp, i.SshHost, i.SshPort, i.Label);
}
