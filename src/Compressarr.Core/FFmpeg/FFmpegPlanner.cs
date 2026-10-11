namespace Compressarr.Core.FFmpeg;

/// <summary>What to do with one stream of the source.</summary>
/// <param name="Action">"copy", "encode" or "drop".</param>
/// <param name="Reason">Plain-English why - shown by "Preview decisions" and written to the encode log.</param>
/// <param name="Codec">For "encode" (the target codec) and for converted subtitles (the target format).</param>
public sealed record StreamDecision(MediaStream Stream, string Action, string Reason, string? Codec = null, int? Channels = null, int? BitrateKbps = null)
{
    public bool Kept => Action != "drop";
}

public sealed record CropRect(int Width, int Height, int X, int Y)
{
    public override string ToString() => $"{Width}:{Height}:{X}:{Y}";
}

/// <summary>Every decision made for one file, before any command line exists: which streams are kept
/// and how, whether to deinterlace, the crop. Built by <see cref="FFmpegPlanner"/>, consumed by
/// <see cref="FFmpegCommandBuilder"/> and shown to the user as "Preview decisions".</summary>
public sealed class FFmpegPlan
{
    public required MediaStream Video { get; init; }
    public List<StreamDecision> Audio { get; } = new();
    public List<StreamDecision> Subtitles { get; } = new();

    /// <summary>Attachment streams (fonts for ASS subtitles) that travel with the file.</summary>
    public List<MediaStream> Attachments { get; } = new();

    /// <summary>Streams not carried over, each with the reason.</summary>
    public List<StreamDecision> Dropped { get; } = new();

    public bool Deinterlace { get; set; }
    public string DeinterlaceReason { get; set; } = "";
    public bool DeinterlaceAll { get; set; }

    /// <summary>Set after the sampling pass; null = no crop (none wanted, or no black bars found).</summary>
    public CropRect? Crop { get; set; }
    public string CropReason { get; set; } = "";

    public bool KeepChapters { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// The decisions HandBrake makes by itself and ffmpeg must be told: which audio and subtitle tracks
/// to keep (language order, first vs all, copy vs encode), whether the container can hold them,
/// and whether the picture needs deinterlacing. A pure function of the profile and what ffprobe
/// found, so it is tested exhaustively and doubles as the "Preview decisions" engine.
/// </summary>
public static class FFmpegPlanner
{
    private static readonly HashSet<string> TextSubtitles = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text" };

    private static readonly HashSet<string> Mp4CopyableAudio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "ac3", "eac3", "mp3", "opus" };

    /// <summary>Plans one file. Returns null when the file has no usable video stream.</summary>
    public static FFmpegPlan? Plan(FFmpegProfile profile, MediaProbeResult probe)
    {
        var video = probe.Video;
        if (video is null) return null;

        var plan = new FFmpegPlan { Video = video, KeepChapters = profile.Chapters && probe.ChapterCount > 0 };
        var mp4 = profile.Extension == ".mp4";

        PlanVideoExtras(profile, probe, plan);
        PlanAudio(profile, probe, plan, mp4);
        PlanSubtitles(profile, probe, plan, mp4);

        foreach (var s in probe.Streams)
        {
            if (s.Kind == MediaStreamKind.Video && s.Index != video.Index)
            {
                plan.Dropped.Add(new StreamDecision(s, "drop", s.IsAttachedPicture ? "cover art isn't carried over" : "only the first video stream is encoded"));
            }
            else if (s.Kind == MediaStreamKind.Data)
            {
                plan.Dropped.Add(new StreamDecision(s, "drop", "data/timecode streams aren't carried over"));
            }
        }

        if (!mp4 && plan.Subtitles.Any(d => d.Kept && TextSubtitles.Contains(d.Stream.Codec)))
        {
            plan.Attachments.AddRange(probe.Attachments);
        }
        else
        {
            foreach (var a in probe.Attachments)
            {
                plan.Dropped.Add(new StreamDecision(a, "drop", mp4 ? "MP4 can't hold attachments" : "no text subtitles are kept, so their fonts aren't needed"));
            }
        }

        if (profile.Chapters && probe.ChapterCount > 0) plan.Notes.Add($"{probe.ChapterCount} chapter marker(s) are kept.");
        if (probe.HasDynamicHdrMetadata)
        {
            plan.Notes.Add($"{probe.DynamicHdrName} dynamic metadata found - ffmpeg can't carry it over (the file is normally handed to HandBrake).");
        }
        else if (video.IsHdr)
        {
            plan.Notes.Add("HDR10 colour information is carried over" + (video.Mastering is not null ? " along with its mastering-display metadata." : "."));
        }

        return plan;
    }

    private static void PlanVideoExtras(FFmpegProfile profile, MediaProbeResult probe, FFmpegPlan plan)
    {
        var video = plan.Video;
        switch (profile.Deinterlace)
        {
            case "always":
                plan.Deinterlace = true;
                plan.DeinterlaceAll = true;
                plan.DeinterlaceReason = "deinterlacing is set to always";
                break;
            case "auto" when video.IsInterlaced:
                plan.Deinterlace = true;
                plan.DeinterlaceReason = $"the source is interlaced (field order {video.FieldOrder})";
                break;
            case "auto":
                plan.DeinterlaceReason = video.FieldOrder == "unknown"
                    ? "no field order is stored, so the source is treated as progressive"
                    : "the source is progressive";
                break;
            default:
                plan.DeinterlaceReason = "deinterlacing is off";
                break;
        }

        plan.CropReason = profile.Crop == "auto" ? "not checked yet" : "cropping is off";
    }

    private static void PlanAudio(FFmpegProfile profile, MediaProbeResult probe, FFmpegPlan plan, bool mp4)
    {
        var all = probe.Audio.ToList();
        if (all.Count == 0)
        {
            plan.Notes.Add("The file has no audio.");
            return;
        }

        var chosen = ChooseTracks(all, profile.AudioLanguages, profile.AudioTracks, out var fellBack);
        if (fellBack)
        {
            plan.Notes.Add($"No audio track matched the language list ({string.Join(", ", profile.AudioLanguages)}), so the first audio track was kept rather than none.");
            chosen = new List<MediaStream> { all[0] };
        }

        foreach (var stream in all.Where(s => !chosen.Contains(s)))
        {
            var matchesALanguage = profile.AudioLanguages.Any(l => Matches(stream, l));
            plan.Dropped.Add(new StreamDecision(stream, "drop", matchesALanguage && profile.AudioTracks == "first" && !fellBack
                ? "only the first matching audio track is kept"
                : "its language isn't in the profile's audio language list"));
        }

        var targetChannels = MixdownChannels(profile.AudioMixdown);
        foreach (var stream in chosen)
        {
            var maskName = MaskName(stream);
            var canCopyHere = !mp4 || Mp4CopyableAudio.Contains(stream.Codec);

            if (profile.AudioMode == "passthru" && profile.PassthroughCodecs.Contains(maskName, StringComparer.OrdinalIgnoreCase) && canCopyHere)
            {
                plan.Audio.Add(new StreamDecision(stream, "copy", $"{maskName} is on the pass-through list, so it is copied untouched"));
                continue;
            }

            var why = profile.AudioMode != "passthru" ? "the profile always encodes audio"
                : !profile.PassthroughCodecs.Contains(maskName, StringComparer.OrdinalIgnoreCase) ? $"{maskName} isn't on the pass-through list"
                : $"MP4 can't hold {maskName}";
            // The encoder is the limit as well as the profile: ffmpeg's E-AC3 and AC3 encoders stop at 5.1 and MP3 at stereo, so a
            // larger source is mixed down to what the codec can hold - said out loud, not left to happen silently.
            var codecMax = CodecMaxChannels(profile.AudioCodec);
            var limit = Math.Min(targetChannels ?? int.MaxValue, codecMax ?? int.MaxValue);
            int? channels = limit != int.MaxValue && stream.Channels > limit ? limit : null;
            var cappedByCodec = codecMax is { } cm && stream.Channels > cm && cm < (targetChannels ?? int.MaxValue);
            var capNote = cappedByCodec ? $" ({profile.AudioCodec} holds at most {codecMax} channels, so this {stream.Channels}-channel track is mixed down to {codecMax})" : "";
            plan.Audio.Add(new StreamDecision(stream, "encode", $"{why}, so it is encoded as {profile.AudioCodec}{capNote}", profile.AudioCodec, channels, profile.AudioBitrateKbps));
        }
    }

    private static void PlanSubtitles(FFmpegProfile profile, MediaProbeResult probe, FFmpegPlan plan, bool mp4)
    {
        var all = probe.Subtitles.ToList();
        if (all.Count == 0) return;

        if (profile.SubtitleTracks == "none")
        {
            foreach (var s in all) plan.Dropped.Add(new StreamDecision(s, "drop", "the profile keeps no subtitles"));
            return;
        }

        var chosen = ChooseTracks(all, profile.SubtitleLanguages, profile.SubtitleTracks, out var fellBack);
        if (fellBack) chosen = new List<MediaStream>();

        foreach (var s in all.Where(s => !chosen.Contains(s)))
        {
            var matchesALanguage = profile.SubtitleLanguages.Any(l => Matches(s, l));
            plan.Dropped.Add(new StreamDecision(s, "drop", matchesALanguage && profile.SubtitleTracks == "first" && chosen.Count > 0
                ? "only the first matching subtitle track is kept"
                : "its language isn't in the profile's subtitle language list"));
        }

        foreach (var s in chosen)
        {
            var isText = TextSubtitles.Contains(s.Codec);
            if (mp4)
            {
                if (isText) plan.Subtitles.Add(new StreamDecision(s, "encode", $"{s.Codec} is converted to mov_text for MP4", "mov_text"));
                else plan.Dropped.Add(new StreamDecision(s, "drop", $"{s.Codec} is a picture subtitle and MP4 can't hold it"));
            }
            else if (s.Codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase))
            {
                plan.Subtitles.Add(new StreamDecision(s, "encode", "mov_text is converted to SRT for MKV", "srt"));
            }
            else
            {
                plan.Subtitles.Add(new StreamDecision(s, "copy", "copied untouched"));
            }
        }
    }

    /// <summary>HandBrake's selection rule: walk the language list in order; "first" stops at the
    /// first matching track, "all" keeps every track matching any entry (ordered by the list, then by
    /// position in the file). "und" matches a track with no language tag, "any" matches everything.
    /// <paramref name="fellBack"/> is true when nothing matched at all.</summary>
    internal static List<MediaStream> ChooseTracks(IReadOnlyList<MediaStream> tracks, IReadOnlyList<string> languages, string mode, out bool fellBack)
    {
        var picked = new List<MediaStream>();
        foreach (var language in languages)
        {
            foreach (var track in tracks)
            {
                if (picked.Contains(track)) continue;
                if (!Matches(track, language)) continue;
                picked.Add(track);
                if (mode == "first") { fellBack = false; return picked; }
            }
        }

        fellBack = picked.Count == 0;
        return picked;
    }

    private static bool Matches(MediaStream track, string language) =>
        language.Equals("any", StringComparison.OrdinalIgnoreCase)
        || string.Equals(track.Language, language.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a codec has in a profile's pass-through list (HandBrake's names).</summary>
    internal static string MaskName(MediaStream audio) => audio.Codec.ToLowerInvariant() switch
    {
        "dts" when audio.CodecProfile.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase) => "dtshd",
        var other => other
    };

    /// <summary>The most channels ffmpeg's encoder for this codec can write; null when it has no practical limit.</summary>
    internal static int? CodecMaxChannels(string codec) => codec.ToLowerInvariant() switch
    {
        "ac3" or "eac3" => 6,
        "mp3" => 2,
        _ => null
    };

    internal static int? MixdownChannels(string mixdown) => mixdown switch
    {
        "mono" => 1,
        "stereo" => 2,
        "5.1" => 6,
        "7.1" => 8,
        _ => null
    };
}
