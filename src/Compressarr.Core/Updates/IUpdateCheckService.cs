using System.Text.Json;
using Compressarr.Core.Config;

namespace Compressarr.Core.Updates;

public sealed record UpdateCheckResult(
    bool CheckedOk,
    string? Error,
    string LatestVersion,
    string ReleaseUrl,
    bool HasUpdate,
    DateTimeOffset CheckedAt,
    string Channel = "Stable");

/// <summary>Drives the GitHub releases check on a background loop for the app's whole lifetime,
/// same always-on posture as IBackupScheduler/IDigestScheduler. Runs its first check immediately
/// on Start() rather than waiting out the check interval - the app restarting is itself the signal
/// that's most likely to have just resolved a previously-flagged update (a fresh install replaces
/// the running process), so a stale "update available" shouldn't need to wait up to a full day to
/// clear after that. LastResult is served directly by the /api/about/check-update endpoint instead
/// of that endpoint hitting GitHub itself on every request.</summary>
public interface IUpdateCheckService
{
    UpdateCheckResult? LastResult { get; }
    bool IsRunning { get; }

    /// <summary>Idempotent - a second call while already running is a no-op.</summary>
    void Start();

    Task StopAsync();

    /// <summary>Runs one check immediately regardless of the loop's own schedule and updates
    /// LastResult - used as a same-request fallback by the endpoint when no background check has
    /// completed yet (Start() not called, or its first check is still in flight).</summary>
    Task<UpdateCheckResult> CheckNowAsync();
}

public sealed class UpdateCheckService : IUpdateCheckService, IDisposable
{
    // Same repo AboutEndpoints already pointed at - see its own comment for why this is a plain
    // constant rather than derived from anything.
    private const string RepoOwner = "Compressarr";
    private const string RepoName = "Compressarr";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkInterval;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public UpdateCheckResult? LastResult { get; private set; }

    public UpdateCheckService(IHttpClientFactory httpClientFactory, IConfigStore? configStore = null)
        : this(httpClientFactory, TimeProvider.System, TimeSpan.FromHours(24), configStore)
    {
    }

    // Internal ctor lets tests drive the loop with a tiny check interval and a fake clock instead
    // of waiting a real day between ticks - same pattern BackupScheduler/DigestScheduler use.
    internal UpdateCheckService(IHttpClientFactory httpClientFactory, TimeProvider timeProvider, TimeSpan checkInterval,
        IConfigStore? configStore = null, Func<AppVersion?>? installed = null)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _checkInterval = checkInterval;
        _configStore = configStore;
        _installed = installed ?? AppVersion.Installed;
    }

    private readonly IConfigStore? _configStore;
    private readonly Func<AppVersion?> _installed;

    /// <summary>The channel the user chose in Settings (stable releases only, or betas too). Stable when no config is available.</summary>
    public UpdateChannel CurrentChannel() => _configStore?.Load(AppPaths.GetConfigFilePath()).UpdateCheck.Channel ?? UpdateChannel.Stable;

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _loopTask = LoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;

        cts.Cancel();
        try { await (_loopTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await CheckNowAsync();

            try { await Task.Delay(_checkInterval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        var installed = _installed();
        var checkedAt = _timeProvider.GetUtcNow();
        var channel = CurrentChannel();
        var channelName = channel.ToString();

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr-UpdateChecker");

            // Stable: GitHub's own "latest release", which never is a pre-release. Beta: every release, and the highest version wins - so a
            // beta of the next version (2.2.0-beta.6) is newer than the current stable (2.1.10), and a final 2.2.0 beats its own betas.
            var url = channel == UpdateChannel.Beta
                ? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100"
                : $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var failed = new UpdateCheckResult(false, $"GitHub returned HTTP {(int)response.StatusCode}.", "", "", false, checkedAt, channelName);
                LastResult = failed;
                return failed;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };

            string tagName = "", releaseUrl = "";
            AppVersion? latest = null;
            foreach (var release in releases)
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

                var tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                var parsed = AppVersion.Parse(tag);
                if (parsed is null) continue; // not one of ours (a tag that is not a version)
                if (latest is null || parsed.CompareTo(latest) > 0)
                {
                    latest = parsed;
                    tagName = tag;
                    releaseUrl = release.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                }
            }

            // Compared as versions with their pre-release tag, not as plain numbers: 2.2.0-beta.5 is older than 2.2.0 and older than 2.2.0-beta.6.
            var hasUpdate = latest is not null && installed is not null && latest.CompareTo(installed) > 0;

            var result = new UpdateCheckResult(true, null, tagName, releaseUrl, hasUpdate, checkedAt, channelName);
            LastResult = result;
            return result;
        }
        catch (Exception ex)
        {
            var failed = new UpdateCheckResult(false, ex.Message, "", "", false, checkedAt, channelName);
            LastResult = failed;
            return failed;
        }
    }

    public void Dispose() => _cts?.Cancel();
}
