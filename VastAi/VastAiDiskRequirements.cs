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
        // wan-native runs the native Wan-Video/Wan2.2 runner (LOBO_SKIP_COMFY=1 — NO ComfyUI).
        // Its claim-ready gate (i2v_ready) requires the full Wan-AI/Wan2.2-I2V-A14B checkpoint:
        // high_noise_model 57GB + low_noise_model 57GB + umt5-xxl T5 11.4GB + VAE 0.5GB = ~126GB,
        // plus the Wan2.2 git repo + pip venv (~12GB), 2 lightning LoRAs (~1.5GB), hf download
        // staging, and video-output temp during jobs. A 170GB box (incident 2026-07-18, inst
        // 45255378) filled up mid-download and never became claim-ready; healthy reference box
        // (45013488) sits at ~23GB free on 200GB. Size to 256GB for comfortable headroom.
        "wan-native" => 256,
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
        // Must exceed the full native+fp8 wan footprint (~200GB observed on healthy boxes).
        // Renting below this silently produces a box that never reaches claim_ready.
        "wan-native" => 220,
        "video" => 80,
        "music" => 50,
        "all"   => 120,
        _       => 100, // image stack ~68GB models + venv + ref uploads
    };

    /// <summary>Offer search floor — host must expose at least this much disk_space.</summary>
    public static int MinimumHostDiskGb(string? mode) => NormalizeMode(mode) switch
    {
        "ltx-native" => 120,
        "wan-native" => 220,
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
