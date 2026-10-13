using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Compressarr.Core.Config;

namespace Compressarr.Core.Posters;

public sealed record PosterTestResult(bool Success, string Message);

public sealed record PosterCacheInfo(int Posters, long Bytes, string? Problem);

/// <summary>
/// Poster thumbnails for the Monitor page's queue, from TMDb (The Movie Database), using the user's own free API key. Off unless the user
/// turned "Show posters on queue" on and entered a key; when off, nothing here ever makes a request.
///
/// What leaves the machine: a title (and a year) to api.themoviedb.org, and a poster download from image.tmdb.org - made by Compressarr,
/// never by the browser. Each show or movie is looked up once and remembered on disk (a poster that was found is kept; "not found" is
/// remembered for two weeks), and each poster image is downloaded once per size and served from the app's own data folder afterwards.
/// A picture that has not been used for the log retention period (Settings > Logging & Reports, 30 days by default) is deleted by a
/// daily background cleanup; what was looked up is kept, so the picture is simply downloaded again if that title comes back.
///
/// Lookups never block a page: GetPosterId answers from memory and, when it does not know the title yet, queues a background lookup, so the
/// poster appears on a later refresh.
/// </summary>
public interface IPosterService
{
    /// <summary>The id of a ready poster for this file, or null (feature off, no match, or not looked up yet - in which case a lookup is queued).</summary>
    string? GetPosterId(PosterSettings settings, string fileName);

    /// <summary>The cached image file for a poster id at a TMDb size (w92, w154 or w185), downloading it first if needed. Null when unknown.</summary>
    Task<string?> GetImagePathAsync(PosterSettings settings, string id, string size, CancellationToken cancellationToken = default);

    /// <summary>Checks a key against TMDb without saving it (the Settings card's Test button).</summary>
    Task<PosterTestResult> TestKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    PosterCacheInfo GetCacheInfo();

    /// <summary>Deletes every cached poster and what was remembered about each title. Returns how many images were removed.</summary>
    int ClearCache();
}

public sealed partial class PosterService : IPosterService, IDisposable
{
    public static readonly string[] ImageSizes = { "w92", "w154", "w185" };

    private const string DefaultApiBase = "https://api.themoviedb.org/3";
    private const string DefaultImageBase = "https://image.tmdb.org/t/p";
    private const long MaxImageBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan NotFoundMemory = TimeSpan.FromDays(14);
    private static readonly TimeSpan TransientPause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeenGranularity = TimeSpan.FromDays(1);
    private static readonly TimeSpan IndexSaveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{5,64}\.(jpg|jpeg|png)$")]
    private static partial Regex SafePosterPath();

    private sealed class Entry
    {
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public bool IsTv { get; set; }
        public string? PosterPath { get; set; }
        public DateTimeOffset CheckedUtc { get; set; }

        /// <summary>The last day a queued file asked for this poster (null on entries from before this was recorded: CheckedUtc is used).</summary>
        public DateTimeOffset? LastSeenUtc { get; set; }
    }

    private sealed record Work(string Id, PosterQuery Query, string ApiKey);

    private sealed class KeyRejectedException : Exception { }
    private sealed class TransientException : Exception { }

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _gap;
    private readonly string _apiBase;
    private readonly string _imageBase;
    private readonly Func<int> _retentionDays;

    private readonly object _lock = new();
    private Dictionary<string, Entry>? _index;
    private readonly HashSet<string> _pending = new();
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private DateTimeOffset _pausedUntil;
    private string? _rejectedKey;
    private bool _indexDirty;
    private DateTimeOffset _lastIndexSave;
    private DateTimeOffset _lastCleanup;
    private bool _cleaning;

    public PosterService(IHttpClientFactory httpFactory, IConfigStore configStore)
        : this(httpFactory, TimeProvider.System, TimeSpan.FromMilliseconds(300), DefaultApiBase, DefaultImageBase,
            () => configStore.Load(AppPaths.GetConfigFilePath()).Logging.RetentionDays)
    {
    }

    // Test seam: a fake clock, no pause between lookups, made-up hosts and a fixed retention period (30 days unless a test says otherwise).
    internal PosterService(IHttpClientFactory httpFactory, TimeProvider time, TimeSpan gap, string apiBase, string imageBase, Func<int>? retentionDays = null)
    {
        _httpFactory = httpFactory;
        _time = time;
        _gap = gap;
        _apiBase = apiBase.TrimEnd('/');
        _imageBase = imageBase.TrimEnd('/');
        _retentionDays = retentionDays ?? (() => 30);
    }

    private static string IndexPath => Path.Combine(AppPaths.GetPostersDirectory(), "index.json");

    // ---- asking for a poster ------------------------------------------------------------------------------------------------------

    public string? GetPosterId(PosterSettings settings, string fileName)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.TmdbApiKey)) return null;

        var query = PosterNameParser.Parse(fileName);
        if (query is null) return null;

        MaybeCleanUp();

        var id = query.Id;
        lock (_lock)
        {
            if (LoadIndex().TryGetValue(id, out var entry))
            {
                var now = _time.GetUtcNow();
                if (entry.PosterPath is not null)
                {
                    // Note that this poster is still in use (at most once a day, and the index is written at most every few minutes), so
                    // the cleanup only ever removes pictures nobody has asked for in a long while.
                    if (now - (entry.LastSeenUtc ?? entry.CheckedUtc) >= SeenGranularity)
                    {
                        entry.LastSeenUtc = now;
                        _indexDirty = true;
                        if (now - _lastIndexSave >= IndexSaveInterval) SaveIndex();
                    }

                    return id;
                }

                if (now - entry.CheckedUtc < NotFoundMemory) return null;
            }
        }

        Enqueue(id, query, settings.TmdbApiKey.Trim());
        return null;
    }

    private void Enqueue(string id, PosterQuery query, string apiKey)
    {
        lock (_lock)
        {
            if (_time.GetUtcNow() < _pausedUntil) return;                     // TMDb was unreachable a moment ago: do not hammer it
            if (_rejectedKey is not null && _rejectedKey == apiKey) return;    // this key was refused: wait until the user changes it
            if (!_pending.Add(id)) return;
            _worker ??= Task.Run(WorkerAsync);
        }

        _work.Writer.TryWrite(new Work(id, query, apiKey));
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    await ResolveAsync(work, _stop.Token);
                }
                finally
                {
                    lock (_lock) _pending.Remove(work.Id);
                }

                if (_gap > TimeSpan.Zero) await Task.Delay(_gap, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ResolveAsync(Work work, CancellationToken cancellationToken)
    {
        try
        {
            var path = await SearchAsync(work.Query, work.ApiKey, cancellationToken);
            lock (_lock)
            {
                LoadIndex()[work.Id] = new Entry
                {
                    Title = work.Query.Title, Year = work.Query.Year, IsTv = work.Query.IsTv, PosterPath = path, CheckedUtc = _time.GetUtcNow(),
                    LastSeenUtc = _time.GetUtcNow()
                };
                SaveIndex();
            }
        }
        catch (KeyRejectedException)
        {
            lock (_lock) _rejectedKey = work.ApiKey;
        }
        catch (TransientException)
        {
            lock (_lock) _pausedUntil = _time.GetUtcNow() + TransientPause;
        }
    }

    /// <summary>One search. Returns the poster path of the first result that has one, or null when TMDb knows no such title. Throws
    /// KeyRejectedException (401) or TransientException (network, 429, 5xx) so those are never remembered as "not found".</summary>
    private async Task<string?> SearchAsync(PosterQuery query, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{_apiBase}/search/{(query.IsTv ? "tv" : "movie")}?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query.Title)}"
                  + (query.Year is { } year ? $"&{(query.IsTv ? "first_air_date_year" : "year")}={year}" : "");

        var (status, body) = await GetAsync(url, cancellationToken);
        if (status == 401) throw new KeyRejectedException();
        if (status != 200 || body is null) throw new TransientException();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String
                    && p.GetString() is { } posterPath && SafePosterPath().IsMatch(posterPath))
                {
                    return posterPath;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            throw new TransientException();
        }
    }

    private async Task<(int Status, string? Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr");
            using var response = await client.GetAsync(url, cancellationToken);
            var status = (int)response.StatusCode;
            return (status, status == 200 ? await response.Content.ReadAsStringAsync(cancellationToken) : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (0, null);
        }
    }

    // ---- the images ---------------------------------------------------------------------------------------------------------------

    public async Task<string?> GetImagePathAsync(PosterSettings settings, string id, string size, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || !ImageSizes.Contains(size) || string.IsNullOrWhiteSpace(id)) return null;

        string? posterPath;
        lock (_lock)
        {
            posterPath = LoadIndex().TryGetValue(id, out var entry) ? entry.PosterPath : null;
        }

        if (posterPath is null || !SafePosterPath().IsMatch(posterPath)) return null;

        var file = Path.Combine(AppPaths.GetPostersDirectory(), size, posterPath.TrimStart('/'));
        if (File.Exists(file)) return file;

        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr");
            using var response = await client.GetAsync($"{_imageBase}/{size}{posterPath}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // The poster was withdrawn: forget it, so the next sighting looks the title up again.
                lock (_lock)
                {
                    LoadIndex().Remove(id);
                    SaveIndex();
                }

                return null;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes) return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, buffer.ToArray(), cancellationToken);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- the Settings card --------------------------------------------------------------------------------------------------------

    public async Task<PosterTestResult> TestKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new PosterTestResult(false, "Enter a TMDb API key first.");

        var (status, _) = await GetAsync($"{_apiBase}/authentication?api_key={Uri.EscapeDataString(apiKey.Trim())}", cancellationToken);
        return status switch
        {
            200 => new PosterTestResult(true, "TMDb accepted the key."),
            401 => new PosterTestResult(false, "TMDb rejected this key. Check that you copied the API key (the shorter one), not the Read Access Token."),
            0 => new PosterTestResult(false, "Could not reach TMDb. Check the internet connection and try again."),
            _ => new PosterTestResult(false, $"TMDb answered with an unexpected status ({status}). Try again later.")
        };
    }

    public PosterCacheInfo GetCacheInfo()
    {
        int posters;
        string? problem = null;
        lock (_lock)
        {
            posters = LoadIndex().Values.Count(e => e.PosterPath is not null);
            if (_rejectedKey is not null) problem = "TMDb rejected the saved key, so no posters are being looked up. Fix the key and save.";
            else if (_time.GetUtcNow() < _pausedUntil) problem = "TMDb could not be reached a moment ago; Compressarr will try again shortly.";
        }

        long bytes = 0;
        var directory = AppPaths.GetPostersDirectory();
        if (Directory.Exists(directory))
        {
            foreach (var size in ImageSizes)
            {
                var sizeDirectory = Path.Combine(directory, size);
                if (Directory.Exists(sizeDirectory)) bytes += Directory.EnumerateFiles(sizeDirectory).Sum(f => new FileInfo(f).Length);
            }
        }

        return new PosterCacheInfo(posters, bytes, problem);
    }

    public int ClearCache()
    {
        var removed = 0;
        lock (_lock)
        {
            var directory = AppPaths.GetPostersDirectory();
            if (Directory.Exists(directory))
            {
                foreach (var size in ImageSizes)
                {
                    var sizeDirectory = Path.Combine(directory, size);
                    if (!Directory.Exists(sizeDirectory)) continue;
                    removed += Directory.EnumerateFiles(sizeDirectory).Count();
                    Directory.Delete(sizeDirectory, recursive: true);
                }

                if (File.Exists(IndexPath)) File.Delete(IndexPath);
            }

            _index = new Dictionary<string, Entry>();
            _rejectedKey = null;
            _pausedUntil = default;
        }

        return removed;
    }

    // ---- cleanup ------------------------------------------------------------------------------------------------------------------

    /// <summary>At most once a day, started from a poster request (so it only runs while the feature is on), on a background thread.</summary>
    private void MaybeCleanUp()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_cleaning || now - _lastCleanup < CleanupInterval) return;
            _cleaning = true;
            _lastCleanup = now;
        }

        _ = Task.Run(() =>
        {
            try { CleanUp(_retentionDays()); }
            catch (Exception) { /* a cache cleanup must never matter to anyone */ }
            finally { lock (_lock) _cleaning = false; }
        });
    }

    /// <summary>Deletes the poster pictures whose title has not been in the queue for retentionDays (the same period the logs and reports
    /// use), plus anything in the cache folder that no title points at. What was looked up is kept, so a title that comes back only needs its
    /// picture downloaded again; a "not found" answer older than the period is dropped (it would be asked again anyway). A period of 0 or less
    /// means keep everything, as it does for logs and reports. Pictures are deleted outright, not sent to the Recycle Bin: this is a cache.
    /// Returns how many picture files were removed.</summary>
    public int CleanUp(int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        var removed = 0;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var cutoff = now - TimeSpan.FromDays(retentionDays);
            var index = LoadIndex();
            var changed = false;

            foreach (var stale in index.Where(kv => kv.Value.PosterPath is null && kv.Value.CheckedUtc < cutoff).Select(kv => kv.Key).ToList())
            {
                index.Remove(stale);
                changed = true;
            }

            var keep = index.Values
                .Where(e => e.PosterPath is not null && (e.LastSeenUtc ?? e.CheckedUtc) >= cutoff)
                .Select(e => e.PosterPath!.TrimStart('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var size in ImageSizes)
            {
                var sizeDirectory = Path.Combine(AppPaths.GetPostersDirectory(), size);
                if (!Directory.Exists(sizeDirectory)) continue;

                foreach (var file in Directory.EnumerateFiles(sizeDirectory))
                {
                    if (keep.Contains(Path.GetFileName(file))) continue;
                    // A download in progress writes a .tmp file first; leave a fresh one alone.
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(1)) continue;

                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // in use right now: the next daily pass gets it
                    }
                }
            }

            if (changed) SaveIndex();
        }

        return removed;
    }

    // ---- what is remembered -------------------------------------------------------------------------------------------------------

    private Dictionary<string, Entry> LoadIndex()
    {
        if (_index is not null) return _index;

        try
        {
            _index = File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath)) ?? new()
                : new();
        }
        catch (Exception)
        {
            _index = new();   // a damaged index only costs the lookups again
        }

        return _index;
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.GetPostersDirectory());
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_index));
            File.Move(temp, IndexPath, overwrite: true);
            _indexDirty = false;
            _lastIndexSave = _time.GetUtcNow();
        }
        catch (Exception)
        {
            // Remembering is a courtesy: the poster still works for this session.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _work.Writer.TryComplete();
        _stop.Dispose();
    }
}
