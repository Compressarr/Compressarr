using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>"Lands in" lane assignment through the real endpoints: the Monitor page shows where a
/// file will land, the choice is saved without touching anything else about the file, and a deleted
/// destination lane is flagged rather than silently dropped. (The routing itself is covered by the
/// engine tests in Core.Tests.)</summary>
public class LaneAssignmentEndpointTests
{
    private static readonly LaneSpec Home = new("hdsd", "SD-HD");
    private static readonly LaneSpec Kids = new("kids", "Kids");

    [Fact]
    public async Task AssigningAFile_ShowsWhereItWillLandOnItsQueueRow()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "Sesame Street (1969).mkv");

        Assert.Equal(HttpStatusCode.OK, await host.SetDestinationAsync("hdsd", file, "kids"));

        var row = Assert.Single(await host.PollAsync());
        Assert.Equal("hdsd", row.LaneId);                 // it still lives in its own lane
        Assert.Equal("SD-HD", row.LaneDisplayName);
        Assert.Equal("kids", row.DestinationLaneId);
        Assert.Equal("Kids", row.DestinationLaneName);
        Assert.False(row.DestinationMissing);
    }

    [Fact]
    public async Task TheCurrentStatus_SaysWhereTheFileBeingEncodedWillLand()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var ordinary = host.Drop("hdsd", "a.mkv");
        var assigned = host.Drop("hdsd", "Sesame Street (1969).mkv");
        await host.SetDestinationAsync("hdsd", assigned, "kids");
        var state = host.Services.GetRequiredService<Compressarr.Web.CurrentRunStateService>();
        state.RunStarted("20260101_000000");
        state.LaneStarted("hdsd", "SD-HD", false);

        state.FileStarted("hdsd", 1, 2, "a.mkv", ordinary, "Some Preset", 1);
        var own = await StatusLandsInAsync(host);
        state.FileStarted("hdsd", 2, 2, "Sesame Street (1969).mkv", assigned, "Some Preset", 1);
        var redirected = await StatusLandsInAsync(host);

        Assert.Equal("SD-HD", own);        // no assignment: it lands in its own lane
        Assert.Equal("Kids", redirected);  // assigned to Kids
    }

    private static async Task<string?> StatusLandsInAsync(QueueHost host)
    {
        var status = await host.Client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/api/run/status");
        return status!["landsInLaneName"]?.GetValue<string>();
    }

    // ---- Duplicate lane -------------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, System.Text.Json.Nodes.JsonObject Body)> DuplicateAsync(QueueHost host, System.Text.Json.Nodes.JsonObject lane, string name)
    {
        var request = new System.Text.Json.Nodes.JsonObject { ["displayName"] = name, ["lane"] = lane.DeepClone() };
        using var response = await host.Client.PostAsync("/api/lanes/duplicate", new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        return (response.StatusCode, System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private static async Task<System.Text.Json.Nodes.JsonArray> GetLanesAsync(QueueHost host) =>
        System.Text.Json.Nodes.JsonNode.Parse(await host.Client.GetStringAsync("/api/lanes"))!.AsArray();

    [Fact]
    public async Task DuplicatingALane_CopiesEveryField_StartsDisabled_AndSitsAfterTheOriginal()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var home = (await GetLanesAsync(host))[0]!.AsObject();
        home["tvPreset"] = "Compressarr SD-HD";
        home["tvShowBasePath"] = @"D:\TV";
        home["movieBasePath"] = @"D:\Movies";
        home["engine"] = "HandBrake";

        var (status, copy) = await DuplicateAsync(host, home, "  SD-HD Anime  ");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("SD-HD Anime", copy["displayName"]!.GetValue<string>()); // trimmed
        Assert.NotEqual("hdsd", copy["id"]!.GetValue<string>());                // a lane of its own
        Assert.Equal("Compressarr SD-HD", copy["tvPreset"]!.GetValue<string>());
        Assert.Equal(@"D:\TV", copy["tvShowBasePath"]!.GetValue<string>());
        Assert.Equal(@"D:\Movies", copy["movieBasePath"]!.GetValue<string>());
        Assert.Equal(home["input"]!.GetValue<string>(), copy["input"]!.GetValue<string>());   // an exact copy, folders included
        Assert.Equal(home["output"]!.GetValue<string>(), copy["output"]!.GetValue<string>());
        Assert.False(copy["enabled"]!.GetValue<bool>());

        var lanes = await GetLanesAsync(host);
        Assert.Equal(new[] { "SD-HD", "SD-HD Anime", "Kids" }, lanes.Select(l => l!["displayName"]!.GetValue<string>()).ToArray());
    }

    // ---- Two enabled lanes must not watch the same Input folder ----------------------------------

    private static async Task<(HttpStatusCode Status, System.Text.Json.Nodes.JsonObject Body)> PutLaneAsync(QueueHost host, System.Text.Json.Nodes.JsonObject lane)
    {
        using var response = await host.Client.PutAsync($"/api/lanes/{lane["id"]!.GetValue<string>()}", new StringContent(lane.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        return (response.StatusCode, System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private static System.Text.Json.Nodes.JsonObject Lane(System.Text.Json.Nodes.JsonArray lanes, string id) =>
        lanes.Single(l => l!["id"]!.GetValue<string>() == id)!.AsObject();

    [Fact]
    public async Task EnablingACopyOnTheOriginalsFolder_IsRefused_UntilTheOriginalIsTurnedOff()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var home = Lane(await GetLanesAsync(host), "hdsd");
        var (_, copy) = await DuplicateAsync(host, home, "SD-HD Copy");
        copy["enabled"] = true;

        var (refused, refusal) = await PutLaneAsync(host, copy);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("SD-HD", refusal["message"]!.GetValue<string>());       // names the lane that already watches it
        Assert.Equal("input", refusal["field"]!.GetValue<string>());
        Assert.False(Lane(await GetLanesAsync(host), copy["id"]!.GetValue<string>())["enabled"]!.GetValue<bool>()); // nothing was saved

        home["enabled"] = false;
        Assert.Equal(HttpStatusCode.OK, (await PutLaneAsync(host, home)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutLaneAsync(host, copy)).Status);   // the swap now works
    }

    [Fact]
    public async Task PointingAnEnabledLaneAtAnotherLanesFolder_OrInsideIt_IsRefused()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var homeInput = Lane(await GetLanesAsync(host), "hdsd")["input"]!.GetValue<string>();
        var kids = Lane(await GetLanesAsync(host), "kids");

        kids["input"] = homeInput;
        var same = await PutLaneAsync(host, kids);
        kids["input"] = System.IO.Path.Combine(homeInput, "Sub", "Folder");
        var inside = await PutLaneAsync(host, kids);
        kids["input"] = System.IO.Path.GetDirectoryName(homeInput)!;
        var around = await PutLaneAsync(host, kids);

        Assert.Equal(HttpStatusCode.BadRequest, same.Status);
        Assert.Equal(HttpStatusCode.BadRequest, inside.Status);
        Assert.Equal(HttpStatusCode.BadRequest, around.Status);
        Assert.Contains("inside", inside.Body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFolderThatMerelyStartsWithAnotherLanesFolderName_IsNotAClash()
    {
        // D:\Media Download and D:\Media Download Kids are two folders, not one inside the other.
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var homeInput = Lane(await GetLanesAsync(host), "hdsd")["input"]!.GetValue<string>();
        var kids = Lane(await GetLanesAsync(host), "kids");
        kids["input"] = homeInput + " Kids";

        Assert.Equal(HttpStatusCode.OK, (await PutLaneAsync(host, kids)).Status);
    }

    [Fact]
    public async Task ADisabledLane_CanShareAFolder_AndSavesFreely()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var homeInput = Lane(await GetLanesAsync(host), "hdsd")["input"]!.GetValue<string>();
        var kids = Lane(await GetLanesAsync(host), "kids");
        kids["input"] = homeInput;
        kids["enabled"] = false;

        Assert.Equal(HttpStatusCode.OK, (await PutLaneAsync(host, kids)).Status);
    }

    [Fact]
    public async Task AnOverlapThatAlreadyExists_IsFlagged_ButUnrelatedEditsStillSave()
    {
        // An upgrade, an import or a hand-edited settings file can already hold two enabled lanes on one folder.
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var homeInput = Lane(await GetLanesAsync(host), "hdsd")["input"]!.GetValue<string>();
        host.Services.GetRequiredService<Compressarr.Core.Config.IConfigStore>().Update(Compressarr.Core.Config.AppPaths.GetConfigFilePath(), config =>
        {
            config.Lanes.Single(l => l.Id == "kids").Input = homeInput;
            return true;
        });

        var kids = Lane(await GetLanesAsync(host), "kids");
        kids["displayName"] = "Kids Renamed";
        var (status, _) = await PutLaneAsync(host, kids);

        Assert.Equal(HttpStatusCode.OK, status); // not locked out of an unrelated edit
        foreach (var id in new[] { "hdsd", "kids" })
        {
            var issues = Lane(await GetLanesAsync(host), id)["validationIssues"]!.AsArray().Select(i => i!["field"]!.GetValue<string>());
            Assert.Contains("input", issues);   // both lanes show the warning
        }
    }

    [Fact]
    public async Task DuplicatingALane_CopiesWhatTheCardShowsRightNow_NotWhatWasLastSaved()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var card = (await GetLanesAsync(host))[0]!.AsObject();
        card["moviePreset"] = "Edited But Not Saved"; // an unsaved edit on the card

        var (_, copy) = await DuplicateAsync(host, card, "Copy");

        Assert.Equal("Edited But Not Saved", copy["moviePreset"]!.GetValue<string>());
        Assert.NotEqual("Edited But Not Saved", (await GetLanesAsync(host))[0]!["moviePreset"]!.GetValue<string>()); // the original is untouched
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kids")]      // already used, whatever the capitalisation
    [InlineData("  SD-HD ")]
    public async Task DuplicatingALane_NeedsAnUnusedName(string name)
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var home = (await GetLanesAsync(host))[0]!.AsObject();

        var (status, body) = await DuplicateAsync(host, home, name);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        Assert.Equal(2, (await GetLanesAsync(host)).Count); // nothing was added
    }

    [Fact]
    public async Task AnOrdinaryRow_HasNoDestination()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        host.Drop("hdsd", "a.mkv");

        var row = Assert.Single(await host.PollAsync());

        Assert.Null(row.DestinationLaneId);
        Assert.Null(row.DestinationLaneName);
    }

    [Fact]
    public async Task ClearingTheAssignment_OrChoosingTheFilesOwnLane_RemovesTheMarker()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");

        await host.SetDestinationAsync("hdsd", file, "kids");
        Assert.Equal(HttpStatusCode.OK, await host.SetDestinationAsync("hdsd", file, null));
        Assert.Null((await host.PollAsync()).Single().DestinationLaneId);

        await host.SetDestinationAsync("hdsd", file, "kids");
        Assert.Equal(HttpStatusCode.OK, await host.SetDestinationAsync("hdsd", file, "hdsd"));
        Assert.Null((await host.PollAsync()).Single().DestinationLaneId);
    }

    [Fact]
    public async Task AssigningAFile_ChangesNothingElse_NotPositionPresetOrSkip()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var a = host.Drop("hdsd", "a.mkv");
        var b = host.Drop("hdsd", "b.mkv");
        var c = host.Drop("hdsd", "c.mkv");
        await host.OverridePresetAsync("hdsd", b, "My Preset");
        await host.SkipAsync("hdsd", b);
        await host.ReorderAsync(("hdsd", c), ("hdsd", a), ("hdsd", b)); // c, a, b
        var before = (await host.PollAsync()).Select(i => (i.FileName, i.Preset, i.IsSkipped, i.IsCustomPreset)).ToList();

        await host.SetDestinationAsync("hdsd", b, "kids");

        var after = await host.PollAsync();
        Assert.Equal(before, after.Select(i => (i.FileName, i.Preset, i.IsSkipped, i.IsCustomPreset)).ToList());
        Assert.Equal(new[] { "c.mkv", "a.mkv", "b.mkv" }, after.Select(i => i.FileName).ToArray());
        Assert.All(after, i => Assert.Equal("hdsd", i.LaneId));
    }

    [Fact]
    public async Task TheAssignmentIsStable_AcrossPolls_AndOtherEditsToTheSameFile()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");
        await host.SetDestinationAsync("hdsd", file, "kids");

        for (var i = 0; i < 3; i++) Assert.Equal("kids", (await host.PollAsync()).Single().DestinationLaneId);

        await host.OverridePresetAsync("hdsd", file, "X");
        await host.SkipAsync("hdsd", file);
        await host.SkipAsync("hdsd", file, skipped: false);
        await host.ReorderAsync(("hdsd", file));

        Assert.Equal("kids", (await host.PollAsync()).Single().DestinationLaneId);
        Assert.Equal("kids", host.ResumeEntries().Single().DestinationLaneId);
    }

    [Fact]
    public async Task AssigningAFileTheEngineHasNotTrackedYet_StillWorks_AndLocksItsPlaceInLine()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var first = host.Drop("hdsd", "z-first.mkv");
        await host.PollAsync(); // tracked, in arrival order
        var second = host.Drop("hdsd", "a-second.mkv"); // sorts before "z-first" on a scan

        await host.SetDestinationAsync("hdsd", second, "kids");

        Assert.Equal(new[] { "z-first.mkv", "a-second.mkv" }, await host.PollNamesAsync());
    }

    [Fact]
    public async Task AnAssignmentToAnUnknownLane_IsRejected_AndNothingIsSaved()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");

        Assert.Equal(HttpStatusCode.BadRequest, await host.SetDestinationAsync("hdsd", file, "no-such-lane"));

        Assert.Null((await host.PollAsync()).Single().DestinationLaneId);
    }

    [Fact]
    public async Task AssigningFromAnUnknownLane_OrForAFileThatIsGone_Returns404()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");

        Assert.Equal(HttpStatusCode.NotFound, await host.SetDestinationAsync("nope", file, "kids"));
        Assert.Equal(HttpStatusCode.NotFound, await host.SetDestinationAsync("hdsd", Path.Combine(host.Root, "missing.mkv"), "kids"));
    }

    [Fact]
    public async Task ADisabledDestinationLane_CanBeChosen()
    {
        await using var host = await QueueHost.StartAsync(Home, new LaneSpec("kids", "Kids", Enabled: false));
        var file = host.Drop("hdsd", "a.mkv");

        Assert.Equal(HttpStatusCode.OK, await host.SetDestinationAsync("hdsd", file, "kids"));
        Assert.Equal("Kids", (await host.PollAsync()).Single().DestinationLaneName);
    }

    [Fact]
    public async Task IfTheDestinationLaneIsDeleted_TheRowIsFlagged_ItStaysAssigned_AndNothingFallsBackToTheHomeLane()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");
        await host.SetDestinationAsync("hdsd", file, "kids");

        host.DeleteLane("kids");

        var row = Assert.Single(await host.PollAsync());
        Assert.True(row.DestinationMissing);
        Assert.Null(row.DestinationLaneName);
        Assert.Equal("kids", row.DestinationLaneId); // the assignment isn't silently cleared
        Assert.Equal("kids", host.ResumeEntries().Single().DestinationLaneId);
    }
    [Fact]
    public async Task DeletingALane_CanFirstAskHowManyQueuedFilesAreSetToLandInIt()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var a = host.Drop("hdsd", "a.mkv");
        var b = host.Drop("hdsd", "b.mkv");
        var c = host.Drop("hdsd", "c.mkv");
        await host.SetDestinationAsync("hdsd", a, "kids");
        await host.SetDestinationAsync("hdsd", b, "kids");
        await host.RemoveAsync("hdsd", b); // removed from the queue: no longer waiting to land anywhere

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/lanes/kids/redirected-files"));
        Assert.Equal(1, doc.RootElement.GetProperty("waitingFiles").GetInt32());

        using var none = JsonDocument.Parse(await host.Client.GetStringAsync("/api/lanes/hdsd/redirected-files"));
        Assert.Equal(0, none.RootElement.GetProperty("waitingFiles").GetInt32()); // a lane's own files aren't "redirected to" it
    }

    [Fact]
    public async Task AFinishedFileHeldOnAFailedMove_StillCountsAsWaitingForItsDestinationLane()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var file = host.Drop("hdsd", "a.mkv");
        host.SeedResume(new ResumeEntry { LaneId = "hdsd", FullName = file, Status = ResumeStatus.MoveFailed, DestinationLaneId = "kids", EncodedFilePath = file });

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/lanes/kids/redirected-files"));

        Assert.Equal(1, doc.RootElement.GetProperty("waitingFiles").GetInt32());
    }

    [Fact]
    public async Task TheHistoryReportsList_CarriesEachRunsRedirectCount_ForTheHighlight()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var config = host.Services.GetRequiredService<IConfigStore>().Load(AppPaths.GetConfigFilePath());
        var expander = host.Services.GetRequiredService<IPathExpander>();
        var today = DateTime.Now;
        // Runs the way the app records them since 3.0: the history row, then the report's rows (here an empty report: only the list is looked at).
        var archive = host.Services.GetRequiredService<IRunArchive>();
        var emptyReport = new Compressarr.Core.Reporting.ReportModel { GeneratedAt = today, RunTime = TimeSpan.FromMinutes(1), Lanes = Array.Empty<Compressarr.Core.Reporting.LaneReportSection>() };
        archive.SaveReport(archive.RecordRun(new RunHistoryRecord(today.Year, today.Month, today.Day, 10, 4, 2, 0, 1, 0, RunNumber: 1)), emptyReport);
        archive.SaveReport(archive.RecordRun(new RunHistoryRecord(today.Year, today.Month, today.Day, 10, 4, 2, 0, 1, 0, RunNumber: 2, RedirectCount: 3)), emptyReport);

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/history/reports"));

        var byRun = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("runNumber").GetInt32(), e => e.GetProperty("redirectCount").GetInt32());
        Assert.Equal(3, byRun[2]);
        Assert.Equal(0, byRun[1]);
    }
}
