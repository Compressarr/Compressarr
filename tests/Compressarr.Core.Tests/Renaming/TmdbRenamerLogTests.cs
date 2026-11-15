using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Renaming;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Renaming;

/// <summary>How the automatic renamer uses the rename log: it writes to it, and it listens to what a person chose.</summary>
public class TmdbRenamerLogTests : AppDataTestBase
{
    private readonly string _input;
    private readonly FakeTmdbClient _tmdb = new();
    private readonly SqliteRenameLog _log = new(new SqliteResumeStateStore());
    private static readonly string[] VidTypes = { "mkv" };

    public TmdbRenamerLogTests()
    {
        _input = Path.Combine(AppData, "In");
        Directory.CreateDirectory(_input);
        _tmdb.Films["Heat"] = new() { new TmdbCandidate(949, false, "Heat", "Heat", 1995, Array.Empty<string>(), 16, null, null) };
        _tmdb.Shows["Breaking Bad"] = new() { new TmdbCandidate(1396, true, "Breaking Bad", "Breaking Bad", 2008, new[] { "US" }, 146, null, null) };
        _tmdb.Seasons[(1396, 1)] = new() { [1] = "Pilot", [2] = "Cat's in the Bag..." };
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_input, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private Task<Compressarr.Core.FileBot.FileBotRunResult> Run() =>
        new TmdbRenamer(new RenamingFolderScanner(), _tmdb, _log, TimeProvider.System)
            .RunAsync(new TmdbRenameSettings { Enabled = true }, "key", _input, VidTypes, new RenamingTestLogger(), CancellationToken.None);

    [Fact]
    public async Task ARenameIsWrittenToTheLog_WithWhatItWasMatchedTo()
    {
        var movie = Touch("Heat.1995.720p.mkv");
        var episode = Touch("Breaking.Bad.S01E02.mkv");

        await Run();

        var film = _log.Find(Path.Combine(_input, "Heat (1995).mkv"))!;
        Assert.Equal(movie, film.OriginalPath);
        Assert.Equal(949, film.TmdbId);
        Assert.False(film.IsTv);
        Assert.Equal(RenameRecord.AutomaticSource, film.Source);

        var show = _log.Find(Path.Combine(_input, "Breaking Bad (2008) - S01E02 - Cat's in the Bag.mkv"))!;
        Assert.Equal(episode, show.OriginalPath);
        Assert.Equal(1396, show.TmdbId);
        Assert.True(show.IsTv);
        Assert.Equal(1, show.Season);
        Assert.Equal(new[] { 2 }, show.Episodes);
    }

    [Fact]
    public async Task AFileLeftAloneIsNotOnTheLog()
    {
        Touch("Heat.mkv");           // no year: left as it is
        Touch("Heat (1995).mkv");    // already named

        await Run();

        Assert.Empty(_log.CurrentPaths());
    }

    [Fact]
    public async Task AMatchAPersonChoseBefore_IsUsed_EvenForAFilmWithNoYear_AndWithoutAskingTmdbForIt()
    {
        _log.Remember(RenameMemoryKey.For(new ParsedName(false, "Heat", null, null, null, Array.Empty<int>(), "")), new RememberedMatch(949, false, "Heat", 1995));
        var path = Touch("Heat.mkv");

        var result = await Run();

        Assert.Equal(Path.Combine(_input, "Heat (1995).mkv"), result.Renames[path]);
        Assert.Empty(result.Unmatched);
        Assert.Equal(0, _tmdb.SearchCalls);
    }

    [Fact]
    public async Task ARememberedShowIsUsedWithoutASearch_ButItsEpisodesAreStillLookedUp()
    {
        _log.Remember(RenameMemoryKey.For(new ParsedName(true, "Breaking Bad", null, null, 1, new[] { 1 }, "")), new RememberedMatch(1396, true, "Breaking Bad", 2008));
        Touch("Breaking.Bad.S01E01.mkv");

        await Run();

        Assert.True(File.Exists(Path.Combine(_input, "Breaking Bad (2008) - S01E01 - Pilot.mkv")));
        Assert.Equal(0, _tmdb.SearchCalls);
        Assert.Equal(1, _tmdb.SeasonCalls);
    }

    [Fact]
    public async Task AFileThatWasMatchedByHand_IsNeverRenamedAgain()
    {
        var path = Touch("Heat.1995.720p.mkv");              // the automatic renamer would call this "Heat (1995).mkv"
        _log.Record(path, path, 136795, false, null, null, RenameRecord.ManualSource);

        var result = await Run();

        Assert.Empty(result.Renames);
        Assert.Empty(result.Unmatched);
        Assert.True(File.Exists(path));
        Assert.Equal(0, _tmdb.SearchCalls);
    }
}
