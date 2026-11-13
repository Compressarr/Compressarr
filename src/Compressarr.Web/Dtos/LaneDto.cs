namespace Compressarr.Web.Dtos;

public sealed record LaneDto(
    string Id,
    string DisplayName,
    bool Enabled,
    string Input,
    string Output,
    string TvPreset,
    string MoviePreset,
    string TvShowBasePath,
    string MovieBasePath,
    List<ValidationIssueDto> ValidationIssues,
    string? Engine = null,
    bool? AllowHdrLoss = null,
    bool? UseGlobalProcessing = null,
    LaneProcessingDto? Processing = null,
    string? Kind = null,
    string? Tracking = null);

/// <summary>A lane's own file-handling settings (3.1): the Settings &gt; Processing fields a lane can override. The per-pass limit and "output next to input" stay global.</summary>
public sealed record LaneProcessingDto(
    List<string> VidTypes,
    string DeleteAfterConvert,
    bool MoveFiles,
    bool ClearTitleMetadata,
    long MinSizeBytes,
    List<string> CompanionExtensions,
    string UnmatchedCompanionAction,
    string OnDestinationCollision);

/// <summary>Duplicate a lane: the new lane's name, and the lane as it is currently shown (see POST /api/lanes/duplicate).</summary>
public sealed record DuplicateLaneRequest(string DisplayName, LaneDto Lane);
