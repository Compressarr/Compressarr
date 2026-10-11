using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;

namespace Compressarr.Web.Dtos;

/// <summary>The Encoder page's settings: where the encoder tools are and the extra options passed
/// to every encode. The read-only fields (profile counts, lanes using each encoder) are ignored on PUT.
/// The ffmpeg fields are optional on PUT: a client that doesn't send them (an older cached page)
/// leaves the saved ffmpeg settings alone.</summary>
public sealed record EncoderSettingsDto(
    string HandBrakeCliPath,
    string HandBrakeOptions,
    int BuiltInProfileCount,
    int UserProfileCount,
    List<string> HandBrakeLanes,
    List<ValidationIssueDto> ValidationIssues,
    string? FfmpegPath = null,
    string? FfmpegProbePath = null,
    string? FfmpegOptions = null,
    int FfmpegProfileCount = 0,
    List<string>? FfmpegLanes = null,
    string? FfmpegChannel = null);

/// <summary>One row of the Profiles page. Engine is "handbrake" or "ffmpeg". UsedBy lists the lanes
/// (of that engine) whose TV or Movie preset is this profile.</summary>
public sealed record ProfileDto(
    string Engine,
    string Name,
    bool BuiltIn,
    string Container,
    string Video,
    string Audio,
    string Description,
    List<string> UsedBy,
    bool Catalog = false);

public sealed record ProfileListDto(
    List<ProfileDto> Profiles,
    string UserFilePath,
    string ActivePresetsPath,
    string? UserFileError,
    string? FfmpegUserFilePath = null,
    string? FfmpegUserFileError = null,
    string? CatalogHandBrakeFilePath = null,
    string? CatalogFFmpegFilePath = null,
    List<string>? DamagedCatalog = null);

/// <summary>The editor's view of one HandBrake profile: the form fields, whether it is a locked
/// built-in (the editor is then read-only), and which lanes use it.</summary>
public sealed record ProfileEditDto(
    HandBrakeProfileForm Form,
    bool BuiltIn,
    List<string> UsedBy,
    string ActivePresetsPath,
    bool Catalog = false);

/// <summary>Save request. BaseName (create only) names the profile whose settings the new one starts
/// from - everything the form doesn't show is inherited from it.</summary>
public sealed record SaveProfileRequest(HandBrakeProfileForm Form, string? BaseName);

public sealed record DuplicateProfileRequest(string? Name);

public sealed record ImportReadRequest(string Path);

public sealed record ImportRequest(string Path, List<string> Names, string OnConflict);

public sealed record ImportCandidateDto(string Name, string? Group, string Video, string Container, string Status);

public sealed record ImportListingDto(string Path, bool Found, string? Error, List<ImportCandidateDto> Candidates);

/// <summary>The ffmpeg profile editor's view of one profile.</summary>
public sealed record FFmpegProfileEditDto(FFmpegProfile Profile, bool BuiltIn, List<string> UsedBy, bool Catalog = false);

public sealed record FFmpegPreviewRequest(FFmpegProfile Profile, string Path);
