namespace Compressarr.Core.Config;

/// <summary>
/// The one place that decides which file-handling settings apply to a lane (3.1): the lane's own set when it has one, otherwise the global
/// Settings &gt; Processing block. The engine never reads a lane's settings any other way, so a lane that overrides and a lane that does not behave
/// the same way everywhere (scan, move, collision, delete, companions, the queue's own view of the lane).
/// </summary>
public static class LaneProcessing
{
    /// <summary>The settings that govern files owned by <paramref name="lane"/>. A lane with no override of its own gets the global block.</summary>
    public static ProcessingSettings For(CompressarrConfig config, LaneConfig? lane)
        => lane?.Processing is { } own ? Merge(config.Processing, own) : config.Processing;

    /// <summary>The global block with the settings a lane owns replaced by the lane's. Two settings stay global on purpose: the per-pass file limit (a run-wide
    /// throttle) and "write output to the same folder as input" (it interacts with the lane's Output folder); a lane's copy of those two is ignored.</summary>
    private static ProcessingSettings Merge(ProcessingSettings global, ProcessingSettings own)
    {
        var merged = Clone(own);
        merged.Limit = global.Limit;
        merged.OutSameAsIn = global.OutSameAsIn;
        return merged;
    }

    /// <summary>True when the lane has its own set instead of following the global one.</summary>
    public static bool IsCustom(LaneConfig? lane) => lane?.Processing is not null;

    /// <summary>
    /// The configuration to hand to code that reads <c>config.Processing</c> for one lane's files. For a lane that follows the global settings this is
    /// <paramref name="config"/> itself (so nothing changes for anyone who never uses the feature); for a lane with its own set it is a view of the same
    /// configuration with that set in place of the global one. A lane the config does not know (<c>null</c>) gets the global block.
    /// </summary>
    public static CompressarrConfig ConfigFor(CompressarrConfig config, LaneConfig? lane)
        => lane?.Processing is not null ? config.WithProcessing(For(config, lane)) : config;

    /// <summary>Resolves the lane of a queue entry by its id, then <see cref="ConfigFor"/>. An entry whose lane is gone gets the global settings.</summary>
    public static CompressarrConfig ConfigFor(CompressarrConfig config, string? laneId)
        => ConfigFor(config, laneId is null ? null : config.Lanes.FirstOrDefault(l => l.Id == laneId));

    /// <summary>A complete copy of the global settings, for a lane that stops following them and starts with the values it had been using.</summary>
    public static ProcessingSettings CopyOfGlobal(CompressarrConfig config) => Clone(config.Processing);

    /// <summary>A deep copy (the lists are copied too, so editing one lane never edits another or the global block).</summary>
    public static ProcessingSettings Clone(ProcessingSettings p) => new()
    {
        VidTypes = p.VidTypes.ToList(),
        OutSameAsIn = p.OutSameAsIn,
        DeleteAfterConvert = p.DeleteAfterConvert,
        MoveFiles = p.MoveFiles,
        Limit = p.Limit,
        ClearTitleMetadata = p.ClearTitleMetadata,
        MinSizeBytes = p.MinSizeBytes,
        CompanionExtensions = p.CompanionExtensions.ToList(),
        UnmatchedCompanionAction = p.UnmatchedCompanionAction,
        OnDestinationCollision = p.OnDestinationCollision
    };
}
