namespace EventForge.Infrastructure;

public static class GpuNodeCompatibility
{
    public static bool GpuNameCanExecuteJobs(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return true;
        ReadOnlySpan<string> blocked = ["K80", "M40", "M10"];
        foreach (var token in blocked)
        {
            if (gpuName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
