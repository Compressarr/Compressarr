using Compressarr.Core.Config;
using Compressarr.Core.Presets;
using Compressarr.Core.Validation;

namespace Compressarr.Core.Conversion;

/// <summary>The single source of truth for "is this lane's own configuration actually usable" -
/// shared by both the real run-time orchestrators (RunOrchestrator's lane-prep loop,
/// ConversionOrchestrator.PrepareLane's Output-folder check) and the Lanes page's own
/// GET/PUT-time validation, so the UI's red-field warnings and the engine's real skip/warn
/// behavior can never drift apart. A disabled lane is never validated - it's deliberately off,
/// warning about its otherwise-incomplete config would just be noise.</summary>
public static class LaneValidator
{
    public static List<ValidationIssue> Validate(LaneConfig lane, CompressarrConfig config, IPathExpander pathExpander, IEncoderPresetService presets)
    {
        var issues = new List<ValidationIssue>();
        if (!lane.Enabled) return issues;

        // An ffmpeg lane needs ffmpeg and ffprobe in place. (A HandBrake lane's CLI is checked on the
        // Encoder page and at run time, exactly as before.)
        if (lane.Engine == EncoderEngine.FFmpeg && EncoderReadiness.Problem(EncoderEngine.FFmpeg, config, pathExpander) is { } engineProblem)
        {
            issues.Add(new ValidationIssue("engine", engineProblem));
        }

        var inputPath = pathExpander.Expand(lane.Input);
        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            issues.Add(new ValidationIssue("input", "Input folder is required and must exist."));
        }
        else if (LaneFolders.FindConflict(lane, config.Lanes, pathExpander) is { } folderConflict)
        {
            // Only a warning here: an overlap that already existed keeps running as it always did (it is flagged, not
            // stopped). Creating a NEW overlap is refused when the lane is saved - see LaneEndpoints.
            issues.Add(new ValidationIssue("input", folderConflict.Describe()));
        }

        var outputBase = pathExpander.Expand(lane.Output);
        if (string.IsNullOrWhiteSpace(outputBase) && !config.Processing.OutSameAsIn)
        {
            issues.Add(new ValidationIssue("output", "Output folder is required, unless 'write output to same folder as input' is enabled in Settings."));
        }

        // Where finished files are moved to (only when Settings > Move converted files is on). Compressarr never creates a missing
        // library folder - the move fails instead - so a destination that is not there is flagged, the way a missing Input is.
        // This only marks the field on the Lanes page; a run is not stopped by it.
        if (config.Processing.MoveFiles)
        {
            FlagMissingDestination(issues, "tvShowBasePath", "TV destination path", lane.TvShowBasePath, pathExpander);
            FlagMissingDestination(issues, "movieBasePath", "Movie destination path", lane.MovieBasePath, pathExpander);
        }

        if (string.IsNullOrWhiteSpace(lane.TvPreset) && string.IsNullOrWhiteSpace(lane.MoviePreset))
        {
            // Either field resolves this - both are flagged so the fix is obvious regardless of
            // which one the user ends up filling in.
            issues.Add(new ValidationIssue("tvPreset", "At least a TV or Movie preset is required on this lane."));
            issues.Add(new ValidationIssue("moviePreset", "At least a TV or Movie preset is required on this lane."));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(lane.TvPreset) && !PassthroughPreset.Is(lane.TvPreset) && !presets.PresetExists(lane.TvPreset))
            {
                issues.Add(new ValidationIssue("tvPreset", $"TV preset '{lane.TvPreset}' was not found in Compressarr's profiles - TV episodes in this lane will be skipped."));
            }
            if (!string.IsNullOrWhiteSpace(lane.MoviePreset) && !PassthroughPreset.Is(lane.MoviePreset) && !presets.PresetExists(lane.MoviePreset))
            {
                issues.Add(new ValidationIssue("moviePreset", $"Movie preset '{lane.MoviePreset}' was not found in Compressarr's profiles - movies in this lane will be skipped."));
            }
        }

        return issues;
    }

    private static void FlagMissingDestination(List<ValidationIssue> issues, string field, string label, string path, IPathExpander pathExpander)
    {
        var expanded = pathExpander.Expand(path);
        if (string.IsNullOrWhiteSpace(expanded) || Directory.Exists(expanded)) return;
        issues.Add(new ValidationIssue(field, $"{label}: that folder does not exist. Compressarr does not create it - create it, or pick another folder."));
    }
}
