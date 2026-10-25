using Compressarr.Core.Config;
using Compressarr.Web.Dtos;
using Compressarr.Web.Secrets;

namespace Compressarr.Web;

/// <summary>Maps CompressarrConfig <-> the web API's DTOs. Direct port of the field-by-field
/// logic MainViewModel.ApplyConfig/BuildConfig used to do for Avalonia data binding, now facing
/// JSON instead.</summary>
internal static class ConfigMapping
{
    public static SettingsDto ToSettingsDto(CompressarrConfig config, List<ValidationIssueDto> validationIssues, DateTimeOffset? backupLastRunUtc) => new(
        FileBotEnabled: config.FileBot.Enabled,
        FileBotCliPath: config.FileBot.CliPath,
        FileBotTvEnabled: config.FileBot.TvEnabled,
        FileBotTvArgs: config.FileBot.TvArgs,
        FileBotMovieEnabled: config.FileBot.MovieEnabled,
        FileBotMovieArgs: config.FileBot.MovieArgs,
        VidTypes: config.Processing.VidTypes,
        OutSameAsIn: config.Processing.OutSameAsIn,
        DeleteAfterConvert: config.Processing.DeleteAfterConvert.ToString(),
        MoveFiles: config.Processing.MoveFiles,
        ClearTitleMetadata: config.Processing.ClearTitleMetadata,
        Limit: config.Processing.Limit,
        MinSizeBytes: config.Processing.MinSizeBytes,
        CompanionExtensions: config.Processing.CompanionExtensions,
        UnmatchedCompanionAction: config.Processing.UnmatchedCompanionAction.ToString(),
        OnDestinationCollision: config.Processing.OnDestinationCollision.ToString(),
        LogFilePath: config.Logging.LogFilePath,
        RetentionDays: config.Logging.RetentionDays,
        KeepSuccessfulHandBrakeLogs: config.Logging.KeepSuccessfulHandBrakeLogs,
        PostExecCmd: config.PostExec.Cmd,
        PostExecArgs: config.PostExec.Args,
        ReportPath: config.Report.ReportPath,
        OpenAfterRun: config.Report.OpenAfterRun.ToString(),
        RepeatCount: config.Repeat.Count,
        RepeatMonitor: config.Repeat.Monitor,
        LaunchMonitorAtStartup: config.Repeat.LaunchMonitorAtStartup,
        PollIntervalSeconds: config.Repeat.PollIntervalSeconds,
        QueueEtaFormat: config.Repeat.QueueEtaFormat.ToString(),
        Sonarr: new ArrServiceDto(config.Arrs.Sonarr.Enabled, config.Arrs.Sonarr.Url, SecretFields.Show(config.Arrs.Sonarr.ApiKey)),   // a saved key is never sent, only the mask
        Radarr: new ArrServiceDto(config.Arrs.Radarr.Enabled, config.Arrs.Radarr.Url, SecretFields.Show(config.Arrs.Radarr.ApiKey)),
        WebPort: config.Web.Port,
        RunAtLogin: config.Startup.RunAtLogin,
        AnnouncementsEnabled: config.Announcements.Enabled,
        UpdateChannel: config.UpdateCheck.Channel.ToString(),
        BackupFolderPath: config.Backup.FolderPath,
        BackupIntervalDays: config.Backup.IntervalDays,
        BackupRetentionDays: config.Backup.RetentionDays,
        BackupLastRunUtc: backupLastRunUtc,
        ValidationIssues: validationIssues,
        Posters: new PosterSettingsDto(config.Posters.Enabled, SecretFields.Show(config.Posters.TmdbApiKey), config.Posters.Size.ToString(), config.Posters.SmallerImages,
            config.Posters.Source.ToString(), SecretFields.Show(config.Posters.OmdbApiKey), config.Posters.FallbackToOther),
        SaveReportCopy: config.Report.SaveCopy,
        ReportCopyRetentionDays: config.Report.CopyRetentionDays,
        UnseenErrorDays: config.Report.UnseenErrorDays,
        AllowedHosts: config.Web.AllowedHosts);

    public static void ApplySettingsDto(CompressarrConfig config, SettingsDto dto)
    {
        config.FileBot.Enabled = dto.FileBotEnabled;
        config.FileBot.CliPath = dto.FileBotCliPath;
        config.FileBot.TvEnabled = dto.FileBotTvEnabled;
        config.FileBot.TvArgs = dto.FileBotTvArgs;
        config.FileBot.MovieEnabled = dto.FileBotMovieEnabled;
        config.FileBot.MovieArgs = dto.FileBotMovieArgs;
        config.Processing.VidTypes = dto.VidTypes;
        config.Processing.OutSameAsIn = dto.OutSameAsIn;
        config.Processing.DeleteAfterConvert = Enum.Parse<DeleteAfterConvertMode>(dto.DeleteAfterConvert);
        config.Processing.MoveFiles = dto.MoveFiles;
        config.Processing.ClearTitleMetadata = dto.ClearTitleMetadata;
        config.Processing.Limit = dto.Limit;
        config.Processing.MinSizeBytes = dto.MinSizeBytes;
        config.Processing.CompanionExtensions = dto.CompanionExtensions;
        config.Processing.UnmatchedCompanionAction = Enum.Parse<DeleteAfterConvertMode>(dto.UnmatchedCompanionAction);
        config.Processing.OnDestinationCollision = Enum.Parse<DestinationCollisionMode>(dto.OnDestinationCollision);
        config.Logging.LogFilePath = dto.LogFilePath;
        config.Logging.RetentionDays = dto.RetentionDays;
        config.Logging.KeepSuccessfulHandBrakeLogs = dto.KeepSuccessfulHandBrakeLogs;
        config.PostExec.Cmd = dto.PostExecCmd;
        config.PostExec.Args = dto.PostExecArgs;
        config.Report.ReportPath = dto.ReportPath;
        config.Report.OpenAfterRun = Enum.Parse<OpenReportMode>(dto.OpenAfterRun);
        if (dto.SaveReportCopy is { } saveCopy) config.Report.SaveCopy = saveCopy;
        // Blank (null) means "same as the log retention"; a negative number is not a number of days.
        config.Report.CopyRetentionDays = dto.ReportCopyRetentionDays is >= 0 ? dto.ReportCopyRetentionDays : null;
        config.Report.UnseenErrorDays = dto.UnseenErrorDays is >= 0 ? dto.UnseenErrorDays : null;
        if (dto.AllowedHosts is { } allowedHosts) config.Web.AllowedHosts = allowedHosts.Trim();
        config.Repeat.Count = dto.RepeatCount;
        config.Repeat.Monitor = dto.RepeatMonitor;
        config.Repeat.LaunchMonitorAtStartup = dto.LaunchMonitorAtStartup;
        config.Repeat.PollIntervalSeconds = dto.PollIntervalSeconds;
        config.Repeat.QueueEtaFormat = Enum.Parse<QueueEtaDisplayFormat>(dto.QueueEtaFormat);
        // A key box that still shows the mask keeps the saved key; anything else is what was typed - an empty box removes the key.
        var sonarrKey = SecretFields.Resolve(dto.Sonarr.ApiKey, config.Arrs.Sonarr.ApiKey);
        var radarrKey = SecretFields.Resolve(dto.Radarr.ApiKey, config.Arrs.Radarr.ApiKey);
        config.Arrs.Sonarr = new ArrServiceSettings { Enabled = dto.Sonarr.Enabled, Url = dto.Sonarr.Url, ApiKey = sonarrKey };
        config.Arrs.Radarr = new ArrServiceSettings { Enabled = dto.Radarr.Enabled, Url = dto.Radarr.Url, ApiKey = radarrKey };
        config.Web.Port = dto.WebPort;
        config.Startup.RunAtLogin = dto.RunAtLogin;
        // Like the poster and HDR choices: a page that does not send these (an older cached one) leaves the saved values alone.
        if (dto.AnnouncementsEnabled is { } announcementsEnabled) config.Announcements.Enabled = announcementsEnabled;
        // Not sent: left alone. Sent but not a known channel: the default for this release.
        if (dto.UpdateChannel is not null)
        {
            config.UpdateCheck.Channel = Enum.TryParse<UpdateChannel>(dto.UpdateChannel, ignoreCase: true, out var updateChannel) ? updateChannel : UpdateCheckSettings.DefaultChannel;
        }
        config.Backup.FolderPath = dto.BackupFolderPath;
        config.Backup.IntervalDays = dto.BackupIntervalDays;
        config.Backup.RetentionDays = dto.BackupRetentionDays;
        if (dto.Posters is { } posters)
        {
            config.Posters.Enabled = posters.Enabled;
            config.Posters.TmdbApiKey = SecretFields.Resolve(posters.TmdbApiKey, config.Posters.TmdbApiKey);
            config.Posters.Size = Enum.TryParse<PosterSize>(posters.Size, ignoreCase: true, out var posterSize) ? posterSize : PosterSize.Medium;
            config.Posters.SmallerImages = posters.SmallerImages;
            // Added in 2.2.0-rc.2: a page that does not send them (an older cached one) leaves the saved choice alone.
            if (posters.Source is not null) config.Posters.Source = Enum.TryParse<PosterSource>(posters.Source, ignoreCase: true, out var posterSource) ? posterSource : PosterSource.Tmdb;
            config.Posters.OmdbApiKey = SecretFields.Resolve(posters.OmdbApiKey, config.Posters.OmdbApiKey);
            if (posters.FallbackToOther is { } fallback) config.Posters.FallbackToOther = fallback;
        }

        // BackupLastRunUtc is read-only from the client's perspective - set only by BackupService
        // itself after a real backup runs, never round-tripped back in from a settings save.

    }

    public static LaneDto ToLaneDto(LaneConfig lane, List<ValidationIssueDto> validationIssues) => new(
        lane.Id, lane.DisplayName, lane.Enabled, lane.Input, lane.Output,
        lane.TvPreset, lane.MoviePreset, lane.TvShowBasePath, lane.MovieBasePath, validationIssues, lane.Engine.ToString(), lane.AllowHdrLoss);

    public static void ApplyLaneDto(LaneConfig lane, LaneDto dto)
    {
        lane.DisplayName = dto.DisplayName;
        lane.Enabled = dto.Enabled;
        lane.Input = dto.Input;
        lane.Output = dto.Output;
        lane.TvPreset = dto.TvPreset;
        lane.MoviePreset = dto.MoviePreset;
        lane.TvShowBasePath = dto.TvShowBasePath;
        lane.MovieBasePath = dto.MovieBasePath;
        // A client that doesn't send the field (an older cached page) leaves the lane's encoder alone.
        if (Enum.TryParse<EncoderEngine>(dto.Engine, ignoreCase: true, out var engine)) lane.Engine = engine;
        // Same for the HDR choice: not sent, not changed.
        if (dto.AllowHdrLoss is { } allowHdrLoss) lane.AllowHdrLoss = allowHdrLoss;
    }

    /// <summary>A channel as the page sees it: the notifier's secret fields (<paramref name="secretKeys"/>) come back as the mask when saved (never the value).</summary>
    public static NotificationChannelDto ToChannelDto(NotificationChannel channel, HashSet<string> secretKeys) => new(
        channel.Id, channel.Type, channel.DisplayName, channel.Trigger.ToString(),
        channel.Settings.ToDictionary(kv => kv.Key, kv => secretKeys.Contains(kv.Key) ? SecretFields.Show(kv.Value) : kv.Value),
        channel.DigestDailyEnabled, channel.DigestWeeklyEnabled, channel.DigestDailyTime, channel.DigestWeeklyTime, channel.DigestWeeklyDay.ToString());

    public static void ApplyChannelDto(NotificationChannel channel, NotificationChannelDto dto, HashSet<string> secretKeys)
    {
        channel.DisplayName = dto.DisplayName;
        channel.Trigger = Enum.Parse<NotificationTrigger>(dto.Trigger);
        // A secret field that still shows the mask (or is not sent) keeps the saved value; anything else is what was typed - an empty box removes it. The other fields are taken as sent.
        var settings = new Dictionary<string, string>(dto.Settings);
        foreach (var key in secretKeys)
        {
            var typed = settings.TryGetValue(key, out var value) ? value : null;
            var existing = channel.Settings.TryGetValue(key, out var saved) ? saved : "";
            var resolved = SecretFields.Resolve(typed, existing);
            if (resolved.Length == 0) settings.Remove(key); else settings[key] = resolved;
        }

        channel.Settings = settings;
        // Type is deliberately not settable via update - a channel's type is fixed at creation
        // (its field schema depends on it); changing type would need a new channel instead.

        channel.DigestDailyEnabled = dto.DigestDailyEnabled;
        channel.DigestWeeklyEnabled = dto.DigestWeeklyEnabled;
        channel.DigestDailyTime = dto.DigestDailyTime;
        channel.DigestWeeklyTime = dto.DigestWeeklyTime;
        channel.DigestWeeklyDay = Enum.Parse<DayOfWeek>(dto.DigestWeeklyDay);
        // The days a digest was last sent are deliberately not in the DTO at all - scheduler-internal bookkeeping (kept in the app state, not the
        // settings file), never user-editable.
    }
}
