using System.Net;
using Compressarr.Core.Config;
using Compressarr.Core.Updates;

namespace Compressarr.Core.Announcements;

public sealed record AnnouncementItem(string Id, string Date, string Kind, string Title, string Text, string? Link, bool Important, bool Read);

/// <summary>What the bell and the list show. When announcements are off, Items is empty and nothing was ever fetched.</summary>
public sealed record AnnouncementView(bool Enabled, IReadOnlyList<AnnouncementItem> Items, int Unread, DateTimeOffset? LastCheckedUtc, bool NeedsNewerApp);

/// <summary>
/// Compressarr's announcements: short news items posted to the Compressarr-Announcements repo on GitHub. A background loop, started
/// with the app next to the update check, fetches the file once at start-up and then about once a day - and only while the setting is on;
/// when it is off no request is made at all. The request is a plain GET that sends nothing about the user (not even the version: the
/// filtering by version happens here). Everything fails quietly: offline, a bad file or a missing repo simply leaves the bell as it was.
/// </summary>
public interface IAnnouncementService
{
    void Start();

    Task StopAsync();

    /// <summary>One check now. Does nothing (and returns false) when announcements are off. True when GitHub answered with a usable file
    /// or said it had not changed.</summary>
    Task<bool> CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads only what is stored locally - never touches the network - so the bell can ask on every page load.</summary>
    AnnouncementView GetView();

    /// <summary>Marks the given ids read; null marks everything currently on the list.</summary>
    void MarkRead(IReadOnlyCollection<string>? ids);
}

public sealed class AnnouncementService : IAnnouncementService, IDisposable
{
    internal const string DefaultUrl = "https://raw.githubusercontent.com/MrWizardCT/Compressarr-Announcements/main/announcements.json";
    private const int MaxReadMarks = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigStore _configStore;
    private readonly IAnnouncementStateStore _state;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retry;
    private readonly TimeSpan _tick;
    private readonly string _url;
    private readonly Func<AppVersion?> _appVersion;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _nextDue = DateTimeOffset.MinValue;

    public AnnouncementService(IHttpClientFactory httpClientFactory, IConfigStore configStore, IAnnouncementStateStore state)
        : this(httpClientFactory, configStore, state, TimeProvider.System, TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), DefaultUrl, AppVersion.Installed)
    {
    }

    // Test seam: a tiny interval and a fake clock and URL instead of waiting a real day.
    internal AnnouncementService(IHttpClientFactory httpClientFactory, IConfigStore configStore, IAnnouncementStateStore state, TimeProvider time,
        TimeSpan interval, TimeSpan retry, TimeSpan tick, string url, Func<AppVersion?> appVersion)
    {
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _state = state;
        _time = time;
        _interval = interval;
        _retry = retry;
        _tick = tick;
        _url = url;
        _appVersion = appVersion;
    }

    private bool Enabled() => _configStore.Load(AppPaths.GetConfigFilePath()).Announcements.Enabled;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loop = LoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try { await (_loop ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Checks the setting every tick, so turning announcements on later starts them within minutes, and off stops them at once.
                if (_time.GetUtcNow() >= _nextDue && Enabled())
                {
                    var ok = await CheckNowAsync(token);
                    _nextDue = _time.GetUtcNow() + (ok ? _interval : _retry);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* never let a bad check end the loop */ }

            try { await Task.Delay(_tick, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<bool> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled()) return false;

        try
        {
            var before = _state.Load();
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr-Announcements"); // no version number: nothing about the install is sent

            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            if (!string.IsNullOrEmpty(before.ETag) && !string.IsNullOrEmpty(before.CachedJson))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", before.ETag);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var now = _time.GetUtcNow();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _state.Update(s => { s.LastCheckedUtc = now; return 0; });
                return true;
            }

            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength > AnnouncementReader.MaxFileBytes) return false;

            var text = await ReadBoundedAsync(response, cancellationToken);
            if (text is null) return false;

            var parsed = AnnouncementReader.Parse(text);
            var etag = response.Headers.ETag?.ToString();
            var today = DateOnly.FromDateTime(now.UtcDateTime);

            switch (parsed.Status)
            {
                case AnnouncementFileStatus.Ok:
                    _state.Update(s =>
                    {
                        s.CachedJson = text;
                        s.ETag = etag;
                        s.LastCheckedUtc = now;
                        s.NeedsNewerApp = false;
                        s.Baseline ??= today.ToString("yyyy-MM-dd");
                        return 0;
                    });
                    return true;

                case AnnouncementFileStatus.UnknownSchema:
                    _state.Update(s => { s.NeedsNewerApp = true; s.LastCheckedUtc = now; return 0; });
                    return true;

                default:
                    return false; // keeps whatever was cached
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > AnnouncementReader.MaxFileBytes) return null;
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public AnnouncementView GetView()
    {
        var enabled = Enabled();
        var state = _state.Load();
        if (!enabled) return new AnnouncementView(false, Array.Empty<AnnouncementItem>(), 0, state.LastCheckedUtc, false);

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var current = _appVersion();
        DateOnly? baseline = DateOnly.TryParseExact(state.Baseline, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var b) ? b : null;

        var items = AnnouncementReader.Parse(state.CachedJson).Items
            .Where(a => AnnouncementRules.InVersionRange(a, current) && AnnouncementRules.InWindow(a, today))
            .OrderByDescending(a => a.Important)
            .ThenByDescending(a => a.Date)
            .Select(a => new AnnouncementItem(
                a.Id, a.Date.ToString("yyyy-MM-dd"), a.Kind, a.Title, a.Text, a.Link, a.Important,
                // News from before this machine first looked is shown, but never as unread.
                Read: state.Read.Contains(a.Id) || baseline is null || a.Date < baseline))
            .ToList();

        return new AnnouncementView(true, items, items.Count(i => !i.Read), state.LastCheckedUtc, state.NeedsNewerApp);
    }

    public void MarkRead(IReadOnlyCollection<string>? ids)
    {
        var toMark = ids ?? GetView().Items.Select(i => i.Id).ToList();
        if (toMark.Count == 0) return;

        _state.Update(s =>
        {
            foreach (var id in toMark.Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= AnnouncementReader.MaxIdLength))
            {
                if (!s.Read.Contains(id)) s.Read.Add(id);
            }

            if (s.Read.Count > MaxReadMarks) s.Read.RemoveRange(0, s.Read.Count - MaxReadMarks);
            return 0;
        });
    }

    public void Dispose() => _cts?.Cancel();
}
