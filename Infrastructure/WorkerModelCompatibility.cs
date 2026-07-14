namespace EventForge.Infrastructure;

/// <summary>
/// Mirror of agent/loboforge_agent_common.py worker_can_run_model — gate job claims by on-disk models.
/// </summary>
public static class WorkerModelCompatibility
{
    public static bool CanRunModel(
        WorkerModelAssets assets,
        string? model,
        string? hostname,
        string? capability)
    {
        if (string.IsNullOrWhiteSpace(model))
            return true;

        var lower = model.Trim().ToLowerInvariant();
        var hn = hostname ?? "";

        // JoyCaption/Ollama workers do not send Comfy model inventory on check-in.
        if (lower is "joycaption" or "joy-caption" && HostnameIsJoycaption(hn))
            return true;
        if (HostnameIsOllama(hn) && (lower == "dolphin" || lower.StartsWith("dolphin", StringComparison.Ordinal)))
            return true;
        if (IsOllamaChatCapability(capability)
            && (lower.StartsWith("dolphin", StringComparison.Ordinal)
                || lower.StartsWith("loboforge-", StringComparison.Ordinal)
                || lower.Contains("roleplay", StringComparison.Ordinal)))
            return true;

        // Native Wan workers skip Comfy inventory — gate on claim_ready + layout on the box.
        if (HostnameIsWanNative(hn)
            && (lower is "wan2" or "wan2flf" or "wan2t2v"
                || string.Equals(capability, "wan", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (assets.Assets.Count == 0)
            return false;

        var imageOnly = HostnameIsImageOnly(hn);
        var videoOnly = HostnameIsWanOrVideoOnly(hn);

        if (lower == "wan2t2v")
        {
            if (imageOnly) return false;
            return HasWanT2V(assets);
        }

        if (lower is "wan2" or "wan2flf")
        {
            if (imageOnly) return false;
            return HasWanI2V(assets);
        }

        if (lower.StartsWith("ltx23", StringComparison.Ordinal) || lower == "ltx23")
        {
            if (imageOnly) return false;
            return assets.Assets.Any(LooksLikeLtxAsset);
        }

        if (lower is "music" or "ace-step")
        {
            if (imageOnly) return false;
            return HasAceStep(assets);
        }

        if (videoOnly && (lower.StartsWith("flux", StringComparison.Ordinal)
                          || lower is "storyboard" or "zimage" or "chroma" or "lens"))
            return false;

        if (lower.StartsWith("flux", StringComparison.Ordinal) || lower == "storyboard")
        {
            if (lower is "flux2klein" or "flux2klein-edit" or "flux2klein-dual")
                return HasFluxKlein(assets) && HasFlux2TextEncoder(assets);
            return HasFluxKlein(assets)
                   || assets.Assets.Any(m => m.Contains("flux", StringComparison.OrdinalIgnoreCase)
                                             || m.Contains("klein", StringComparison.OrdinalIgnoreCase));
        }

        if (lower == "chroma")
            return assets.Assets.Any(m => m.Contains("chroma", StringComparison.OrdinalIgnoreCase));

        if (lower == "zimage")
            return HasZImageTextEncoder(assets)
                   || assets.Assets.Any(m =>
                       m.Contains("zimage", StringComparison.OrdinalIgnoreCase)
                       || m.Contains("z_image", StringComparison.OrdinalIgnoreCase)
                       || m.Contains("z-image", StringComparison.OrdinalIgnoreCase));

        if (lower == "lens")
            return HasLensTextEncoder(assets)
                   || assets.Assets.Any(m => m.Contains("lens", StringComparison.OrdinalIgnoreCase));

        if (lower is "joycaption" or "joy-caption")
        {
            if (HostnameIsJoycaption(hn))
                return true;
            return assets.Assets.Any(m =>
                m.Contains("joycaption", StringComparison.OrdinalIgnoreCase)
                || m.Contains("joy_caption", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(capability) && !string.Equals(capability, lower, StringComparison.OrdinalIgnoreCase))
        {
            var expected = capability.Trim().ToLowerInvariant() switch
            {
                "flux-klein" => lower.StartsWith("flux", StringComparison.Ordinal) || lower == "storyboard",
                "flux-klein-dual" => lower == "flux2klein-dual",
                "flux-klein-edit" => lower is "flux2klein-edit" or "flux2klein-dual",
                "zimage" => lower is "zimage" or "lens",
                "chroma" => lower == "chroma",
                "wan" => lower.StartsWith("wan", StringComparison.Ordinal) || lower is "music" or "ace-step",
                "ltx" => lower.StartsWith("ltx", StringComparison.Ordinal),
                "dolphin" => lower == "dolphin" || lower.StartsWith("dolphin", StringComparison.Ordinal),
                "ollama-chat" => lower.StartsWith("dolphin", StringComparison.Ordinal)
                                 || lower.StartsWith("loboforge-", StringComparison.Ordinal)
                                 || lower.Contains("roleplay", StringComparison.Ordinal),
                _ => (bool?)null,
            };
            if (expected == false) return false;
        }

        return assets.Assets.Contains(model, StringComparer.OrdinalIgnoreCase)
               || assets.Assets.Any(m => m.Contains(model, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HostnameIsJoycaption(string hostname) =>
        hostname.Contains("joycaption", StringComparison.OrdinalIgnoreCase);

    private static bool HostnameIsOllama(string hostname) =>
        hostname.Contains("ollama", StringComparison.OrdinalIgnoreCase)
        || hostname.Contains("dolphin", StringComparison.OrdinalIgnoreCase);

    private static bool IsOllamaChatCapability(string? capability) =>
        string.Equals(capability, "ollama-chat", StringComparison.OrdinalIgnoreCase)
        || string.Equals(capability, "dolphin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ollama/dolphin jobs must never terminal-fail — workers release back to queue instead.</summary>
    public static bool IsNeverFailCapability(string? capability) => IsOllamaChatCapability(capability);

    private static bool HostnameIsWanNative(string hostname) =>
        hostname.Contains("wan-native", StringComparison.OrdinalIgnoreCase)
        || hostname.StartsWith("loboforge-wan-", StringComparison.OrdinalIgnoreCase);

    private static bool HostnameIsImageOnly(string hostname)
    {
        var hn = hostname.ToLowerInvariant();
        return hn.Contains("-image-", StringComparison.Ordinal) && !hn.Contains("-all-", StringComparison.Ordinal);
    }

    private static bool HostnameIsWanOrVideoOnly(string hostname)
    {
        var hn = hostname.ToLowerInvariant();
        if (hn.Contains("-all-", StringComparison.Ordinal)) return false;
        return hn.Contains("-wan-", StringComparison.Ordinal) || hn.Contains("-video-", StringComparison.Ordinal);
    }

    private static bool HasFluxKlein(WorkerModelAssets assets) =>
        assets.Assets.Any(m =>
        {
            var ml = m.ToLowerInvariant();
            return ml.Contains("klein") || ml.Contains("flux2") || ml.Contains("flux-2");
        });

    private static bool HasFlux2TextEncoder(WorkerModelAssets assets) =>
        assets.Assets.Any(m => m.Contains("qwen", StringComparison.OrdinalIgnoreCase));

    private static bool HasZImageTextEncoder(WorkerModelAssets assets) =>
        assets.Assets.Any(m => m.Contains("qwen_3_4b", StringComparison.OrdinalIgnoreCase));

    private static bool HasLensTextEncoder(WorkerModelAssets assets) =>
        assets.Assets.Any(m => m.Contains("gpt_oss", StringComparison.OrdinalIgnoreCase));

    private static bool HasWanI2V(WorkerModelAssets assets) =>
        WanNoisePairPresent(assets.Unets, i2v: true);

    private static bool HasWanT2V(WorkerModelAssets assets) =>
        WanNoisePairPresent(assets.Unets, i2v: false);

    /// <summary>
    /// Wan 2.2 MoE needs both high- and low-noise UNETs. High-only inventory caused
    /// Comfy prompt_outputs_failed_validation (low_noise unet_name not in list).
    /// </summary>
    private static bool WanNoisePairPresent(IEnumerable<string> unets, bool i2v)
    {
        var matched = new List<string>();
        foreach (var m in unets)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            var ml = m.ToLowerInvariant();
            if (i2v)
            {
                if (ml.Contains("wan") && ml.Contains("i2v"))
                    matched.Add(ml);
            }
            else if ((ml.Contains("wan") && ml.Contains("t2v"))
                     || ml.Contains("t2v_low_noise")
                     || ml.Contains("wan2.2_t2v"))
            {
                matched.Add(ml);
            }
        }

        if (matched.Count == 0) return false;
        var hasHigh = matched.Any(u => u.Contains("high_noise") || u.Contains("high-noise"));
        var hasLow = matched.Any(u => u.Contains("low_noise") || u.Contains("low-noise"));
        return hasHigh && hasLow;
    }

    private static bool HasAceStep(WorkerModelAssets assets) =>
        assets.Assets.Any(m =>
        {
            var ml = m.ToLowerInvariant();
            return ml.Contains("ace_step") || ml.Contains("ace-step");
        });

    private static bool LooksLikeLtxAsset(string name)
    {
        var ml = name.ToLowerInvariant();
        if (ml.Contains("taeltx")) return false;
        if (ml.Contains("ltx-2.3") || ml.Contains("ltx-2") || ml.Contains("ltx23") || ml.Contains("ltx2"))
            return true;
        return ml.Contains("ltx") && ml.Contains("gemma") || ml.Contains("gemma_3_12b");
    }
}
