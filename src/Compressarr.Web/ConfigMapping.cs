using Compressarr.Core.Config;
using Compressarr.Web.Dtos;

namespace Compressarr.Web;

/// <summary>Maps CompressarrConfig <-> the web API's DTOs. Direct port of the field-by-field
/// logic MainViewModel.ApplyConfig/BuildConfig used to do for Avalonia data binding, now facing
/// JSON instead.</summary>
internal static class ConfigMapping
{
    public static SettingsDto ToSettingsDto(CompressarrConfig config, List<ValidationIssueDto> validationIssues) => new(
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
        Sonarr: new ArrServiceDto(config.Arrs.Sonarr.Enabled, config.Arrs.Sonarr.Url, config.Arrs.Sonarr.ApiKey),
        Radarr: new ArrServiceDto(config.Arrs.Radarr.Enabled, config.Arrs.Radarr.Url, config.Arrs.Radarr.ApiKey),
        WebPort: config.Web.Port,
        RunAtLogin: config.Startup.RunAtLogin,
        AnnouncementsEnabled: config.Announcements.Enabled,
        UpdateChannel: config.UpdateCheck.Channel.ToString(),
        BackupFolderPath: config.Backup.FolderPath,
        BackupIntervalDays: config.Backup.IntervalDays,
        BackupRetentionDays: config.Backup.RetentionDays,
        BackupLastRunUtc: config.Backup.LastRunUtc,
        ValidationIssues: validationIssues,
        Posters: new PosterSettingsDto(config.Posters.Enabled, config.Posters.TmdbApiKey, config.Posters.Size.ToString(), config.Posters.SmallerImages));

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
        config.Repeat.Count = dto.RepeatCount;
        config.Repeat.Monitor = dto.RepeatMonitor;
        config.Repeat.LaunchMonitorAtStartup = dto.LaunchMonitorAtStartup;
        config.Repeat.PollIntervalSeconds = dto.PollIntervalSeconds;
        config.Repeat.QueueEtaFormat = Enum.Parse<QueueEtaDisplayFormat>(dto.QueueEtaFormat);
        config.Arrs.Sonarr = new ArrServiceSettings { Enabled = dto.Sonarr.Enabled, Url = dto.Sonarr.Url, ApiKey = dto.Sonarr.ApiKey };
        config.Arrs.Radarr = new ArrServiceSettings { Enabled = dto.Radarr.Enabled, Url = dto.Radarr.Url, ApiKey = dto.Radarr.ApiKey };
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
            config.Posters.TmdbApiKey = (posters.TmdbApiKey ?? "").Trim();
            config.Posters.Size = Enum.TryParse<PosterSize>(posters.Size, ignoreCase: true, out var posterSize) ? posterSize : PosterSize.Medium;
            config.Posters.SmallerImages = posters.SmallerImages;
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

    public static NotificationChannelDto ToChannelDto(NotificationChannel channel) => new(
        channel.Id, channel.Type, channel.DisplayName, channel.Trigger.ToString(), new Dictionary<string, string>(channel.Settings),
        channel.DigestDailyEnabled, channel.DigestWeeklyEnabled, channel.DigestDailyTime, channel.DigestWeeklyTime, channel.DigestWeeklyDay.ToString());

    public static void ApplyChannelDto(NotificationChannel channel, NotificationChannelDto dto)
    {
        channel.DisplayName = dto.DisplayName;
        channel.Trigger = Enum.Parse<NotificationTrigger>(dto.Trigger);
        channel.Settings = new Dictionary<string, string>(dto.Settings);
        // Type is deliberately not settable via update - a channel's type is fixed at creation
        // (its field schema depends on it); changing type would need a new channel instead.

        channel.DigestDailyEnabled = dto.DigestDailyEnabled;
        channel.DigestWeeklyEnabled = dto.DigestWeeklyEnabled;
        channel.DigestDailyTime = dto.DigestDailyTime;
        channel.DigestWeeklyTime = dto.DigestWeeklyTime;
        channel.DigestWeeklyDay = Enum.Parse<DayOfWeek>(dto.DigestWeeklyDay);
        // LastDailyDigestSentDate/LastWeeklyDigestSentDate are deliberately not in the DTO at all -
        // scheduler-internal bookkeeping, never user-editable, so a Settings save can never reset it.
    }
}
