namespace EventForge.Infrastructure;

/// <summary>EventForge queue capability names for worker poll loops.</summary>
public static class GenQueueCapabilities
{
    public const string FluxKlein = "flux-klein";
    public const string FluxKleinEdit = "flux-klein-edit";
    public const string ZImage = "zimage";
    public const string Chroma = "chroma";
    public const string Wan = "wan";
    public const string Ltx = "ltx";
    public const string Dolphin = "dolphin";

    public static readonly IReadOnlyList<string> ImageCapabilities =
        [FluxKlein, FluxKleinEdit, ZImage, Chroma];

    public static readonly IReadOnlyList<string> AllComfyCapabilities =
        [FluxKlein, FluxKleinEdit, ZImage, Chroma, Wan, Ltx];

    /// <summary>EventForge queue capability for a gen model key (keep in sync with LoboForge GenQueueCapabilities.ForModel).</summary>
    public static string ForModel(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m is "flux2klein-edit" or "flux2klein-dual") return FluxKleinEdit;
        if (m.StartsWith("flux", StringComparison.Ordinal) || m is "storyboard") return FluxKlein;
        if (m is "wan2" or "wan2flf" or "wan2t2v" or "wan" || m.StartsWith("wan", StringComparison.Ordinal))
            return Wan;
        if (m.StartsWith("ltx23", StringComparison.Ordinal) || m is "ltx23") return Ltx;
        if (m is "zimage" or "lens") return ZImage;
        if (m is "chroma") return Chroma;
        // ACE-Step music rides the video (wan) queue — not the LTX23 video queue.
        if (m is "music" or "ace-step") return Wan;
        if (m is "joycaption" or "joy-caption") return "caption";
        if (m is "dolphin") return Dolphin;
        return FluxKlein;
    }

    public static IReadOnlyList<string> ForProvisionMode(
        string? mode,
        bool wanEnabled = true,
        bool ltx23Enabled = false,
        bool musicEnabled = true)
    {
        var norm = NormalizeProvisionMode(mode);
        return norm switch
        {
            "image" => ImageCapabilities,
            "video" => VideoCapabilities(wanEnabled, ltx23Enabled),
            "music" => [Wan],
            "all" or "both" => MergeOrdered(AllComfyCapabilities,
                VideoCapabilities(wanEnabled, ltx23Enabled)),
            "ltx-native" or "ltx" => [Ltx],
            "wan-native" => [Wan],
            "dolphin" => [Dolphin],
            _ => [FluxKlein],
        };
    }

    public static string EnvValueForProvisionMode(
        string? mode,
        bool wanEnabled = true,
        bool ltx23Enabled = false,
        bool musicEnabled = true) =>
        string.Join(",", ForProvisionMode(mode, wanEnabled, ltx23Enabled, musicEnabled));

    public static string NormalizeProvisionMode(string? mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        if (m is "both") return "all";
        if (m.Contains(','))
            return m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return m.Length == 0 ? "all" : m;
    }

    private static IReadOnlyList<string> VideoCapabilities(bool wanEnabled, bool ltx23Enabled)
    {
        var caps = new List<string>(2);
        if (wanEnabled) caps.Add(Wan);
        if (ltx23Enabled) caps.Add(Ltx);
        if (caps.Count == 0) caps.Add(Wan);
        return caps;
    }

    private static IReadOnlyList<string> MergeOrdered(
        IReadOnlyList<string> preferredOrder,
        IReadOnlyList<string> extra)
    {
        var set = new HashSet<string>(preferredOrder, StringComparer.OrdinalIgnoreCase);
        foreach (var c in extra) set.Add(c);
        var merged = new List<string>();
        foreach (var c in preferredOrder)
            if (set.Remove(c)) merged.Add(c);
        foreach (var c in extra)
            if (set.Remove(c)) merged.Add(c);
        foreach (var c in set) merged.Add(c);
        return merged;
    }
}
