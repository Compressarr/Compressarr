using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Conversion;

/// <summary>What a HandBrake video encoder can do with an HDR source.</summary>
public enum HandBrakeHdrSupport
{
    /// <summary>H.264 and older codecs: no HDR signalling worth the name, so an HDR source comes out with washed-out colours.</summary>
    SdrOnly,

    /// <summary>Carries HDR10 (colour tags, mastering display, light level) but not Dolby Vision / HDR10+ (the hardware encoders).</summary>
    StaticHdr,

    /// <summary>Carries HDR10 and, with the profile's dynamic-metadata setting on, Dolby Vision and HDR10+ (x265, SVT-AV1).</summary>
    DynamicHdr
}

/// <summary>The two things in a HandBrake profile that decide how it treats HDR: its video encoder and whether it passes dynamic
/// HDR metadata (Dolby Vision / HDR10+) through.</summary>
public sealed record HandBrakeProfileTraits(string Encoder, bool DynamicMetadataOn);

/// <summary>
/// The HandBrake side of "no surprises with HDR": an HDR source is not re-encoded with a profile whose encoder would silently lose
/// the HDR (and then replace the original). ffmpeg lanes have their own rule (see ReportErrorCode.DynamicHdrNeedsHandBrake);
/// this is the same idea for lanes that run HandBrake.
/// </summary>
public static class HandBrakeHdrGuard
{
    /// <summary>Classifies a HandBrake encoder by its HandBrake name (x265_10bit, svt_av1, nvenc_h265_10bit...). HandBrake's own
    /// documentation lists Dolby Vision and HDR10+ passthrough for x265, SVT-AV1 and VideoToolbox only. An encoder not recognised
    /// as H.264-or-older is treated as HDR10-capable, so an unfamiliar encoder is never refused wrongly.</summary>
    public static HandBrakeHdrSupport SupportOf(string? encoder)
    {
        var e = (encoder ?? "").Trim().ToLowerInvariant();
        if (e.StartsWith("x265") || e.StartsWith("svt_av1") || e.StartsWith("vt_h265")) return HandBrakeHdrSupport.DynamicHdr;
        if (e.StartsWith("x264") || e.Contains("h264") || e.StartsWith("mpeg") || e.StartsWith("ffmpeg4") || e.StartsWith("ffmpeg2")
            || e.StartsWith("theora") || e.StartsWith("vp8")) return HandBrakeHdrSupport.SdrOnly;
        return HandBrakeHdrSupport.StaticHdr;
    }

    /// <summary>True when the source has to be read to decide: only a profile that can't be relied on for HDR can ever be refused.
    /// An x265 or SVT-AV1 profile with dynamic metadata on keeps everything and never needs the (slower) probe.</summary>
    public static bool NeedsProbe(HandBrakeProfileTraits traits) =>
        SupportOf(traits.Encoder) != HandBrakeHdrSupport.DynamicHdr || !traits.DynamicMetadataOn;

    /// <summary>Why this source must not be encoded with this profile, or null when it may be. <paramref name="allowLoss"/> is the
    /// lane's explicit "let HDR files lose their HDR" choice; it never covers a Dolby Vision profile 5 file, which has no HDR10 base
    /// layer and would show wrong colours.</summary>
    public static string? RefusalReason(MediaProbeResult probe, HandBrakeProfileTraits traits, bool allowLoss)
    {
        var video = probe.Video;
        if (video is null) return null;

        var dynamic = probe.HasDynamicHdrMetadata;
        if (!video.IsHdr && !dynamic) return null;

        var support = SupportOf(traits.Encoder);
        var keepsDynamic = support == HandBrakeHdrSupport.DynamicHdr && traits.DynamicMetadataOn;

        // Dolby Vision with no HDR10-compatible base layer (profile 5) can't be encoded without its Dolby Vision data.
        if (video.HasDolbyVision && !probe.DynamicHdrCanBeDropped && !keepsDynamic)
        {
            var label = probe.DolbyVisionProfileLabel.Length > 0 ? probe.DolbyVisionProfileLabel : "profile unknown";
            return $"this Dolby Vision file ({label}) has no HDR10-compatible base layer, and the profile's video encoder ({traits.Encoder}) "
                 + "would not keep the Dolby Vision data, so the picture would come out with wrong colours";
        }

        if (allowLoss) return null;

        if (support == HandBrakeHdrSupport.SdrOnly)
        {
            var what = dynamic ? probe.DynamicHdrName : "HDR";
            return $"this is a {what} file, but the profile's video encoder ({traits.Encoder}) can only produce SDR, so the picture would come out with washed-out colours";
        }

        // HDR10 alone is carried by every other encoder; what a hardware encoder can't carry is the dynamic metadata, and a
        // profile that has switched dynamic metadata off has already said it doesn't want it.
        if (dynamic && !keepsDynamic && traits.DynamicMetadataOn)
        {
            return $"this file has {probe.DynamicHdrName}, which the profile's video encoder ({traits.Encoder}) can't keep, so it would be lost";
        }

        return null;
    }
}
