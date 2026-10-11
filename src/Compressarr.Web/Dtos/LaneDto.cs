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
    bool? AllowHdrLoss = null);

/// <summary>Duplicate a lane: the new lane's name, and the lane as it is currently shown (see POST /api/lanes/duplicate).</summary>
public sealed record DuplicateLaneRequest(string DisplayName, LaneDto Lane);
