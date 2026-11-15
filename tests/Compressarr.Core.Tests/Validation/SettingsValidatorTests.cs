using Compressarr.Core.Config;

namespace Compressarr.Core.Tests.Validation;

file sealed class PassThroughPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

public class SettingsValidatorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-settings-validator-tests-").FullName;
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static CompressarrConfig MakeHealthyConfig() => new()
    {
        FileBot = new FileBotSettings { Enabled = false },
        Processing = new ProcessingSettings { VidTypes = new() { "mkv" } }
    };

    [Fact]
    public void Validate_HealthyConfig_ReturnsNoIssues()
    {
        var config = MakeHealthyConfig();

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Empty(issues);
    }

    // The HandBrake path/options checks moved to EncoderValidator (the Encoder page) in 2.2 - a
    // missing HandBrakeCLI must no longer show up as a Settings-page issue.
    [Fact]
    public void Validate_MissingHandBrakeCli_IsNoLongerASettingsIssue()
    {
        var config = MakeHealthyConfig();
        config.HandBrake.CliPath = Path.Combine(_tempDir, "DoesNotExist.exe");

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_FileBotDisabled_MissingFileBotPath_DoesNotFlag()
    {
        var config = MakeHealthyConfig();
        config.FileBot.Enabled = false;
        config.FileBot.CliPath = Path.Combine(_tempDir, "DoesNotExist.exe");

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.DoesNotContain(issues, i => i.Field == "fileBotCliPath");
    }

    [Fact]
    public void Validate_FileBotEnabled_MissingFileBotPath_FlagsField()
    {
        var config = MakeHealthyConfig();
        config.FileBot.Enabled = true;
        config.FileBot.CliPath = Path.Combine(_tempDir, "DoesNotExist.exe");

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Contains(issues, i => i.Field == "fileBotCliPath");
    }

    [Fact]
    public void Validate_FileBotEnabled_ExistingFileBotPath_DoesNotFlag()
    {
        var config = MakeHealthyConfig();
        var fileBotPath = Path.Combine(_tempDir, "filebot.exe");
        File.WriteAllText(fileBotPath, "");
        config.FileBot.Enabled = true;
        config.FileBot.CliPath = fileBotPath;

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.DoesNotContain(issues, i => i.Field == "fileBotCliPath");
    }

    [Fact]
    public void Validate_EmptyVidTypes_FlagsField()
    {
        var config = MakeHealthyConfig();
        config.Processing.VidTypes = new();

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Contains(issues, i => i.Field == "vidTypes");
    }

    [Fact]
    public void Validate_TmdbRenameOff_NeedsNoKey()
    {
        var config = MakeHealthyConfig();
        config.TmdbRename.Enabled = false;

        Assert.Empty(SettingsValidator.Validate(config, new PassThroughPathExpander()));
    }

    [Fact]
    public void Validate_TmdbRenameOn_WithoutAKey_Flags()
    {
        var config = MakeHealthyConfig();
        config.TmdbRename.Enabled = true;

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Contains(issues, i => i.Field == "tmdbApiKey");
    }

    [Fact]
    public void Validate_TmdbRenameOn_WithAKey_IsFine()
    {
        var config = MakeHealthyConfig();
        config.TmdbRename.Enabled = true;
        config.Posters.TmdbApiKey = "abc";

        Assert.Empty(SettingsValidator.Validate(config, new PassThroughPathExpander()));
    }

    [Fact]
    public void Validate_TmdbRenameAndFileBotBothOn_Flags()
    {
        var config = MakeHealthyConfig();
        config.TmdbRename.Enabled = true;
        config.Posters.TmdbApiKey = "abc";
        config.FileBot = new FileBotSettings { Enabled = true, CliPath = Path.Combine(_tempDir, "filebot.exe") };
        File.WriteAllText(config.FileBot.CliPath, "x");

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Contains(issues, i => i.Field == "tmdbRenameEnabled");
    }

    [Fact]
    public void Validate_MultipleSimultaneousProblems_AllAppear()
    {
        var config = new CompressarrConfig
        {
            FileBot = new FileBotSettings { Enabled = true, CliPath = "" },
            Processing = new ProcessingSettings { VidTypes = new() }
        };

        var issues = SettingsValidator.Validate(config, new PassThroughPathExpander());

        Assert.Contains(issues, i => i.Field == "fileBotCliPath");
        Assert.Contains(issues, i => i.Field == "vidTypes");
    }
}
