namespace EventForge.VastAi;

/// <summary>
/// Disk sizing for vast.ai rents. Models + venv + Comfy temp/output need headroom —
/// image-only boxes wedge at ~45GB models on 45GB containers (incident 2026-05-29).
/// </summary>
public static class VastAiDiskRequirements
{
    /// <summary>Container disk to request on rent (GB).</summary>
    public static int RecommendedRentDiskGb(string? mode) => NormalizeMode(mode) switch
    {
        "ltx-native" => 130,
        "wan-native" => 150,
        "video" => 130,
        "music" => 80,
        "all"   => 150,
        _       => 120, // image (+ Chroma ~68GB models)
    };

    /// <summary>Minimum GPU VRAM for offer search / rent validation (GB).</summary>
    public static int MinimumVramGb(string? mode) => NormalizeMode(mode) switch
    {
        "ltx-native" or "wan-native" or "video" or "all" => 24,
        _ => 16,
    };

    /// <summary>Minimum container disk to request on rent (GB).</summary>
    public static int MinimumRentDiskGb(string? mode) => NormalizeMode(mode) switch
    {
        "ltx-native" => 100,
        "wan-native" => 120,
        "video" => 80,
        "music" => 50,
        "all"   => 120,
        _       => 100, // image stack ~68GB models + venv + ref uploads
    };

    /// <summary>Offer search floor — host must expose at least this much disk_space.</summary>
    public static int MinimumHostDiskGb(string? mode) => NormalizeMode(mode) switch
    {
        "ltx-native" => 120,
        "wan-native" => 120,
        "video" => 90,
        "music" => 50,
        "all"   => 120,
        _       => 80,
    };

    /// <summary>Clamp rent payload to mode minimum and cap at vast max.</summary>
    public static int ClampRentDiskGb(string? mode, int requestedGb)
    {
        var min = MinimumRentDiskGb(mode);
        var rec = RecommendedRentDiskGb(mode);
        var disk = requestedGb > 0 ? requestedGb : rec;
        if (disk < min) disk = min;
        return Math.Clamp(disk, 40, 1024);
    }

    public static string NormalizeMode(string? mode)
    {
        var m = (mode ?? "image").Trim().ToLowerInvariant();
        if (m is "both") return "all";
        if (m is "ltx_native" or "ltxnative") return "ltx-native";
        if (m is "wan_native" or "wannative") return "wan-native";
        return m;
    }

    public static bool IsNativeLtxMode(string? mode) =>
        NormalizeMode(mode) is "ltx-native";

    public static bool IsNativeWanMode(string? mode) =>
        NormalizeMode(mode) is "wan-native";
}
