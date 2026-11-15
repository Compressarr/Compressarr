using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;
using Compressarr.Core.Renaming;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Renaming;

internal sealed class IdentityExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

public class TmdbMatchServiceTests : AppDataTestBase
{
    private readonly string _input;
    private readonly FakeTmdbClient _tmdb = new();
    private readonly SqliteResumeStateStore _store = new();
    private readonly SqliteRenameLog _log;
    private readonly QueueService _queue;
    private readonly TmdbMatchService _service;
    private readonly CompressarrConfig _config = new();
    private readonly LaneConfig _lane;

    public TmdbMatchServiceTests()
    {
        _input = Path.Combine(AppData, "In");
        Directory.CreateDirectory(_input);
        _lane = new LaneConfig { Id = "lane1", DisplayName = "Lane", Input = _input, Output = Path.Combine(AppData, "Out"), MoviePreset = "p", TvPreset = "p" };
        _config.Lanes.Add(_lane);
        _config.Posters.TmdbApiKey = "key";
        _log = new SqliteRenameLog(_store);
        _queue = new QueueService(_store, new IdentityExpander(), new RenamingFolderScanner());
        _service = new TmdbMatchService(_tmdb, _log, _queue, _store, new IdentityExpander());

        _tmdb.Details[(false, 949)] = new TmdbCandidate(949, false, "Heat", "Heat", 1995, Array.Empty<string>(), 16, null, null);
        _tmdb.Details[(false, 136795)] = new TmdbCandidate(136795, false, "The Heat", "The Heat", 2013, Array.Empty<string>(), 4, null, null);
        _tmdb.Details[(true, 57243)] = new TmdbCandidate(57243, true, "Doctor Who", "Doctor Who", 2005, new[] { "GB" }, 1700, null, null);
        _tmdb.Details[(true, 121)] = new TmdbCandidate(121, true, "Doctor Who", "Doctor Who", 1963, new[] { "GB" }, 100, null, null);
        _tmdb.Seasons[(57243, 1)] = new() { [1] = "Rose", [2] = "The End of the World", [3] = "The Unquiet Dead" };
        _tmdb.Seasons[(121, 1)] = new() { [1] = "An Unearthly Child", [2] = "The Cave of Skulls" };
        _tmdb.Films["Heat"] = new() { _tmdb.Details[(false, 949)], _tmdb.Details[(false, 136795)] };
    }

    private string Touch(string relativePath, bool tracked = true, bool unmatched = true, int order = 0)
    {
        var path = Path.Combine(_input, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        if (tracked)
        {
            _store.Update(AppPaths.GetResumeFilePath(), state =>
            {
                state.Add(new ResumeEntry { LaneId = "lane1", FullName = path, Status = ResumeStatus.Pending, Order = order, FileBotUnmatched = unmatched });
                return true;
            });
        }

        return path;
    }

    private ResumeEntry? EntryOf(string path) =>
        _store.Load(AppPaths.GetResumeFilePath()).FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

    private static MatchApplyRequest Movie(string path, int id) => new("lane1", path, id, false, null, null, false);

    private static MatchApplyRequest Episode(string path, int id, int season, int[] episodes, bool others = false) => new("lane1", path, id, true, season, episodes, others);

    // ---- the candidates ----

    [Fact]
    public async Task TheDialogStartsFromWhatTheFileNameSays_AndListsWhatTmdbFound()
    {
        var path = Touch("Heat.mkv");

        var result = await _service.CandidatesAsync(_config, "lane1", path, null, null, null, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Heat", result.Suggestion!.Title);
        Assert.Null(result.Suggestion.Year);
        Assert.False(result.Suggestion.IsTv);
        Assert.Equal(new[] { 949, 136795 }, result.Candidates!.Select(c => c.Id));
        Assert.False(result.IsFix);
    }

    [Fact]
    public async Task ATypedSearchIsExactlyWhatWasTyped()
    {
        var path = Touch("Heat.mkv");
        _tmdb.Films["Heat 1995"] = new() { _tmdb.Details[(false, 949)] };

        var result = await _service.CandidatesAsync(_config, "lane1", path, "Heat 1995", null, false, null, CancellationToken.None);

        Assert.Equal(new[] { 949 }, result.Candidates!.Select(c => c.Id));
    }

    [Fact]
    public async Task AnEmptySearchSaysSo_AndAsksTmdbNothing()
    {
        var path = Touch("Heat.mkv");

        var result = await _service.CandidatesAsync(_config, "lane1", path, "  ", null, false, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Candidates!);
        Assert.Equal(0, _tmdb.SearchCalls);
    }

    [Fact]
    public async Task WithoutAKey_NothingIsAsked()
    {
        var path = Touch("Heat.mkv");
        _config.Posters.TmdbApiKey = "";

        var result = await _service.CandidatesAsync(_config, "lane1", path, null, null, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("TMDB API key", result.Message);
        Assert.Equal(0, _tmdb.SearchCalls);
    }

    [Fact]
    public async Task ARejectedKeyAndAnUnreachableTmdb_AreSaidPlainly()
    {
        var path = Touch("Heat.mkv");

        _tmdb.Fail = new TmdbKeyRejectedException();
        Assert.Contains("rejected", (await _service.CandidatesAsync(_config, "lane1", path, null, null, null, null, CancellationToken.None)).Message);

        _tmdb.Fail = new TmdbUnavailableException();
        Assert.Contains("could not be reached", (await _service.CandidatesAsync(_config, "lane1", path, null, null, null, null, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task OnlyAWaitingVideoInTheLanesInputFolderIsEverTouched()
    {
        var inside = Touch("Heat.1995.mkv");
        var outside = Path.Combine(AppData, "elsewhere.mkv");
        File.WriteAllText(outside, "x");
        var notVideo = Touch("Heat.txt", tracked: false);
        _store.Update(AppPaths.GetResumeFilePath(), state => { state.Add(new ResumeEntry { LaneId = "lane1", FullName = Path.Combine(_input, "Done (2000).mkv"), Status = ResumeStatus.Completed }); return true; });
        File.WriteAllText(Path.Combine(_input, "Done (2000).mkv"), "x");
        _config.Processing.VidTypes = new List<string> { "mkv" };

        Assert.Contains("not in the lane's Input", (await _service.ApplyAsync(_config, Movie(outside, 949), null, CancellationToken.None)).Message);
        Assert.Contains("not one of the lane's video types", (await _service.ApplyAsync(_config, Movie(notVideo, 949), null, CancellationToken.None)).Message);
        Assert.Contains("no longer waiting", (await _service.ApplyAsync(_config, Movie(Path.Combine(_input, "Done (2000).mkv"), 949), null, CancellationToken.None)).Message);
        Assert.Contains("no longer there", (await _service.ApplyAsync(_config, Movie(Path.Combine(_input, "gone.mkv"), 949), null, CancellationToken.None)).Message);
        Assert.Contains("no longer exists", (await _service.ApplyAsync(_config, Movie(inside, 949) with { LaneId = "nope" }, null, CancellationToken.None)).Message);
        Assert.True(File.Exists(inside));
    }

    [Fact]
    public async Task TheFileBeingEncodedIsRefused()
    {
        var path = Touch("Heat.1995.mkv");

        var result = await _service.ApplyAsync(_config, Movie(path, 949), currentFileFullName: path.ToUpperInvariant(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("being encoded", result.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AVideosLaneIsNeverRenamed()
    {
        var path = Touch("Heat.1995.mkv");
        _lane.Kind = LaneKind.Videos;

        var result = await _service.ApplyAsync(_config, Movie(path, 949), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(path));
    }

    // ---- applying a choice ----

    [Fact]
    public async Task AChosenFilm_RenamesTheFile_KeepsItsPlaceInLine_AndClearsTheBadge()
    {
        var other = Touch("Aaa.mkv", unmatched: false, order: 0);
        var path = Touch("Heat.mkv", order: 1);
        _store.Update(AppPaths.GetResumeFilePath(), state => { var e = state.Single(x => x.FullName == path); e.Skipped = true; e.PresetOverride = "Mine"; return true; });
        File.WriteAllText(Path.Combine(_input, "Heat.en.srt"), "sub");

        var result = await _service.ApplyAsync(_config, Movie(path, 949), null, CancellationToken.None);

        var renamed = Path.Combine(_input, "Heat (1995).mkv");
        Assert.True(result.Success, result.Message);
        Assert.Equal("Heat (1995).mkv", result.NewName);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_input, "Heat (1995).en.srt")));       // its subtitle goes with it
        Assert.Null(EntryOf(path));
        var entry = EntryOf(renamed)!;
        Assert.Equal(1, entry.Order);
        Assert.True(entry.Skipped);
        Assert.Equal("Mine", entry.PresetOverride);
        Assert.False(entry.FileBotUnmatched);
        Assert.Equal(0, EntryOf(other)!.Order);
        var record = _log.Find(renamed)!;
        Assert.Equal(path, record.OriginalPath);
        Assert.Equal(RenameRecord.ManualSource, record.Source);
        Assert.Equal(949, record.TmdbId);
    }

    [Fact]
    public async Task TheChoiceIsRemembered_SoTheNextFileOfThatNameFollowsIt()
    {
        var path = Touch("Heat.mkv");

        await _service.ApplyAsync(_config, Movie(path, 949), null, CancellationToken.None);

        var remembered = _log.Recall(RenameMemoryKey.For(new ParsedName(false, "Heat", null, null, null, Array.Empty<int>(), "")));
        Assert.Equal(new RememberedMatch(949, false, "Heat", 1995), remembered);
    }

    [Fact]
    public async Task ATitleTmdbCallsByAnotherNameKeepsTmdbsName_NotTheFilesAndNeverTheClients()
    {
        var path = Touch("whatever.mkv");

        var result = await _service.ApplyAsync(_config, Movie(path, 136795), null, CancellationToken.None);

        Assert.Equal("The Heat (2013).mkv", result.NewName);      // from TMDB's own record of id 136795
    }

    [Fact]
    public async Task ANameThatIsTaken_IsNeverOverwritten()
    {
        var path = Touch("Heat.mkv");
        File.WriteAllText(Path.Combine(_input, "Heat (1995).mkv"), "the one already there");

        var result = await _service.ApplyAsync(_config, Movie(path, 949), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("already there", result.Message);
        Assert.True(File.Exists(path));
        Assert.Equal("the one already there", File.ReadAllText(Path.Combine(_input, "Heat (1995).mkv")));
        Assert.True(EntryOf(path)!.FileBotUnmatched);                  // nothing changed
    }

    [Fact]
    public async Task APickThatMakesNoChangeToTheNameJustMarksItMatched()
    {
        var path = Touch("Heat (1995).mkv");

        var result = await _service.ApplyAsync(_config, Movie(path, 949), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(path));
        Assert.False(EntryOf(path)!.FileBotUnmatched);
    }

    [Fact]
    public async Task AnEpisodeNeedsItsSeasonAndEpisode_AndTmdbMustHaveThem()
    {
        var path = Touch("show.mkv");

        var missing = await _service.ApplyAsync(_config, new MatchApplyRequest("lane1", path, 57243, true, null, null, false), null, CancellationToken.None);
        var absent = await _service.ApplyAsync(_config, Episode(path, 57243, 1, new[] { 9 }), null, CancellationToken.None);
        var good = await _service.ApplyAsync(_config, Episode(path, 57243, 1, new[] { 1 }), null, CancellationToken.None);

        Assert.Contains("Say which season", missing.Message);
        Assert.Contains("no episode 9", absent.Message);
        Assert.Equal("Doctor Who (2005) - S01E01 - Rose.mkv", good.NewName);
    }

    [Fact]
    public async Task AChoiceCanCarryToTheOtherWaitingEpisodesOfTheShow_OnlyThoseThatAreNotAlreadyHandled()
    {
        var one = Touch("Doctor.Who.S01E01.720p.mkv");
        var two = Touch("Doctor.Who.S01E02.720p.mkv");
        var three = Touch("Doctor.Who.S01E03.720p.mkv");
        var handled = Touch("Doctor.Who.S01E04.720p.mkv");
        var elsewhere = Touch("Some.Other.Show.S01E01.mkv");
        _log.Record(handled, handled, 121, true, 1, new[] { 4 }, RenameRecord.ManualSource);          // somebody already chose this one

        var candidates = await _service.CandidatesAsync(_config, "lane1", one, null, null, null, null, CancellationToken.None);
        Assert.Equal(new[] { "Doctor.Who.S01E02.720p.mkv", "Doctor.Who.S01E03.720p.mkv" }, candidates.OtherFiles!.OrderBy(n => n));

        var result = await _service.ApplyAsync(_config, Episode(one, 57243, 1, new[] { 1 }, others: true), null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.OthersRenamed);
        Assert.True(File.Exists(Path.Combine(_input, "Doctor Who (2005) - S01E01 - Rose.mkv")));
        Assert.True(File.Exists(Path.Combine(_input, "Doctor Who (2005) - S01E02 - The End of the World.mkv")));
        Assert.True(File.Exists(Path.Combine(_input, "Doctor Who (2005) - S01E03 - The Unquiet Dead.mkv")));
        Assert.True(File.Exists(handled));                             // a choice somebody made is never overruled
        Assert.True(File.Exists(elsewhere));
        Assert.All(new[] { two, three }, p => Assert.Null(EntryOf(p)));
        Assert.False(EntryOf(Path.Combine(_input, "Doctor Who (2005) - S01E02 - The End of the World.mkv"))!.FileBotUnmatched);
    }

    [Fact]
    public async Task AnOtherEpisodeTmdbDoesNotHave_IsReported_NotRenamed_AndTheRestStillAre()
    {
        var one = Touch("Doctor.Who.S01E01.mkv");
        var nine = Touch("Doctor.Who.S01E09.mkv");

        var result = await _service.ApplyAsync(_config, Episode(one, 57243, 1, new[] { 1 }, others: true), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.OthersRenamed);
        Assert.True(File.Exists(nine));
        Assert.Contains(result.Problems!, p => p.Contains("no episode 9"));
    }

    // ---- fixing a wrong match, and putting a name back ----

    [Fact]
    public async Task AFileTheRenamerRenamed_CanBeFixed_FromItsOriginalName()
    {
        var original = Touch("Doctor.Who.S01E01.720p.mkv", unmatched: false);
        var wrong = RenameAsTmdb(original, 57243);          // matched to the 2005 show, when it is the 1963 one

        var candidates = await _service.CandidatesAsync(_config, "lane1", wrong, null, null, null, null, CancellationToken.None);
        Assert.True(candidates.IsFix);
        Assert.Equal("Doctor.Who.S01E01.720p.mkv", candidates.OriginalName);
        Assert.Equal("Doctor Who", candidates.Suggestion!.Title);                  // searched from the original name, not the new one
        Assert.Equal(57243, candidates.CurrentTmdbId);

        var fixedResult = await _service.ApplyAsync(_config, Episode(wrong, 121, 1, new[] { 1 }), null, CancellationToken.None);

        Assert.True(fixedResult.Success, fixedResult.Message);
        var right = Path.Combine(_input, "Doctor Who (1963) - S01E01 - An Unearthly Child.mkv");
        Assert.True(File.Exists(right));
        Assert.False(File.Exists(wrong));
        var record = _log.Find(right)!;
        Assert.Equal(original, record.OriginalPath);                                // still the very first name
        Assert.Equal(121, record.TmdbId);
        Assert.Equal(RenameRecord.ManualSource, record.Source);
    }

    [Fact]
    public async Task AWholeShowMatchedInOneGo_CanBeFixedInOneGo()
    {
        var one = Touch("Doctor.Who.S01E01.720p.mkv");
        var two = Touch("Doctor.Who.S01E02.720p.mkv");
        var first = await _service.ApplyAsync(_config, Episode(one, 57243, 1, new[] { 1 }, others: true), null, CancellationToken.None);   // all of it matched to the 2005 show
        Assert.Equal(1, first.OthersRenamed);
        var wrongOne = Path.Combine(_input, "Doctor Who (2005) - S01E01 - Rose.mkv");

        var candidates = await _service.CandidatesAsync(_config, "lane1", wrongOne, null, null, null, null, CancellationToken.None);
        Assert.True(candidates.IsFix);
        Assert.Equal(new[] { "Doctor Who (2005) - S01E02 - The End of the World.mkv" }, candidates.OtherFiles);      // the rest of the batch follows the fix

        var fixedAll = await _service.ApplyAsync(_config, Episode(wrongOne, 121, 1, new[] { 1 }, others: true), null, CancellationToken.None);

        Assert.True(fixedAll.Success, fixedAll.Message);
        Assert.Equal(1, fixedAll.OthersRenamed);
        Assert.True(File.Exists(Path.Combine(_input, "Doctor Who (1963) - S01E01 - An Unearthly Child.mkv")));
        Assert.True(File.Exists(Path.Combine(_input, "Doctor Who (1963) - S01E02 - The Cave of Skulls.mkv")));
        Assert.Equal(two, _log.Find(Path.Combine(_input, "Doctor Who (1963) - S01E02 - The Cave of Skulls.mkv"))!.OriginalPath);
    }

    [Fact]
    public async Task AnEpisodeSomebodyMatchedToADifferentTitle_IsNotOverruledByAFix()
    {
        var one = Touch("Doctor.Who.S01E01.720p.mkv");
        var two = Touch("Doctor.Who.S01E02.720p.mkv");
        var wrongOne = RenameAsTmdb(one, 57243);
        var chosenTwo = Path.Combine(_input, "Doctor Who (1963) - S01E02 - The Cave of Skulls.mkv");
        File.Move(two, chosenTwo);
        _log.Record(two, chosenTwo, 121, true, 1, new[] { 2 }, RenameRecord.ManualSource);              // a person put this one with the 1963 show

        var candidates = await _service.CandidatesAsync(_config, "lane1", wrongOne, null, null, null, null, CancellationToken.None);

        Assert.Empty(candidates.OtherFiles!);
    }

    [Fact]
    public async Task AnEpisodePutBackByHand_IsOfferedToFollowTheShowsChoice()
    {
        var one = Touch("Doctor.Who.S01E01.720p.mkv", unmatched: false);
        var two = Touch("Doctor.Who.S01E02.720p.mkv", unmatched: false);
        var renamedTwo = RenameAsTmdb(two, 57243, episode: 2);
        _service.Restore(_config, "lane1", renamedTwo, null);                                           // put back, so waiting and Unmatched again

        var candidates = await _service.CandidatesAsync(_config, "lane1", one, null, null, null, null, CancellationToken.None);

        Assert.Equal(new[] { "Doctor.Who.S01E02.720p.mkv" }, candidates.OtherFiles);
    }

    [Fact]
    public async Task RestoreTurnsTheOriginalNameBack_ForgetsTheChoice_AndLeavesTheFileUnmatched()
    {
        var original = Touch("Doctor.Who.S01E01.720p.mkv", unmatched: false);
        File.WriteAllText(Path.Combine(_input, "Doctor.Who.S01E01.720p.en.srt"), "sub");
        var renamed = RenameAsTmdb(original, 57243);
        _log.Remember(RenameMemoryKey.For(ReleaseNameParser.Parse(original, _input)!), new RememberedMatch(57243, true, "Doctor Who", 2005));

        var result = _service.Restore(_config, "lane1", renamed, null);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(renamed));
        Assert.True(File.Exists(Path.Combine(_input, "Doctor.Who.S01E01.720p.en.srt")));
        Assert.True(EntryOf(original)!.FileBotUnmatched);
        Assert.Null(_log.Recall(RenameMemoryKey.For(ReleaseNameParser.Parse(original, _input)!)));
        var record = _log.Find(original)!;
        Assert.True(record.IsHandled);                                              // the automatic renamer leaves it alone now
        Assert.Empty(_log.CurrentPaths());                                          // and it is not offered for fixing
    }

    [Fact]
    public async Task ARestoredFileIsNotRenamedAgainByTheAutomaticRenamer()
    {
        var original = Touch("Heat.1995.720p.mkv", unmatched: false);
        _tmdb.Films["Heat"] = new() { _tmdb.Details[(false, 949)] };
        var renamed = RenameAsTmdb(original, 949, film: true);
        _service.Restore(_config, "lane1", renamed, null);
        var renamer = new TmdbRenamer(new RenamingFolderScanner(), _tmdb, _log, TimeProvider.System);

        var result = await renamer.RunAsync(new TmdbRenameSettings { Enabled = true }, "key", _input, new[] { "mkv" }, new RenamingTestLogger(), CancellationToken.None);

        Assert.Empty(result.Renames);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void RestoreOnAFileNobodyRenamed_OrWhoseOldNameIsTaken_IsRefused()
    {
        var plain = Touch("Heat.1995.mkv");
        Assert.Contains("did not rename", _service.Restore(_config, "lane1", plain, null).Message);

        var original = Touch("Doctor.Who.S01E01.mkv", unmatched: false);
        var renamed = RenameAsTmdb(original, 57243);
        File.WriteAllText(original, "someone put a new file there");
        var result = _service.Restore(_config, "lane1", renamed, null);

        Assert.False(result.Success);
        Assert.Contains("already there", result.Message);
        Assert.True(File.Exists(renamed));
    }

    [Fact]
    public void ANameFileBotMovedToAnotherFolderCannotBeRestoredHere()
    {
        var folder = Path.Combine(_input, "Sorted");
        Directory.CreateDirectory(folder);
        var moved = Path.Combine(folder, "Heat (1995).mkv");
        File.WriteAllText(moved, "x");
        _log.Record(Path.Combine(_input, "heat.mkv"), moved, null, false, null, null, RenameRecord.FileBotSource);

        var result = _service.Restore(_config, "lane1", moved, null);

        Assert.False(result.Success);
        Assert.Contains("another folder", result.Message);
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void LeaveAsIsClearsTheBadge_AndNothingElse()
    {
        var path = Touch("video1.mkv");

        var result = _service.LeaveAsIs(_config, "lane1", path, null);

        Assert.True(result.Success);
        Assert.True(File.Exists(path));
        Assert.False(EntryOf(path)!.FileBotUnmatched);
    }

    /// <summary>Renames the file the way the automatic renamer would have, and records it.</summary>
    private string RenameAsTmdb(string original, int tmdbId, bool film = false, int episode = 1)
    {
        var details = _tmdb.Details[(!film, tmdbId)];
        var name = film ? $"{details.Title} ({details.Year}).mkv" : $"{details.Title} ({details.Year}) - S01E{episode:00} - {_tmdb.Seasons[(tmdbId, 1)][episode]}.mkv";
        var renamed = Path.Combine(Path.GetDirectoryName(original)!, name);
        File.Move(original, renamed);
        foreach (var companion in Directory.GetFiles(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + ".*").Where(f => !f.EndsWith(".mkv")))
            File.Move(companion, Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(name) + "." + Path.GetFileName(companion)[(Path.GetFileNameWithoutExtension(original).Length + 1)..]));
        _queue.ApplyRename(_config, "lane1", original, renamed, unmatched: false);
        _log.Record(original, renamed, tmdbId, !film, film ? null : 1, film ? null : new[] { episode }, RenameRecord.AutomaticSource);
        return renamed;
    }
}
