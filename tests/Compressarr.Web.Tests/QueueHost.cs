using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Compressarr.Core;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.Reporting;
using Compressarr.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Compressarr.Web.Tests;

/// <summary>One row of the Monitor page's In Queue list, as returned by GET /api/run/status.</summary>
public sealed record QueueItem(
    string LaneId,
    string LaneDisplayName,
    string FileName,
    string FullName,
    string? Preset,
    bool IsResumed,
    bool IsError,
    bool IsSkipped,
    bool IsCustomPreset,
    string? DestinationLaneId = null,
    string? DestinationLaneName = null,
    bool DestinationMissing = false);

public sealed record StatusDto(List<QueueItem> UpNext);

public sealed record LaneSpec(string Id, string Name, bool Enabled = true);

/// <summary>
/// Runs the REAL Compressarr web endpoints (the same AddCompressarrCore/AddCompressarrWeb/
/// MapCompressarrEndpoints wiring Compressarr.Desktop uses) in an in-memory test server, against
/// real services pointed at a throwaway temp folder - so a test exercises exactly what the Monitor
/// page does: drop files into a lane's Input folder, poll /api/run/status, send queue-control
/// requests. Nothing is mocked except the CPU sampler.
///
/// SAFETY: AppPaths.TestOverrideAppDataDirectory is set for the lifetime of the host so settings,
/// resume.json and logs can never touch this machine's real Compressarr data. That override is a
/// process-wide static, so this assembly disables test parallelization (see AssemblyInfo.cs).
///
/// The monitoring loop is never started, so nothing processes files - these tests only observe
/// and manipulate the queue.
/// </summary>
public sealed class QueueHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    private readonly CompressarrConfig _config;
    private readonly Dictionary<string, LaneConfig> _lanes = new();

    public string Root { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    private QueueHost(string root, WebApplication app, CompressarrConfig config)
    {
        Root = root;
        _app = app;
        _config = config;
        Client = app.GetTestClient();
    }

    public static async Task<QueueHost> StartAsync(params LaneSpec[] lanes)
    {
        var root = Path.Combine(Path.GetTempPath(), "compressarr-webtests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        AppPaths.TestOverrideAppDataDirectory = Path.Combine(root, "appdata");
        Directory.CreateDirectory(AppPaths.GetAppDataDirectory());

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddCompressarrCore();
        builder.Services.AddCompressarrWeb();
        builder.Services.RemoveAll<ICpuUsageSampler>();
        builder.Services.AddSingleton<ICpuUsageSampler, NoCpuSampler>();
        // A pass that finds a problem opens its report in the machine's browser (Report > Open after run defaults to "on error"). A test
        // that runs a real pass must never do that to whoever is running the tests.
        builder.Services.RemoveAll<IReportLauncher>();
        builder.Services.AddSingleton<IReportLauncher, NoReportLauncher>();

        var app = builder.Build();
        app.MapCompressarrEndpoints();
        await app.StartAsync();

        var config = new CompressarrConfig();
        var host = new QueueHost(root, app, config);
        foreach (var spec in lanes)
        {
            var lane = new LaneConfig
            {
                Id = spec.Id,
                DisplayName = spec.Name,
                Enabled = spec.Enabled,
                Input = Path.Combine(root, spec.Id, "Input"),
                Output = Path.Combine(root, spec.Id, "Output"),
                TvPreset = "Lane TV Preset",
                MoviePreset = "Lane Movie Preset",
                TvShowBasePath = Path.Combine(root, spec.Id, "TV"),
                MovieBasePath = Path.Combine(root, spec.Id, "Movies")
            };
            Directory.CreateDirectory(lane.Input);
            config.Lanes.Add(lane);
            host._lanes[spec.Id] = lane;
        }
        app.Services.GetRequiredService<IConfigStore>().Save(config, AppPaths.GetConfigFilePath());
        return host;
    }

    /// <summary>Creates a (non-empty - empty files are ignored by the scanner's size filter) video
    /// file under the lane's Input folder and returns its full path.</summary>
    public string Drop(string laneId, string relativePath)
    {
        var path = Path.Combine(_lanes[laneId].Input, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>One Monitor-page poll: GET /api/run/status, returning the In Queue list in the order
    /// it's displayed.</summary>
    public async Task<List<QueueItem>> PollAsync()
    {
        var dto = await Client.GetFromJsonAsync<StatusDto>("/api/run/status", Json);
        return dto!.UpNext;
    }

    public async Task<List<string>> PollNamesAsync() =>
        (await PollAsync()).Select(i => i.FileName).ToList();

    public Task<HttpStatusCode> PostAsync(string url, object body) => SendAsync(url, body);

    private async Task<HttpStatusCode> SendAsync(string url, object body)
    {
        using var response = await Client.PostAsJsonAsync(url, body, Json);
        return response.StatusCode;
    }

    public Task<HttpStatusCode> SkipAsync(string laneId, string fullName, bool skipped = true) =>
        PostAsync("/api/run/queue/skip", new { laneId, fullName, skipped });

    public Task<HttpStatusCode> RemoveAsync(string laneId, string fullName) =>
        PostAsync("/api/run/queue/remove", new { laneId, fullName });

    public Task<HttpStatusCode> OverridePresetAsync(string laneId, string fullName, string? preset) =>
        PostAsync("/api/run/queue/preset-override", new { laneId, fullName, preset });

    /// <summary>The "lands in" picker on a queue row. A null destination clears it.</summary>
    public Task<HttpStatusCode> SetDestinationAsync(string laneId, string fullName, string? destinationLaneId) =>
        PostAsync("/api/run/queue/destination", new { laneId, fullName, destinationLaneId });

    /// <summary>Deletes a lane from the saved config (as the Lanes page does), leaving everything else.</summary>
    public void DeleteLane(string laneId) =>
        Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), config => config.Lanes.RemoveAll(l => l.Id == laneId));

    /// <summary>Submits the whole queue in the given order, exactly like a drag-reorder or Move to
    /// top/bottom does (items are lane id + full path).</summary>
    public Task<HttpStatusCode> ReorderAsync(params (string LaneId, string FullName)[] items) =>
        PostAsync("/api/run/queue/reorder", new { items = items.Select(i => new { laneId = i.LaneId, fullName = i.FullName }).ToArray() });

    public List<ResumeEntry> ResumeEntries() =>
        Services.GetRequiredService<IResumeStateStore>().Load(AppPaths.GetResumeFilePath());

    public void SeedResume(params ResumeEntry[] entries) =>
        Services.GetRequiredService<IResumeStateStore>().Save(entries.ToList(), AppPaths.GetResumeFilePath());

    public CurrentRunStateService RunState => Services.GetRequiredService<CurrentRunStateService>();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        AppPaths.TestOverrideAppDataDirectory = null;
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private sealed class NoReportLauncher : IReportLauncher
    {
        public void Open(string reportFilePath) { }
    }

    private sealed class NoCpuSampler : ICpuUsageSampler
    {
        public Task<double?> SampleAsync() => Task.FromResult<double?>(null);
    }
}
