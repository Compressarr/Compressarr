using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Presets;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The Encoder and Profiles pages through the real HTTP surface: the tool settings that
/// moved out of Settings, the profile list (Compressarr's own built-ins plus the user's), the preset
/// names lanes and the queue pick from, and the old presets.json path/Install/Reload endpoints being
/// gone.</summary>
public class EncoderEndpointTests
{
    private static readonly LaneSpec Lane1 = new("lane1", "Lane One");

    private static JsonObject UserProfile(string name) => new()
    {
        ["PresetName"] = name,
        ["FileFormat"] = "av_mp4",
        ["Folder"] = false
    };

    [Fact]
    public async Task GetEncoder_ReportsTheToolSettings_TheProfileCounts_AndAMissingCli()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        // whether the default HandBrakeCLI location exists depends on the machine running the tests
        var missing = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        missing["handBrakeCliPath"] = Path.Combine(host.Root, "nowhere", "HandBrakeCLI.exe");
        (await host.Client.PutAsJsonAsync("/api/encoder", missing)).EnsureSuccessStatusCode();

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;

        Assert.False(string.IsNullOrWhiteSpace(dto["handBrakeCliPath"]!.GetValue<string>()));
        Assert.Equal("", dto["handBrakeOptions"]!.GetValue<string>());
        Assert.Equal(2, dto["builtInProfileCount"]!.GetValue<int>());
        Assert.Equal(0, dto["userProfileCount"]!.GetValue<int>());
        Assert.Contains("Lane One", dto["handBrakeLanes"]!.AsArray().Select(n => n!.GetValue<string>()));
        // the CLI isn't where the settings say, and the page must say so
        Assert.Contains(dto["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "handBrakeCliPath");
    }

    [Fact]
    public async Task PutEncoder_SavesThePathAndOptions_AndValidatesThem()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var cli = Path.Combine(host.Root, "HandBrakeCLI.exe");
        File.WriteAllText(cli, "");

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        dto["handBrakeCliPath"] = cli;
        dto["handBrakeOptions"] = "--two-pass";
        using var response = await host.Client.PutAsJsonAsync("/api/encoder", dto);
        var saved = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(saved["validationIssues"]!.AsArray());
        var reread = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        Assert.Equal(cli, reread["handBrakeCliPath"]!.GetValue<string>());
        Assert.Equal("--two-pass", reread["handBrakeOptions"]!.GetValue<string>());

        dto["handBrakeOptions"] = "--custom \"unterminated";
        using var bad = await host.Client.PutAsJsonAsync("/api/encoder", dto);
        var flagged = (await bad.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(flagged["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "handBrakeOptions");
    }

    [Fact]
    public async Task TheFfmpegChannel_DefaultsToStable_IsSaved_AndAnOlderPageThatSendsNoChannelLeavesItAlone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        Assert.Equal("stable", dto["ffmpegChannel"]!.GetValue<string>());

        dto["ffmpegChannel"] = "latest";
        (await host.Client.PutAsJsonAsync("/api/encoder", dto)).EnsureSuccessStatusCode();
        Assert.Equal("latest", (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!["ffmpegChannel"]!.GetValue<string>());

        dto.Remove("ffmpegChannel"); // a cached older page
        (await host.Client.PutAsJsonAsync("/api/encoder", dto)).EnsureSuccessStatusCode();
        Assert.Equal("latest", (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!["ffmpegChannel"]!.GetValue<string>());

        dto["ffmpegChannel"] = "nonsense"; // an unknown value is ignored, not an error
        (await host.Client.PutAsJsonAsync("/api/encoder", dto)).EnsureSuccessStatusCode();
        Assert.Equal("latest", (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!["ffmpegChannel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_NoLongerCarriesTheHandBrakeFields_AndSavingItLeavesThemAlone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var encoder = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        encoder["handBrakeCliPath"] = @"C:\Tools\HandBrakeCLI.exe";
        encoder["handBrakeOptions"] = "--two-pass";
        (await host.Client.PutAsJsonAsync("/api/encoder", encoder)).EnsureSuccessStatusCode();

        var settings = (await host.Client.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        Assert.False(settings.ContainsKey("presetsPath"));
        Assert.False(settings.ContainsKey("handBrakeCliPath"));
        Assert.False(settings.ContainsKey("handBrakeOptions"));
        Assert.DoesNotContain(settings["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "handBrakeCliPath");

        // Saving the Settings page (which no longer sends those fields) must not reset them
        (await host.Client.PutAsJsonAsync("/api/settings", settings)).EnsureSuccessStatusCode();
        var after = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        Assert.Equal(@"C:\Tools\HandBrakeCLI.exe", after["handBrakeCliPath"]!.GetValue<string>());
        Assert.Equal("--two-pass", after["handBrakeOptions"]!.GetValue<string>());
    }

    [Fact]
    public async Task Presets_AreTheBuiltInsPlusYours_WithNoPathToGive()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Services.GetRequiredService<IHandBrakeProfileStore>().AddUserProfiles(new[] { UserProfile("Cartoons x265") });

        var names = (await host.Client.GetFromJsonAsync<string[]>("/api/presets"))!;
        var withStaleQuery = (await host.Client.GetFromJsonAsync<string[]>("/api/presets?path=C%3A%5Cold%5Cpresets.json"))!;

        Assert.Equal(new[] { "Cartoons x265", "Compressarr SD-HD", "Compressarr UHD AV1" }, names);
        Assert.Equal(names, withStaleQuery); // a cached 2.1 page still sending ?path= just gets the answer
    }

    [Fact]
    public async Task Profiles_ListsBuiltInsLocked_YoursUnlocked_AndWhichLanesUseEach()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Services.GetRequiredService<IHandBrakeProfileStore>().AddUserProfiles(new[] { UserProfile("Cartoons x265") });
        var lanes = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))!;
        var lane = lanes[0]!.AsObject();
        lane["moviePreset"] = "Compressarr SD-HD";
        lane["tvPreset"] = "Cartoons x265";
        (await host.Client.PutAsJsonAsync("/api/lanes/lane1", lane)).EnsureSuccessStatusCode();

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles"))!;
        var rows = dto["profiles"]!.AsArray().Select(r => r!.AsObject()).Where(r => r["engine"]!.GetValue<string>() == "handbrake").ToList();

        Assert.Equal(3, rows.Count);
        var builtIn = rows.Single(r => r["name"]!.GetValue<string>() == "Compressarr SD-HD");
        Assert.True(builtIn["builtIn"]!.GetValue<bool>());
        Assert.Equal("handbrake", builtIn["engine"]!.GetValue<string>());
        Assert.Equal("MKV", builtIn["container"]!.GetValue<string>());
        Assert.StartsWith("x265 10-bit", builtIn["video"]!.GetValue<string>());
        Assert.Equal(new[] { "Lane One" }, builtIn["usedBy"]!.AsArray().Select(n => n!.GetValue<string>()));

        var mine = rows.Single(r => r["name"]!.GetValue<string>() == "Cartoons x265");
        Assert.False(mine["builtIn"]!.GetValue<bool>());
        Assert.Equal("MP4", mine["container"]!.GetValue<string>());
        Assert.Equal(new[] { "Lane One" }, mine["usedBy"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Empty(rows.Single(r => r["name"]!.GetValue<string>() == "Compressarr UHD AV1")["usedBy"]!.AsArray());
        Assert.EndsWith("handbrake-profiles.json", dto["userFilePath"]!.GetValue<string>());
        Assert.Null(dto["userFileError"]);
    }

    [Theory]
    [InlineData("POST", "/api/presets/install")]
    [InlineData("POST", "/api/presets/reload")]
    [InlineData("GET", "/api/presets/status")]
    public async Task TheOldPresetsJsonPathEndpoints_AreGone(string method, string url)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LaneValidation_FlagsAnUnknownPreset_AgainstCompressarrsOwnProfiles()
    {
        await using var host = await QueueHost.StartAsync(Lane1); // its lane names presets that don't exist

        var lanes = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))!;
        var issues = lanes[0]!["validationIssues"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).ToList();

        Assert.Contains(issues, m => m.Contains("Compressarr's profiles"));
        Assert.DoesNotContain(issues, m => m.Contains("presets.json"));
    }

    [Fact]
    public async Task ExportCarriesYourProfiles_AndImportingItOnANewInstallBringsThemBack()
    {
        string export;
        await using (var first = await QueueHost.StartAsync(Lane1))
        {
            first.Services.GetRequiredService<IHandBrakeProfileStore>().AddUserProfiles(new[] { UserProfile("Cartoons x265") });
            export = await first.Client.GetStringAsync("/api/settings/export");
        }

        var exported = JsonNode.Parse(export)!.AsObject();
        Assert.Equal("Cartoons x265", exported["handBrakeUserProfiles"]![0]!["PresetName"]!.GetValue<string>());

        await using var second = await QueueHost.StartAsync(Lane1);
        Assert.DoesNotContain("Cartoons x265", await second.Client.GetFromJsonAsync<string[]>("/api/presets") ?? Array.Empty<string>());

        using var response = await second.Client.PostAsync("/api/settings/import", new StringContent(export, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Cartoons x265", (await second.Client.GetFromJsonAsync<string[]>("/api/presets"))!);
    }

    [Fact]
    public async Task ImportingAnOldExportWithoutProfiles_StillWorks()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var export = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings/export"))!.AsObject();
        export.Remove("handBrakeUserProfiles");

        using var response = await host.Client.PostAsync("/api/settings/import", new StringContent(export.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ImportingSettings_CopiesLanePresetsThatOnlyTheOldHandBrakeFileHas()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var oldFile = Path.Combine(host.Root, "old-presets.json");
        await File.WriteAllTextAsync(oldFile, new JsonObject
        {
            ["PresetList"] = new JsonArray(new JsonObject
            {
                ["PresetName"] = "Custom Presets",
                ["Folder"] = true,
                ["ChildrenArray"] = new JsonArray(UserProfile("From Old File"))
            })
        }.ToJsonString());

        var export = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings/export"))!.AsObject();
        export["HandBrake"]!["PresetsPath"] = oldFile;
        export["Lanes"]![0]!["TvPreset"] = "From Old File";
        Assert.DoesNotContain("From Old File", (await host.Client.GetFromJsonAsync<string[]>("/api/presets"))!);

        using var response = await host.Client.PostAsync("/api/settings/import", new StringContent(export.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("From Old File", (await host.Client.GetFromJsonAsync<string[]>("/api/presets"))!);
    }

    [Fact]
    public async Task Browse_WithFiles_ListsThePrograms_AndOpensAtAFilesFolder()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var folder = Path.Combine(host.Root, "tools");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var exe = Path.Combine(folder, "HandBrakeCLI.exe");
        await File.WriteAllTextAsync(exe, "x");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "x");

        var plain = JsonNode.Parse(await host.Client.GetStringAsync($"/api/browse?path={Uri.EscapeDataString(folder)}"))!;
        var withFiles = JsonNode.Parse(await host.Client.GetStringAsync($"/api/browse?path={Uri.EscapeDataString(exe)}&files=true"))!;

        Assert.Empty(plain["files"]!.AsArray());                   // the folder picker is unchanged
        Assert.Equal(folder, withFiles["currentPath"]!.GetValue<string>()); // a file path starts in its folder
        var names = withFiles["files"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("HandBrakeCLI.exe", names);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("notes.txt", names);
        Assert.Contains("sub", withFiles["directories"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Browse_WithAnExtension_ListsOnlyThoseFiles()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var folder = Path.Combine(host.Root, "downloads");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "my-presets.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "tool.exe"), "x");

        var result = JsonNode.Parse(await host.Client.GetStringAsync($"/api/browse?path={Uri.EscapeDataString(folder)}&files=true&ext=.json"))!;

        Assert.Equal(new[] { "my-presets.json" }, result["files"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ValidateLane_ReportsWhatIsWrongWithoutSaving_AndNothingOnceItIsFixed()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var lanes = JsonNode.Parse(await host.Client.GetStringAsync("/api/lanes"))!.AsArray();
        var lane = lanes[0]!.AsObject();
        lane["input"] = Path.Combine(host.Root, "does-not-exist");
        lane["output"] = host.Root;

        var bad = await PostValidate(host, lane);
        lane["input"] = host.Root;
        var good = await PostValidate(host, lane);

        Assert.Contains("input", bad);
        Assert.DoesNotContain("input", good);
        // nothing was saved
        Assert.NotEqual(host.Root, JsonNode.Parse(await host.Client.GetStringAsync("/api/lanes"))!.AsArray()[0]!["input"]!.GetValue<string>());
    }

    [Fact]
    public async Task ValidateLane_FlagsADestinationFolderThatIsNotThere_AndClearsItOnceItExists()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var lane = JsonNode.Parse(await host.Client.GetStringAsync("/api/lanes"))!.AsArray()[0]!.AsObject();
        lane["input"] = host.Root;
        lane["output"] = host.Root;
        lane["tvShowBasePath"] = Path.Combine(host.Root, "no-tv");
        lane["movieBasePath"] = Path.Combine(host.Root, "no-movies");

        var bad = await PostValidate(host, lane);
        lane["tvShowBasePath"] = host.Root;
        var oneFixed = await PostValidate(host, lane);
        lane["movieBasePath"] = "";
        var blank = await PostValidate(host, lane);

        Assert.Contains("tvShowBasePath", bad);
        Assert.Contains("movieBasePath", bad);
        Assert.DoesNotContain("tvShowBasePath", oneFixed);
        Assert.Contains("movieBasePath", oneFixed);
        Assert.DoesNotContain("movieBasePath", blank); // an empty destination is not flagged
    }

    private static async Task<List<string>> PostValidate(QueueHost host, JsonObject lane)
    {
        using var response = await host.Client.PostAsync("/api/lanes/validate", new StringContent(lane.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Select(i => i!["field"]!.GetValue<string>()).ToList();
    }

    [Fact]
    public async Task PurgeLogsAndReports_AlsoRemovesTheRunHistory_WhereverItIs_ButClearLogsDoesNot()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var logs = Path.Combine(Compressarr.Core.Config.AppPaths.GetAppDataDirectory(), "Logs");
        Directory.CreateDirectory(logs);
        var live = Compressarr.Core.Config.AppPaths.GetHistoryFilePath();
        var leftover = Path.Combine(logs, "Compressarr_History.csv");
        await File.WriteAllTextAsync(live, "x");
        await File.WriteAllTextAsync(leftover, "x");
        await File.WriteAllTextAsync(Path.Combine(logs, "run.log"), "x");

        (await host.Client.PostAsync("/api/maintenance/clear-logs", null)).EnsureSuccessStatusCode();
        Assert.True(File.Exists(live)); // clearing logs no longer reaches the history

        (await host.Client.PostAsync("/api/maintenance/purge-logs-reports", null)).EnsureSuccessStatusCode();
        Assert.False(File.Exists(live));
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task BackupDownload_ServesTheZip_AndRefusesAnythingElse()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        using var made = await host.Client.PostAsync("/api/backups/run", null);
        made.EnsureSuccessStatusCode();
        var name = JsonNode.Parse(await made.Content.ReadAsStringAsync())!["fileName"]!.GetValue<string>();

        using var ok = await host.Client.GetAsync($"/api/backups/download?fileName={Uri.EscapeDataString(name)}");
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        using var traversal = await host.Client.GetAsync($"/api/backups/download?fileName={Uri.EscapeDataString(@"..\" + name)}&folder={Uri.EscapeDataString(Path.GetTempPath())}");
        using var notABackup = await host.Client.GetAsync("/api/backups/download?fileName=anything.zip");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/zip", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal(name, ok.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal((byte)'P', bytes[0]); // a zip starts "PK"
        Assert.Equal((byte)'K', bytes[1]);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notABackup.StatusCode);
    }

    [Fact]
    public async Task LogsEndpoint_ServesALogByName_AndNothingElse()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var logs = Path.Combine(Compressarr.Core.Config.AppPaths.GetAppDataDirectory(), "Logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "Compressarr_run_HBdetails.txt"), "encoder said no");
        await File.WriteAllTextAsync(Path.Combine(logs, "Compressarr_History.csv"), "1,2,3");

        using var ok = await host.Client.GetAsync("/api/logs/Compressarr_run_HBdetails.txt");
        using var csv = await host.Client.GetAsync("/api/logs/Compressarr_History.csv");
        using var missing = await host.Client.GetAsync("/api/logs/nothing.log");
        using var traversal = await host.Client.GetAsync("/api/logs/" + Uri.EscapeDataString(@"..\compressarr.settings.json"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("encoder said no", await ok.Content.ReadAsStringAsync());
        Assert.StartsWith("text/plain", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, csv.StatusCode);       // only .log/.txt
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
    }

    [Fact]
    public async Task About_ServesTheLicenseAndNotices_WhenTheyAreNextToTheProgram_AndNotFoundWhenTheyAreNot()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        bool hadLicense = File.Exists(license), hadNotices = File.Exists(notices);
        try
        {
            if (hadLicense) File.Delete(license);
            if (hadNotices) File.Delete(notices);
            using var missing = await host.Client.GetAsync("/api/about/license");

            await File.WriteAllTextAsync(license, "GNU GENERAL PUBLIC LICENSE");
            await File.WriteAllTextAsync(notices, "Third-party notices");
            using var licenseResponse = await host.Client.GetAsync("/api/about/license");
            using var noticesResponse = await host.Client.GetAsync("/api/about/notices");

            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("GNU GENERAL PUBLIC LICENSE", await licenseResponse.Content.ReadAsStringAsync());
            Assert.Equal("Third-party notices", await noticesResponse.Content.ReadAsStringAsync());
            Assert.StartsWith("text/plain", licenseResponse.Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            if (!hadLicense) File.Delete(license);
            if (!hadNotices) File.Delete(notices);
        }
    }

    [Fact]
    public async Task FfmpegInstalledVersion_IsEmptyWhenThereIsNoFfmpeg_AndNeverFails()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/ffmpeg/installed-version"))!;

        Assert.Null(dto["version"]);
        Assert.Null(dto["installedBuild"]);
    }

    [Fact]
    public async Task RunNow_WithMonitoringOff_StartsOneBackgroundPass_AndDoesNotStartMonitoring()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsync("/api/run/run-now", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        await Task.Delay(500); // let the background pass finish before the test host is torn down
        var status = (await host.Client.GetFromJsonAsync<JsonObject>("/api/run/status"))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body["started"]!.GetValue<bool>());
        Assert.Equal("single-pass", body["mode"]!.GetValue<string>());
        Assert.False(status["isMonitoring"]!.GetValue<bool>()); // Run Now never turns monitoring on
    }

    [Theory]
    [InlineData("/api/encoder")]
    [InlineData("/api/profiles")]
    [InlineData("/api/presets")]
    public async Task NewGetEndpoints_ReturnValidJson(string url)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}: {body}");
        using var _ = JsonDocument.Parse(body);
    }
}
