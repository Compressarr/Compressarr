namespace Compressarr.Web.Dtos;

public sealed record ValidationIssueDto(string Field, string Message);

public sealed record SettingsDto(
    bool FileBotEnabled,
    string FileBotCliPath,
    bool FileBotTvEnabled,
    string FileBotTvArgs,
    bool FileBotMovieEnabled,
    string FileBotMovieArgs,
    List<string> VidTypes,
    bool OutSameAsIn,
    string DeleteAfterConvert,
    bool MoveFiles,
    bool ClearTitleMetadata,
    int Limit,
    long MinSizeBytes,
    List<string> CompanionExtensions,
    string UnmatchedCompanionAction,
    string OnDestinationCollision,
    string LogFilePath,
    int RetentionDays,
    bool KeepSuccessfulHandBrakeLogs,
    string PostExecCmd,
    string PostExecArgs,
    string ReportPath,
    string OpenAfterRun,
    int RepeatCount,
    bool RepeatMonitor,
    bool LaunchMonitorAtStartup,
    int PollIntervalSeconds,
    string QueueEtaFormat,
    ArrServiceDto Sonarr,
    ArrServiceDto Radarr,
    int WebPort,
    bool RunAtLogin,
    bool? AnnouncementsEnabled,
    string? UpdateChannel,
    string BackupFolderPath,
    int BackupIntervalDays,
    int BackupRetentionDays,
    DateTimeOffset? BackupLastRunUtc,
    List<ValidationIssueDto> ValidationIssues,
    PosterSettingsDto? Posters = null);

/// <summary>Null (an older page that does not know about posters) leaves the saved poster settings as they are.</summary>
public sealed record PosterSettingsDto(bool Enabled, string TmdbApiKey, string Size, bool SmallerImages);

public sealed record ArrServiceDto(bool Enabled, string Url, string ApiKey);
