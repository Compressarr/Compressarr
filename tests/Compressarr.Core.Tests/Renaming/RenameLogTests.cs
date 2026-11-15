using Compressarr.Core.Conversion;
using Compressarr.Core.Renaming;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Renaming;

public class RenameLogTests : AppDataTestBase
{
    private string ResumePath => Path.Combine(AppData, "log-test.resume.json");

    private SqliteRenameLog NewLog() => new(new SqliteResumeStateStore(), () => ResumePath);

    [Fact]
    public void ARenameCanBeFound_ByItsCurrentPath_IgnoringCase()
    {
        var log = NewLog();
        log.Record(@"C:\In\Heat.1995.mkv", @"C:\In\Heat (1995).mkv", 949, false, null, null, RenameRecord.AutomaticSource);

        var found = log.Find(@"c:\in\HEAT (1995).MKV");

        Assert.NotNull(found);
        Assert.Equal(@"C:\In\Heat.1995.mkv", found!.OriginalPath);
        Assert.Equal(949, found.TmdbId);
        Assert.Equal(RenameRecord.AutomaticSource, found.Source);
        Assert.Null(log.Find(@"C:\In\Heat.1995.mkv"));      // the old name is not on record any more
    }

    [Fact]
    public void RenamingAgain_MovesTheSameRow_AndKeepsTheFirstName()
    {
        var log = NewLog();
        log.Record(@"C:\In\Show.S01E01.mkv", @"C:\In\Show (2008) - S01E01 - Pilot.mkv", 1, true, 1, new[] { 1 }, RenameRecord.AutomaticSource);

        log.Record(@"C:\In\Show (2008) - S01E01 - Pilot.mkv", @"C:\In\Other (2011) - S01E01 - Winter.mkv", 2, true, 1, new[] { 1 }, RenameRecord.ManualSource);

        var found = log.Find(@"C:\In\Other (2011) - S01E01 - Winter.mkv")!;
        Assert.Equal(@"C:\In\Show.S01E01.mkv", found.OriginalPath);                 // "restore" goes back to the very beginning
        Assert.Equal(2, found.TmdbId);
        Assert.Equal(RenameRecord.ManualSource, found.Source);
        Assert.Equal(new[] { 1 }, found.Episodes);
        Assert.Null(log.Find(@"C:\In\Show (2008) - S01E01 - Pilot.mkv"));
        Assert.Single(log.CurrentPaths());
    }

    [Fact]
    public void AFileCanBeForgotten()
    {
        var log = NewLog();
        log.Record(@"C:\In\a.mkv", @"C:\In\A (2000).mkv", 1, false, null, null, RenameRecord.AutomaticSource);

        log.Forget(@"C:\In\A (2000).mkv");

        Assert.Null(log.Find(@"C:\In\A (2000).mkv"));
        Assert.Empty(log.CurrentPaths());
    }

    [Fact]
    public void ARestoredFileIsOnRecord_ButIsNotOneThatCanBeFixed()
    {
        var log = NewLog();
        log.Record(@"C:\In\a.mkv", @"C:\In\A (2000).mkv", 1, false, null, null, RenameRecord.AutomaticSource);

        log.Record(@"C:\In\A (2000).mkv", @"C:\In\a.mkv", null, false, null, null, RenameRecord.RestoredSource);

        var found = log.Find(@"C:\In\a.mkv")!;
        Assert.True(found.IsHandled);                          // the automatic renamer leaves it alone
        Assert.Equal(@"C:\In\a.mkv", found.OriginalPath);
        Assert.Empty(log.CurrentPaths());                      // and the queue does not offer to fix it
    }

    [Fact]
    public void AHandMadeMatchIsHandled_AnAutomaticOneIsNot()
    {
        var log = NewLog();
        log.Record(@"C:\In\a.mkv", @"C:\In\A (2000).mkv", 1, false, null, null, RenameRecord.AutomaticSource);
        log.Record(@"C:\In\b.mkv", @"C:\In\B (2001).mkv", 2, false, null, null, RenameRecord.ManualSource);
        log.Record(@"C:\In\c.mkv", @"C:\In\C (2002).mkv", null, false, null, null, RenameRecord.FileBotSource);

        Assert.False(log.Find(@"C:\In\A (2000).mkv")!.IsHandled);
        Assert.True(log.Find(@"C:\In\B (2001).mkv")!.IsHandled);
        Assert.False(log.Find(@"C:\In\C (2002).mkv")!.IsHandled);
        Assert.Equal(3, log.CurrentPaths().Count);
    }

    [Fact]
    public void WhatIsOnRecord_SurvivesANewInstance()
    {
        NewLog().Record(@"C:\In\a.mkv", @"C:\In\A (2000).mkv", 7, true, 2, new[] { 3, 4 }, RenameRecord.ManualSource);
        NewLog().Remember("tv|show||", new RememberedMatch(7, true, "Show", 2008));

        var again = NewLog();

        var found = again.Find(@"C:\In\A (2000).mkv")!;
        Assert.Equal(7, found.TmdbId);
        Assert.True(found.IsTv);
        Assert.Equal(2, found.Season);
        Assert.Equal(new[] { 3, 4 }, found.Episodes);
        Assert.Equal(new RememberedMatch(7, true, "Show", 2008), again.Recall("tv|show||"));
    }

    [Fact]
    public void ARememberedMatchIsReplacedByANewerOne_AndCanBeForgotten()
    {
        var log = NewLog();
        log.Remember("tv|doctorwho||", new RememberedMatch(121, true, "Doctor Who", 1963));
        log.Remember("tv|doctorwho||", new RememberedMatch(57243, true, "Doctor Who", 2005));

        Assert.Equal(57243, log.Recall("tv|doctorwho||")!.TmdbId);

        log.ForgetMemory("tv|doctorwho||");

        Assert.Null(log.Recall("tv|doctorwho||"));
    }

    [Fact]
    public void ThePathKeyIsTheSameShowWrittenTheSameWay()
    {
        var a = new ParsedName(true, "Doctor Who", 2005, null, 1, new[] { 1 }, "");
        var b = new ParsedName(true, "doctor who", 2005, null, 9, new[] { 4 }, "");           // another episode, another case
        var other = new ParsedName(true, "Doctor Who", 1963, null, 1, new[] { 1 }, "");
        var film = new ParsedName(false, "Doctor Who", 2005, null, null, Array.Empty<int>(), "");

        Assert.Equal(RenameMemoryKey.For(a), RenameMemoryKey.For(b));
        Assert.NotEqual(RenameMemoryKey.For(a), RenameMemoryKey.For(other));
        Assert.NotEqual(RenameMemoryKey.For(a), RenameMemoryKey.For(film));
    }
}
