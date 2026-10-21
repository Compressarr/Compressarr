using Compressarr.Core.Validation;

namespace Compressarr.Core.Config;

/// <summary>Checks the Settings page's own "can this app actually run at all" conditions: the
/// FileBot path (enforced at run time by FileBotRunner's own path check) and VidTypes (no runtime
/// guard) - surfaced on the Settings page so a broken base configuration is visible before a real
/// run ever hits it, not just log-only. The encoder tools' own checks live in EncoderValidator.</summary>
public static class SettingsValidator
{
    public static List<ValidationIssue> Validate(CompressarrConfig config, IPathExpander pathExpander)
    {
        var issues = new List<ValidationIssue>();

        if (config.FileBot.Enabled)
        {
            var fileBotPath = pathExpander.Expand(config.FileBot.CliPath);
            if (string.IsNullOrWhiteSpace(config.FileBot.CliPath) || !pathExpander.PathExists(config.FileBot.CliPath))
            {
                issues.Add(new ValidationIssue("fileBotCliPath", $"FileBot is enabled but its path is required and must exist - not found at '{fileBotPath}'."));
            }
        }

        if (config.Posters.Enabled && config.Posters.Source == PosterSource.Omdb && string.IsNullOrWhiteSpace(config.Posters.OmdbApiKey))
        {
            issues.Add(new ValidationIssue("omdbApiKey", "Show posters on queue is on, but it needs an OMDb API key (free at omdbapi.com)."));
        }
        else if (config.Posters.Enabled && config.Posters.Source == PosterSource.Tmdb && string.IsNullOrWhiteSpace(config.Posters.TmdbApiKey))
        {
            issues.Add(new ValidationIssue("tmdbApiKey", "Show posters on queue is on, but it needs a TMDb API key (free at themoviedb.org)."));
        }

        if (config.Processing.VidTypes.Count == 0)
        {
            issues.Add(new ValidationIssue("vidTypes", "At least one video extension is required - without one, no file can ever be recognized as a video to convert."));
        }

        return issues;
    }
}
