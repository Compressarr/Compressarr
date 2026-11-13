using Compressarr.Core.Config;

namespace Compressarr.Core.Conversion;

/// <summary>What a lane's kind (3.1) changes about how its files are classified. A Videos lane has no movies or shows: everything in it is a "video".</summary>
public static class LaneKinds
{
    public static bool IsVideos(LaneConfig? lane) => lane?.Kind == LaneKind.Videos;

    /// <summary>Whether a file is a TV episode. Never in a Videos lane; otherwise decided by the file name, as always.</summary>
    public static bool IsTv(LaneConfig? lane, string fileName) => !IsVideos(lane) && ContentClassifier.IsTvFile(fileName);

    /// <summary>"tv", "movie" or "video" (see <see cref="ContentClassifier.KindOf"/>); always "video" in a Videos lane.</summary>
    public static string KindOf(LaneConfig? lane, string fileName) => IsVideos(lane) ? "video" : ContentClassifier.KindOf(fileName);
}
