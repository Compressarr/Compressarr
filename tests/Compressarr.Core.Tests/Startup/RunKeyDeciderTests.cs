using Compressarr.Core.Startup;

namespace Compressarr.Core.Tests.Startup;

public class RunKeyDeciderTests
{
    private const string Installed = @"C:\Program Files\Compressarr\Compressarr.Desktop.exe";
    private const string DevBuild = @"C:\src\Compressarr\bin\Debug\Compressarr.Desktop.exe";

    private static bool Exists(string path) => path == Installed;

    [Fact]
    public void SettingOn_NoEntry_CreatesIt()
    {
        Assert.Equal(RunKeyAction.Set, RunKeyDecider.Decide(true, null, Installed, Exists));
    }

    [Fact]
    public void SettingOn_AnEntryThatStillWorks_IsLeftAlone_EvenWhenRunFromAnotherCopy()
    {
        // A build run from source must not repoint the installed copy's login entry (nor add the flag to it).
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(true, $"\"{Installed}\"", DevBuild, Exists));
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(true, RunKeyDecider.EntryFor(Installed), DevBuild, Exists));
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(true, RunKeyDecider.EntryFor(Installed), Installed, Exists));
    }

    [Fact]
    public void TheEntryPassesTheAutostartFlag_SoALoginStartCanBeToldFromTheUserOpeningTheApp()
    {
        Assert.Equal($"\"{Installed}\" --autostart", RunKeyDecider.EntryFor(Installed));
    }

    [Fact]
    public void AnEntryFromBeforeTheFlag_ForThisExe_IsBroughtUpToDate_OncePerInstall()
    {
        Assert.Equal(RunKeyAction.Set, RunKeyDecider.Decide(true, $"\"{Installed}\"", Installed, Exists));
        Assert.Equal(RunKeyAction.Set, RunKeyDecider.Decide(true, Installed, Installed, Exists));                     // an older, unquoted entry
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(true, $"\"{Installed}\" --AUTOSTART", Installed, Exists)); // already has it
    }

    [Fact]
    public void SettingOn_AnEntryPointingAtNothing_IsRepointedAtThisExe()
    {
        Assert.Equal(RunKeyAction.Set, RunKeyDecider.Decide(true, @"""D:\Gone\Compressarr.Desktop.exe""", Installed, Exists));
    }

    [Fact]
    public void SettingOff_RemovesAnyEntry_AndDoesNothingWhenThereIsNone()
    {
        Assert.Equal(RunKeyAction.Delete, RunKeyDecider.Decide(false, $"\"{Installed}\"", Installed, Exists));
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(false, null, Installed, Exists));
    }

    [Fact]
    public void WithoutAKnownExe_NothingIsWritten()
    {
        Assert.Equal(RunKeyAction.None, RunKeyDecider.Decide(true, null, null, Exists));
    }
}
