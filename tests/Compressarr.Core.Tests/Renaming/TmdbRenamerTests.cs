using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Renaming;

namespace Compressarr.Core.Tests.Renaming;

internal sealed class RenamingTestLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten;
    public List<(string Message, LogSeverity Severity)> Logs { get; } = new();
    public Dictionary<string, string> Problems { get; } = new();
    public bool HasLoggedError => Logs.Any(l => l.Severity == LogSeverity.Error);

    public string Initialize(string logFilePath, string timestamp) => "";
    public void Log(string message, LogSeverity severity = LogSeverity.Info) => Logs.Add((message, severity));
    public void LogProblem(string key, string message) { Problems[key] = message; Log(message, LogSeverity.Error); }
    public void ClearProblem(string key) => Problems.Remove(key);
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes) => true;
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

/// <summary>Every video file under the folder, as the real scanner would list them.</summary>
internal sealed class RenamingFolderScanner : IVideoFileScanner
{
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        var extensions = vidTypes.Select(t => "." + t.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories).Where(f => extensions.Contains(Path.GetExtension(f))).Select(f => new FileInfo(f));
        return (limit > 0 ? files.Take(limit) : files).ToList();
    }
}

/// <summary>A TMDB that knows a few films and shows, counts how often it is asked, and can be told to fail.</summary>
internal sealed class FakeTmdbClient : ITmdbRenameClient
{
    public Dictionary<string, List<TmdbCandidate>> Films { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<TmdbCandidate>> Shows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(int Show, int Season), Dictionary<int, string>> Seasons { get; } = new();
    public Dictionary<(bool Tv, int Id), TmdbCandidate> Details { get; } = new();
    public Exception? Fail { get; set; }
    public int SearchCalls { get; private set; }
    public int SeasonCalls { get; private set; }

    public Task<IReadOnlyList<TmdbCandidate>> SearchAsync(string apiKey, bool tv, string title, int? year, string language, CancellationToken cancellationToken)
    {
        SearchCalls++;
        if (Fail is not null) throw Fail;

        var source = tv ? Shows : Films;
        return Task.FromResult<IReadOnlyList<TmdbCandidate>>(source.TryGetValue(title, out var list) ? list : new List<TmdbCandidate>());
    }

    public Task<TmdbCandidate?> GetDetailsAsync(string apiKey, bool tv, int id, string language, CancellationToken cancellationToken)
    {
        if (Fail is not null) throw Fail;
        return Task.FromResult(Details.TryGetValue((tv, id), out var details) ? details : null);
    }

    public Task<IReadOnlyDictionary<int, string>> GetSeasonAsync(string apiKey, int showId, int season, string language, CancellationToken cancellationToken)
    {
        SeasonCalls++;
        if (Fail is not null) throw Fail;

        return Task.FromResult<IReadOnlyDictionary<int, string>>(Seasons.TryGetValue((showId, season), out var episodes) ? episodes : new Dictionary<int, string>());
    }
}

public class TmdbRenamerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("compressarr-tmdb-rename-").FullName;
    private static readonly string[] VidTypes = { "mkv", "mp4" };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TmdbCandidate Film(int id, string title, int year) => new(id, false, title, title, year, Array.Empty<string>(), 1, null, null);
    private static TmdbCandidate Show(int id, string name, int year, params string[] countries) => new(id, true, name, name, year, countries, 1, null, null);

    private string Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static FakeTmdbClient KnownTmdb()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.Films["Heat"] = new() { Film(949, "Heat", 1995), Film(136795, "The Heat", 2013) };
        tmdb.Films["Mission Impossible Fallout"] = new() { Film(353081, "Mission: Impossible - Fallout", 2018) };
        tmdb.Shows["Breaking Bad"] = new() { Show(1396, "Breaking Bad", 2008) };
        tmdb.Seasons[(1396, 1)] = new() { [1] = "Pilot", [2] = "Cat's in the Bag...", [3] = "...And the Bag's in the River" };
        tmdb.Shows["The Office"] = new() { Show(2316, "The Office", 2005, "US"), Show(2996, "The Office", 2001, "GB") };
        tmdb.Seasons[(2316, 2)] = new() { [3] = "Initiation" };
        return tmdb;
    }

    private TmdbRenamer Renamer(FakeTmdbClient tmdb) => new(new RenamingFolderScanner(), tmdb);

    private static TmdbRenameSettings Settings(Action<TmdbRenameSettings>? change = null)
    {
        var settings = new TmdbRenameSettings { Enabled = true };
        change?.Invoke(settings);
        return settings;
    }

    private Task<Compressarr.Core.FileBot.FileBotRunResult> Run(TmdbRenamer renamer, RenamingTestLogger logger, TmdbRenameSettings? settings = null) =>
        renamer.RunAsync(settings ?? Settings(), "key", _root, VidTypes, logger, CancellationToken.None);

    [Fact]
    public async Task AMovieIsRenamedInPlace_AndTheRenameIsReported()
    {
        var file = Touch("Heat.1995.720p.BluRay.mkv");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        var expected = Path.Combine(_root, "Heat (1995).mkv");
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, result.Renames[file]);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public async Task AFileInASubfolderStaysInThatSubfolder()
    {
        var file = Touch(Path.Combine("Heat (1995)", "Heat.1995.720p.mkv"));

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995)", "Heat (1995).mkv")));
        Assert.Equal(Path.Combine(_root, "Heat (1995)", "Heat (1995).mkv"), result.Renames[file]);
    }

    [Fact]
    public async Task CompanionFilesAreRenamedWithTheirVideo_SoTheyAreNotOrphaned()
    {
        // Compressarr finds a video's subtitles and other companions by "the video's name, then a dot".
        Touch("Heat.1995.720p.mkv");
        Touch("Heat.1995.720p.en.srt");
        Touch("Heat.1995.720p.nfo");
        Touch("Heat.1995.720p.mp4");          // another video of the same name is its own file, not a companion
        Touch("Heat.1995.7200p.srt");         // and a file whose name only starts the same way, without the dot, is not one either

        var logger = new RenamingTestLogger();
        await Run(Renamer(KnownTmdb()), logger);

        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).mkv")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).en.srt")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).nfo")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).mp4")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat.1995.7200p.srt")));
        Assert.False(logger.HasLoggedError);          // the .mp4 was renamed as a video in its own right, not swept up as a companion of the .mkv
    }

    [Fact]
    public async Task ACompanionWhoseNewNameIsTakenKeepsItsOwn_AndTheVideoIsStillRenamed()
    {
        Touch("Heat.1995.720p.mkv");
        Touch("Heat.1995.720p.en.srt");
        Touch("Heat (1995).en.srt");

        var logger = new RenamingTestLogger();
        await Run(Renamer(KnownTmdb()), logger);

        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).mkv")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat.1995.720p.en.srt")));
        Assert.Contains(logger.Logs, l => l.Severity == LogSeverity.Error && l.Message.Contains("already exists"));
    }

    [Fact]
    public async Task AFileThatAlreadyHasItsNameIsLeftAloneAndIsNotUnmatched()
    {
        var file = Touch("Heat (1995).mkv");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Empty(result.Renames);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public async Task AFileCarryingATmdbTagIsLeftAloneWithoutAskingTmdb()
    {
        var file = Touch("Whatever {tmdb-123}.mkv");
        var tmdb = KnownTmdb();

        var result = await Run(Renamer(tmdb), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Empty(result.Unmatched);
        Assert.Equal(0, tmdb.SearchCalls);
    }

    [Fact]
    public async Task AMovieWithNoYearIsUnmatched_WithoutAskingTmdb()
    {
        var file = Touch("Heat.mkv");
        var tmdb = KnownTmdb();

        var result = await Run(Renamer(tmdb), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Contains(file, result.Unmatched);
        Assert.Equal(0, tmdb.SearchCalls);
    }

    [Fact]
    public async Task AFilmTmdbDoesNotKnow_IsUnmatchedAndUntouched()
    {
        var file = Touch("Some.Home.Video.2019.mkv");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Contains(file, result.Unmatched);
        Assert.Empty(result.Renames);
    }

    [Fact]
    public async Task AnEditionTagIsKeptOnTheNewName()
    {
        Touch("Heat.1995.Directors.Cut {edition-Director's Cut}.mkv");

        await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995) {edition-Director's Cut}.mkv")));
    }

    [Fact]
    public async Task TheTmdbIdCanBeAddedToAMovieName()
    {
        Touch("Heat.1995.mkv");

        await Run(Renamer(KnownTmdb()), new RenamingTestLogger(), Settings(s => s.AddMovieTmdbId = true));

        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995) {tmdb-949}.mkv")));
    }

    [Fact]
    public async Task ColonsInTheTitleAreDropped()
    {
        Touch("Mission.Impossible.Fallout.2018.1080p.BluRay.x264-GRP.mkv");

        await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(Path.Combine(_root, "Mission Impossible - Fallout (2018).mkv")));
    }

    [Fact]
    public async Task AnEpisodeGetsShowYearNumbersAndTitle_AndASeasonIsAskedForOnlyOnce()
    {
        var one = Touch("Breaking.Bad.S01E01.1080p.mkv");
        var two = Touch("Breaking.Bad.S01E02.1080p.mkv");
        var three = Touch("Breaking.Bad.S01E03.1080p.mkv");
        var tmdb = KnownTmdb();

        var result = await Run(Renamer(tmdb), new RenamingTestLogger());

        Assert.Equal(Path.Combine(_root, "Breaking Bad (2008) - S01E01 - Pilot.mkv"), result.Renames[one]);
        Assert.Equal(Path.Combine(_root, "Breaking Bad (2008) - S01E02 - Cat's in the Bag.mkv"), result.Renames[two]);   // the trailing dots are not allowed in a name
        Assert.Equal(Path.Combine(_root, "Breaking Bad (2008) - S01E03 - ...And the Bag's in the River.mkv"), result.Renames[three]);
        Assert.Equal(1, tmdb.SearchCalls);
        Assert.Equal(1, tmdb.SeasonCalls);
    }

    [Fact]
    public async Task TheNumberingStyleIsTheOneChosen()
    {
        Touch("Breaking.Bad.S01E01.mkv");

        await Run(Renamer(KnownTmdb()), new RenamingTestLogger(), Settings(s => s.Numbering = TvNumbering.SxE));

        Assert.True(File.Exists(Path.Combine(_root, "Breaking Bad (2008) - 1x01 - Pilot.mkv")));
    }

    [Fact]
    public async Task ARegionTagPicksTheRightShow()
    {
        Touch("The.Office.US.S02E03.720p.HDTV.mkv");

        await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(Path.Combine(_root, "The Office (2005) - S02E03 - Initiation.mkv")));
    }

    [Fact]
    public async Task AnEpisodeTmdbDoesNotHaveIsUnmatched()
    {
        var file = Touch("Breaking.Bad.S01E09.mkv");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Contains(file, result.Unmatched);
    }

    [Fact]
    public async Task AnAmbiguousShowIsUnmatched_NotGuessed()
    {
        var file = Touch("The.Office.S02E03.mkv");          // no region tag: two shows of that name

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.True(File.Exists(file));
        Assert.Contains(file, result.Unmatched);
    }

    [Fact]
    public async Task TwoFilesThatWouldGetTheSameNameDoNotOverwriteEachOther()
    {
        Touch("Heat.1995.1080p.mkv");
        Touch("Heat.1995.720p.mkv");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.Single(result.Renames);
        Assert.Empty(result.Unmatched);                      // matched, just not renamed: the name is taken
        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).mkv")));
        Assert.Equal(2, Directory.GetFiles(_root, "*.mkv").Length);
    }

    [Fact]
    public async Task AnExistingFileOfTheNewNameIsNeverOverwritten()
    {
        Touch("Heat.1995.720p.mkv");
        File.WriteAllText(Path.Combine(_root, "Heat (1995).mkv"), "the one already there");

        var result = await Run(Renamer(KnownTmdb()), new RenamingTestLogger());

        Assert.Empty(result.Renames);
        Assert.Equal("the one already there", File.ReadAllText(Path.Combine(_root, "Heat (1995).mkv")));
        Assert.True(File.Exists(Path.Combine(_root, "Heat.1995.720p.mkv")));
    }

    [Fact]
    public async Task WhenTmdbCannotBeReached_NothingIsRenamed_NothingIsCalledUnmatched_AndTheRestIsNotAsked()
    {
        var a = Touch("Heat.1995.mkv");
        var b = Touch("Aliens.1986.mkv");
        var tmdb = KnownTmdb();
        tmdb.Fail = new TmdbUnavailableException();
        var logger = new RenamingTestLogger();

        var result = await Run(Renamer(tmdb), logger);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Empty(result.Renames);
        Assert.Empty(result.Unmatched);                      // nobody knows yet, so no "Unmatched" badge
        Assert.Equal(1, tmdb.SearchCalls);                   // the second file was not tried
        Assert.Contains(logger.Logs, l => l.Severity == LogSeverity.Error && l.Message.Contains("could not be reached"));
    }

    [Fact]
    public async Task ARejectedKeyIsAStandingProblem_ClearedOnceItWorks()
    {
        Touch("Heat.1995.mkv");
        var tmdb = KnownTmdb();
        tmdb.Fail = new TmdbKeyRejectedException();
        var logger = new RenamingTestLogger();
        var renamer = Renamer(tmdb);

        var first = await Run(renamer, logger);

        Assert.Empty(first.Renames);
        var problem = Assert.Single(logger.Problems);
        Assert.Contains("rejected", problem.Value);
        Assert.DoesNotContain("key\"", problem.Value);

        tmdb.Fail = null;
        var second = await Run(renamer, logger);

        Assert.Single(second.Renames);
        Assert.Empty(logger.Problems);
    }

    [Fact]
    public async Task APreviewRenamesNothing_AndSaysWhatWouldHappen()
    {
        var movie = Touch("Heat.1995.720p.mkv");
        var named = Touch("Breaking Bad (2008) - S01E01 - Pilot.mkv");
        var unknown = Touch("Some.Home.Video.2019.mkv");
        var renamer = Renamer(KnownTmdb());

        var plan = await renamer.PreviewAsync(Settings(), "key", _root, VidTypes, limit: 0, CancellationToken.None);

        Assert.True(File.Exists(movie));
        Assert.True(File.Exists(unknown));
        Assert.Equal(RenameKind.Rename, plan.Single(p => p.FullName == movie).Kind);
        Assert.Equal("Heat (1995).mkv", plan.Single(p => p.FullName == movie).NewName);
        Assert.Equal(RenameKind.AlreadyNamed, plan.Single(p => p.FullName == named).Kind);
        Assert.Equal(RenameKind.Unmatched, plan.Single(p => p.FullName == unknown).Kind);
    }

    [Fact]
    public async Task APreviewLooksOnlyAtTheFirstFilesUpToTheLimit()
    {
        for (var i = 1; i <= 5; i++) Touch($"Heat.{1990 + i}.mkv");
        var tmdb = KnownTmdb();

        var plan = await Renamer(tmdb).PreviewAsync(Settings(), "key", _root, VidTypes, limit: 2, CancellationToken.None);

        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public async Task ARenameThatFailsIsLoggedAndRetriedLater_ItIsNotCalledUnmatched()
    {
        if (!OperatingSystem.IsWindows()) return;     // relies on a file lock that blocks a rename
        var file = Touch("Heat.1995.720p.mkv");
        var logger = new RenamingTestLogger();
        var renamer = Renamer(KnownTmdb());

        Compressarr.Core.FileBot.FileBotRunResult locked;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            locked = await Run(renamer, logger);
        }

        Assert.Empty(locked.Renames);
        Assert.Empty(locked.Unmatched);
        Assert.True(File.Exists(file));
        Assert.Contains(logger.Logs, l => l.Severity == LogSeverity.Error && l.Message.Contains("tried again"));

        var later = await Run(renamer, logger);

        Assert.Single(later.Renames);
        Assert.True(File.Exists(Path.Combine(_root, "Heat (1995).mkv")));
    }
}
