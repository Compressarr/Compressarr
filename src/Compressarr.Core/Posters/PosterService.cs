using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Compressarr.Core.Config;

namespace Compressarr.Core.Posters;

public sealed record PosterTestResult(bool Success, string Message);

public sealed record PosterCacheInfo(int Posters, long Bytes, string? Problem);

/// <summary>
/// Poster thumbnails for the Monitor page's queue, from TMDb (The Movie Database) or OMDb, using the user's own free API key. Off unless the
/// user turned "Show posters on queue" on and entered a key; when off, nothing here ever makes a request.
///
/// What leaves the machine: a title (and a year) to api.themoviedb.org or www.omdbapi.com - the source the user chose first, and the other
/// one only when the user ticked "try the other source" and the first found nothing - and a poster download from image.tmdb.org or
/// m.media-amazon.com (where OMDb's pictures live) - made by Compressarr, never by the browser. Each show or movie is looked up once and
/// remembered on disk (a poster that was found is kept; "not found" is remembered for two weeks), and each poster image is downloaded once
/// (once per size for TMDb; OMDb has one size) and served from the app's own data folder afterwards.
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

    /// <summary>The cached image file for a poster id at a TMDb size (w92, w154 or w185; the size is ignored for an OMDb picture, which has one),
    /// downloading it first if needed. Null when unknown.</summary>
    Task<string?> GetImagePathAsync(PosterSettings settings, string id, string size, CancellationToken cancellationToken = default);

    /// <summary>Checks a key against TMDb or OMDb without saving it (the Settings card's Test button).</summary>
    Task<PosterTestResult> TestKeyAsync(PosterSource source, string apiKey, CancellationToken cancellationToken = default);

    PosterCacheInfo GetCacheInfo();

    /// <summary>Deletes every cached poster and what was remembered about each title. Returns how many images were removed.</summary>
    int ClearCache();
}

public sealed partial class PosterService : IPosterService, IDisposable
{
    public static readonly string[] ImageSizes = { "w92", "w154", "w185" };

    private const string DefaultApiBase = "https://api.themoviedb.org/3";
    private const string DefaultImageBase = "https://image.tmdb.org/t/p";
    private const string DefaultOmdbBase = "https://www.omdbapi.com";
    private const string OmdbFolder = "omdb";
    private const long MaxImageBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan NotFoundMemory = TimeSpan.FromDays(14);
    private static readonly TimeSpan TransientPause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LimitPause = TimeSpan.FromHours(1);   // OMDb's free key allows about 1,000 lookups a day
    private static readonly TimeSpan SeenGranularity = TimeSpan.FromDays(1);
    private static readonly TimeSpan IndexSaveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{5,64}\.(jpg|jpeg|png)$")]
    private static partial Regex SafePosterPath();

    [GeneratedRegex(@"^tt\d{5,12}$")]
    private static partial Regex SafeImdbId();

    /// <summary>Every folder under the posters directory that holds pictures: one per TMDb size, and one for OMDb.</summary>
    private static readonly string[] CacheFolders = ImageSizes.Append(OmdbFolder).ToArray();

    private const string TmdbName = "tmdb";
    private const string OmdbName = "omdb";

    private static string NameOf(PosterSource source) => source == PosterSource.Omdb ? OmdbName : TmdbName;

    private sealed class Entry
    {
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public bool IsTv { get; set; }
        public string? PosterPath { get; set; }

        /// <summary>Which source the poster came from: "tmdb" or "omdb". Null on entries from before OMDb existed, which are TMDb's.</summary>
        public string? Source { get; set; }

        /// <summary>OMDb only: the IMDb id the picture is saved under, and where it was downloaded from (in case it has to be fetched again).</summary>
        public string? ImdbId { get; set; }
        public string? PosterUrl { get; set; }

        /// <summary>For a "not found": the sources that were asked ("tmdb", "omdb" or both, comma separated). Null on older entries: TMDb only.</summary>
        public string? Tried { get; set; }

        public DateTimeOffset CheckedUtc { get; set; }

        [JsonIgnore] public bool HasPoster => PosterPath is not null || ImdbId is not null;
        [JsonIgnore] public string FoundBy => Source ?? TmdbName;

        /// <summary>The last day a queued file asked for this poster (null on entries from before this was recorded: CheckedUtc is used).</summary>
        public DateTimeOffset? LastSeenUtc { get; set; }
    }

    /// <summary>One source to ask, and the key to ask it with.</summary>
    private readonly record struct Step(PosterSource Source, string Key);

    /// <summary>What a search found: a TMDb poster path, or an OMDb picture (already downloaded, so its shape could be checked).</summary>
    private sealed record Found(string Source, string? PosterPath, string? ImdbId, string? PosterUrl);

    /// <summary>Plan: the sources still to ask. PriorTried: sources that already said "not found" for this title recently (kept in the new answer).</summary>
    private sealed record Work(string Id, PosterQuery Query, IReadOnlyList<Step> Plan, IReadOnlyList<string> PriorTried);

    private sealed class KeyRejectedException : Exception { }

    private sealed class TransientException : Exception
    {
        /// <summary>True when the source said the key's daily limit was reached (a longer wait than a network hiccup).</summary>
        public bool Limit { get; init; }
    }

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _gap;
    private readonly string _apiBase;
    private readonly string _imageBase;
    private readonly string _omdbBase;
    private readonly Func<int> _retentionDays;

    private readonly object _lock = new();
    private Dictionary<string, Entry>? _index;
    private readonly HashSet<string> _pending = new();
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private readonly Dictionary<PosterSource, (DateTimeOffset Until, bool Limit)> _paused = new();   // guarded by _lock
    private readonly Dictionary<PosterSource, string> _rejected = new();                                // the key each source refused; guarded by _lock
    private bool _indexDirty;
    private DateTimeOffset _lastIndexSave;
    private DateTimeOffset _lastCleanup;
    private bool _cleaning;
    private bool _flushing;
    private readonly Dictionary<string, Task<string?>> _downloads = new(StringComparer.OrdinalIgnoreCase);   // guarded by _lock

    // What each file name was worked out to be: the Monitor page asks about every queue row on every poll, and a name never changes meaning.
    private readonly ConcurrentDictionary<string, PosterQuery?> _parsed = new(StringComparer.Ordinal);
    private const int MaxParsedNames = 20000;

    public PosterService(IHttpClientFactory httpFactory, IConfigStore configStore)
        : this(httpFactory, TimeProvider.System, TimeSpan.FromMilliseconds(300), DefaultApiBase, DefaultImageBase,
            () => configStore.Load(AppPaths.GetConfigFilePath()).Logging.RetentionDays)
    {
    }

    // Test seam: a fake clock, no pause between lookups, made-up hosts and a fixed retention period (30 days unless a test says otherwise).
    internal PosterService(IHttpClientFactory httpFactory, TimeProvider time, TimeSpan gap, string apiBase, string imageBase, Func<int>? retentionDays = null, string? omdbBase = null)
    {
        _httpFactory = httpFactory;
        _time = time;
        _gap = gap;
        _apiBase = apiBase.TrimEnd('/');
        _imageBase = imageBase.TrimEnd('/');
        _omdbBase = (omdbBase ?? DefaultOmdbBase).TrimEnd('/');
        _retentionDays = retentionDays ?? (() => 30);
    }

    private static string IndexPath => Path.Combine(AppPaths.GetPostersDirectory(), "index.json");

    // ---- asking for a poster ------------------------------------------------------------------------------------------------------

    public string? GetPosterId(PosterSettings settings, string fileName)
    {
        if (!settings.Enabled) return null;
        var plan = PlanFor(settings);
        if (plan.Count == 0) return null;

        if (_parsed.Count > MaxParsedNames) _parsed.Clear();
        var query = _parsed.GetOrAdd(fileName, PosterNameParser.Parse);
        if (query is null) return null;

        MaybeCleanUp();

        var id = query.Id;
        IReadOnlyList<string> prior = Array.Empty<string>();
        lock (_lock)
        {
            // Marks made by earlier requests are written out once a few minutes have passed since the last write - on a background
            // thread, so a status poll never waits on the disk.
            if (_indexDirty && !_flushing && _time.GetUtcNow() - _lastIndexSave >= IndexSaveInterval)
            {
                _flushing = true;
                _ = Task.Run(() =>
                {
                    lock (_lock)
                    {
                        if (_indexDirty) SaveIndex();
                        _flushing = false;
                    }
                });
            }

            if (LoadIndex().TryGetValue(id, out var entry))
            {
                var now = _time.GetUtcNow();
                if (entry.HasPoster)
                {
                    // A picture from a source that is no longer in use (the user switched sources) is not shown: the title is looked up again.
                    if (plan.Any(step => NameOf(step.Source) == entry.FoundBy))
                    {
                        // Note that this poster is still in use (at most once a day, and the index is written at most every few minutes), so
                        // the cleanup only ever removes pictures nobody has asked for in a long while.
                        if (now - (entry.LastSeenUtc ?? entry.CheckedUtc) >= SeenGranularity)
                        {
                            entry.LastSeenUtc = now;
                            _indexDirty = true;
                        }

                        return id;
                    }
                }
                else if (now - entry.CheckedUtc < NotFoundMemory)
                {
                    // Sources that already said "not found" are not asked again; one that is new to the plan (the user switched, or turned the
                    // other source on as a backup) still is.
                    prior = (entry.Tried ?? TmdbName).Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (plan.All(step => prior.Contains(NameOf(step.Source)))) return null;
                }
            }
        }

        Enqueue(id, query, plan, prior);
        return null;
    }

    /// <summary>The sources to ask, in order: the chosen one, then the other one when "try the other source" is ticked and its key is entered.
    /// Empty when the chosen source has no key.</summary>
    private static List<Step> PlanFor(PosterSettings settings)
    {
        var plan = new List<Step>();
        var chosen = settings.Source;
        var chosenKey = KeyFor(settings, chosen);
        if (chosenKey.Length == 0) return plan;

        plan.Add(new Step(chosen, chosenKey));
        var other = chosen == PosterSource.Omdb ? PosterSource.Tmdb : PosterSource.Omdb;
        var otherKey = KeyFor(settings, other);
        if (settings.FallbackToOther && otherKey.Length > 0) plan.Add(new Step(other, otherKey));
        return plan;
    }

    private static string KeyFor(PosterSettings settings, PosterSource source) => ((source == PosterSource.Omdb ? settings.OmdbApiKey : settings.TmdbApiKey) ?? "").Trim();

    /// <summary>Must be called under _lock. False while this source refused its key (until the key changes) or was unreachable a moment ago.</summary>
    private bool IsUsable(Step step)
    {
        if (_rejected.TryGetValue(step.Source, out var rejectedKey) && rejectedKey == step.Key) return false;
        if (_paused.TryGetValue(step.Source, out var pause) && _time.GetUtcNow() < pause.Until) return false;
        return true;
    }

    private void Enqueue(string id, PosterQuery query, List<Step> plan, IReadOnlyList<string> prior)
    {
        var toAsk = plan.Where(step => !prior.Contains(NameOf(step.Source))).ToList();
        lock (_lock)
        {
            if (!toAsk.Any(IsUsable)) return;     // nothing to ask right now: do not hammer a source that is unreachable or refused the key
            if (!_pending.Add(id)) return;
            _worker ??= Task.Run(WorkerAsync);
        }

        _work.Writer.TryWrite(new Work(id, query, toAsk, prior));
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
        var tried = new List<string>(work.PriorTried);
        var unanswered = false;
        Found? found = null;

        foreach (var step in work.Plan)
        {
            lock (_lock)
            {
                if (!IsUsable(step))
                {
                    unanswered = true;
                    continue;
                }
            }

            try
            {
                found = step.Source == PosterSource.Omdb
                    ? await SearchOmdbAsync(work.Query, step.Key, cancellationToken)
                    : await SearchTmdbAsync(work.Query, step.Key, cancellationToken);
                tried.Add(NameOf(step.Source));

                lock (_lock)
                {
                    // It answered, so whatever was wrong before (an old key, an outage) is over.
                    _rejected.Remove(step.Source);
                    _paused.Remove(step.Source);
                }

                if (found is not null) break;
            }
            catch (KeyRejectedException)
            {
                lock (_lock) _rejected[step.Source] = step.Key;
                unanswered = true;
            }
            catch (TransientException transient)
            {
                lock (_lock) _paused[step.Source] = (_time.GetUtcNow() + (transient.Limit ? LimitPause : TransientPause), transient.Limit);
                unanswered = true;
            }
        }

        // A source that could not answer is never remembered as "not found": the title is asked again once it can.
        if (found is null && unanswered) return;

        lock (_lock)
        {
            LoadIndex()[work.Id] = new Entry
            {
                Title = work.Query.Title, Year = work.Query.Year, IsTv = work.Query.IsTv,
                PosterPath = found?.PosterPath, Source = found?.Source, ImdbId = found?.ImdbId, PosterUrl = found?.PosterUrl,
                Tried = found is null ? string.Join(',', tried) : null,
                CheckedUtc = _time.GetUtcNow(), LastSeenUtc = _time.GetUtcNow()
            };
            SaveIndex();
        }
    }

    /// <summary>One TMDb search. Returns the poster path of the first result that has one, or null when TMDb knows no such title. Throws
    /// KeyRejectedException (401) or TransientException (network, 429, 5xx) so those are never remembered as "not found".</summary>
    private async Task<Found?> SearchTmdbAsync(PosterQuery query, string apiKey, CancellationToken cancellationToken)
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
                    return new Found(TmdbName, posterPath, null, null);
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
            return (status, status is 200 or 401 ? await response.Content.ReadAsStringAsync(cancellationToken) : null);
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

        Entry? entry;
        lock (_lock)
        {
            LoadIndex().TryGetValue(id, out entry);
        }

        if (entry is null || !entry.HasPoster) return null;

        if (entry.FoundBy == OmdbName)
        {
            // OMDb has one picture size, so the size asked for does not matter.
            if (entry.ImdbId is not { } imdbId || !SafeImdbId().IsMatch(imdbId) || entry.PosterUrl is not { } posterUrl || !IsAllowedOmdbPictureUrl(posterUrl)) return null;

            var omdbFile = OmdbFile(imdbId);
            if (File.Exists(omdbFile)) return omdbFile;

            return await SharedDownloadAsync(omdbFile, () => DownloadOmdbImageAsync(id, posterUrl, omdbFile, _stop.Token), cancellationToken);
        }

        var posterPath = entry.PosterPath;
        if (posterPath is null || !SafePosterPath().IsMatch(posterPath)) return null;

        var file = Path.Combine(AppPaths.GetPostersDirectory(), size, posterPath.TrimStart('/'));
        if (File.Exists(file)) return file;

        return await SharedDownloadAsync(file, () => DownloadImageAsync(id, posterPath, size, file, _stop.Token), cancellationToken);
    }

    private static string OmdbFile(string imdbId) => Path.Combine(AppPaths.GetPostersDirectory(), OmdbFolder, imdbId + ".jpg");

    /// <summary>Several requests for one picture (one show, many queued episodes) share one download rather than racing to write the same file.
    /// It is not tied to the first requester's cancellation: whoever is still waiting gets the result.</summary>
    private async Task<string?> SharedDownloadAsync(string file, Func<Task<string?>> start, CancellationToken cancellationToken)
    {
        Task<string?> download;
        lock (_lock)
        {
            if (!_downloads.TryGetValue(file, out download!) || download.IsCompleted)
            {
                download = Task.Run(start);
                _downloads[file] = download;
            }
        }

        try
        {
            return await download.WaitAsync(cancellationToken);
        }
        finally
        {
            if (download.IsCompleted)
            {
                lock (_lock)
                {
                    if (_downloads.TryGetValue(file, out var current) && current == download) _downloads.Remove(file);
                }
            }
        }
    }

    /// <summary>Forgets what was looked up for a title, so the next sighting asks again (its picture was withdrawn).</summary>
    private void Forget(string id)
    {
        lock (_lock)
        {
            LoadIndex().Remove(id);
            SaveIndex();
        }
    }

    private async Task<string?> DownloadImageAsync(string id, string posterPath, string size, string file, CancellationToken cancellationToken)
    {
        try
        {
            var (status, bytes) = await FetchImageAsync($"{_imageBase}/{size}{posterPath}", cancellationToken);
            if (status == 404)
            {
                // The poster was withdrawn: forget it, so the next sighting looks the title up again.
                Forget(id);
                return null;
            }

            if (bytes is null) return null;

            await WriteCacheFileAsync(file, bytes, cancellationToken);
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

    /// <summary>The picture of an OMDb entry whose saved copy is gone (the daily cleanup removed it): downloaded again, and checked again.</summary>
    private async Task<string?> DownloadOmdbImageAsync(string id, string pictureUrl, string file, CancellationToken cancellationToken)
    {
        try
        {
            var (status, bytes) = await FetchImageAsync(pictureUrl, cancellationToken);
            if (status == 404)
            {
                Forget(id);
                return null;
            }

            if (bytes is null || !ImageShape.IsPortrait(bytes)) return null;

            await WriteCacheFileAsync(file, bytes, cancellationToken);
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

    /// <summary>One picture, held in memory (never more than MaxImageBytes). The status is the HTTP status (0 when the request itself failed);
    /// the bytes are null whenever there is no usable picture.</summary>
    private async Task<(int Status, byte[]? Bytes)> FetchImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return (status, null);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes) return (status, null);
            }

            return (status, buffer.ToArray());
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

    private static async Task WriteCacheFileAsync(string file, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // A temp name of its own: the same picture is often asked for by several requests at once (one show, many queued episodes).
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, file, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(50 * attempt, cancellationToken);   // a virus scanner or indexer may hold a new file for a moment
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* the cleanup sweeps stale .tmp files */ }
        }
    }

    // ---- OMDb ---------------------------------------------------------------------------------------------------------------------

    /// <summary>One OMDb lookup by title (and year). OMDb matches loosely, so the answer is only used when it is plainly the same thing: the
    /// same kind (movie or series), the same title (ignoring case, punctuation and a leading "The"), and a portrait picture - a landscape
    /// picture or a different title means no match, and the other source is tried if the user allowed it. The picture is downloaded here so its
    /// shape can be checked. Throws KeyRejectedException or TransientException (network, 5xx, daily limit) so those are never remembered as
    /// "not found".</summary>
    private async Task<Found?> SearchOmdbAsync(PosterQuery query, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{_omdbBase}/?apikey={Uri.EscapeDataString(apiKey)}&t={Uri.EscapeDataString(query.Title)}"
                  + (query.Year is { } year ? $"&y={year}" : "") + $"&type={(query.IsTv ? "series" : "movie")}";

        var (status, body) = await GetAsync(url, cancellationToken);
        if (status is not (200 or 401) || body is null) throw new TransientException();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new TransientException();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TransientException();

            if (!string.Equals(Text(root, "Response"), "True", StringComparison.OrdinalIgnoreCase))
            {
                ThrowIfRefused(Text(root, "Error"));
                if (status == 401) throw new KeyRejectedException();
                return null;   // "Movie not found!" and the like
            }

            if (!string.Equals(Text(root, "Type"), query.IsTv ? "series" : "movie", StringComparison.OrdinalIgnoreCase)) return null;
            if (!PosterNameParser.TitlesMatch(query.Title, Text(root, "Title") ?? "")) return null;

            var imdbId = Text(root, "imdbID");
            var pictureUrl = Text(root, "Poster");
            if (imdbId is null || !SafeImdbId().IsMatch(imdbId) || pictureUrl is null || !IsAllowedOmdbPictureUrl(pictureUrl)) return null;   // "N/A" is no picture

            var file = OmdbFile(imdbId);
            if (File.Exists(file)) return new Found(OmdbName, null, imdbId, pictureUrl);   // saved (and checked) for an earlier lookup of the same title

            var (imageStatus, bytes) = await FetchImageAsync(pictureUrl, cancellationToken);
            if (bytes is null)
            {
                if (imageStatus is 0 or 429 or >= 500) throw new TransientException();
                return null;    // gone (404), refused or too large: no picture
            }

            if (!ImageShape.IsPortrait(bytes)) return null;

            try
            {
                await WriteCacheFileAsync(file, bytes, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TransientException();   // the disk, not OMDb: try again later
            }

            return new Found(OmdbName, null, imdbId, pictureUrl);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>OMDb answers a refused key and a used-up daily limit with an Error text (and usually HTTP 401).</summary>
    private static void ThrowIfRefused(string? error)
    {
        if (error is null) return;
        if (error.Contains("limit", StringComparison.OrdinalIgnoreCase)) throw new TransientException { Limit = true };
        if (error.Contains("api key", StringComparison.OrdinalIgnoreCase)) throw new KeyRejectedException();
    }

    /// <summary>OMDb's pictures are hosted by Amazon (IMDb's host). Only an https address there is ever downloaded, whatever a reply says.</summary>
    private static bool IsAllowedOmdbPictureUrl(string url)
    {
        if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host == "m.media-amazon.com" || host.EndsWith(".media-amazon.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".ssl-images-amazon.com", StringComparison.OrdinalIgnoreCase);
    }

    // ---- the Settings card --------------------------------------------------------------------------------------------------------

    public async Task<PosterTestResult> TestKeyAsync(PosterSource source, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new PosterTestResult(false, source == PosterSource.Omdb ? "Enter an OMDb API key first." : "Enter a TMDb API key first.");

        return source == PosterSource.Omdb ? await TestOmdbKeyAsync(apiKey.Trim(), cancellationToken) : await TestTmdbKeyAsync(apiKey.Trim(), cancellationToken);
    }

    private async Task<PosterTestResult> TestTmdbKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var (status, _) = await GetAsync($"{_apiBase}/authentication?api_key={Uri.EscapeDataString(apiKey)}", cancellationToken);
        return status switch
        {
            200 => new PosterTestResult(true, "TMDb accepted the key."),
            401 => new PosterTestResult(false, "TMDb rejected this key. Check that you copied the API key (the shorter one), not the Read Access Token."),
            0 => new PosterTestResult(false, "Could not reach TMDb. Check the internet connection and try again."),
            _ => new PosterTestResult(false, $"TMDb answered with an unexpected status ({status}). Try again later.")
        };
    }

    /// <summary>Looks one well-known film up by its IMDb id, which uses a lookup of the key's daily allowance but no picture.</summary>
    private async Task<PosterTestResult> TestOmdbKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var (status, body) = await GetAsync($"{_omdbBase}/?apikey={Uri.EscapeDataString(apiKey)}&i=tt0111161", cancellationToken);
        if (status == 0) return new PosterTestResult(false, "Could not reach OMDb. Check the internet connection and try again.");

        string? response = null, error = null;
        if (body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    response = Text(doc.RootElement, "Response");
                    error = Text(doc.RootElement, "Error");
                }
            }
            catch (JsonException)
            {
                // an answer that is not OMDb's: reported below as unexpected
            }
        }

        if (string.Equals(response, "True", StringComparison.OrdinalIgnoreCase)) return new PosterTestResult(true, "OMDb accepted the key.");
        if (error is not null && error.Contains("limit", StringComparison.OrdinalIgnoreCase))
            return new PosterTestResult(false, "OMDb says this key's daily limit has been reached, so it cannot be checked right now. Try again tomorrow.");
        if (status == 401 || (error is not null && error.Contains("api key", StringComparison.OrdinalIgnoreCase)))
            return new PosterTestResult(false, "OMDb rejected this key. Check that you typed it correctly and that you clicked the activation link in the email OMDb sent.");

        return new PosterTestResult(false, $"OMDb answered with an unexpected status ({status}). Try again later.");
    }

    public PosterCacheInfo GetCacheInfo()
    {
        int posters;
        string? problem = null;
        lock (_lock)
        {
            posters = LoadIndex().Values.Count(e => e.HasPoster);

            var problems = new List<string>();
            foreach (var source in new[] { PosterSource.Tmdb, PosterSource.Omdb })
            {
                var label = source == PosterSource.Omdb ? "OMDb" : "TMDb";
                if (_rejected.ContainsKey(source)) problems.Add($"{label} rejected the saved key, so no posters are being looked up. Fix the key and save.");
                else if (_paused.TryGetValue(source, out var pause) && _time.GetUtcNow() < pause.Until)
                    problems.Add(pause.Limit ? $"{label}'s daily lookup limit was reached; Compressarr will try again in a while." : $"{label} could not be reached a moment ago; Compressarr will try again shortly.");
            }

            if (problems.Count > 0) problem = string.Join(" ", problems);
        }

        long bytes = 0;
        var directory = AppPaths.GetPostersDirectory();
        if (Directory.Exists(directory))
        {
            foreach (var folder in CacheFolders)
            {
                var folderPath = Path.Combine(directory, folder);
                if (Directory.Exists(folderPath)) bytes += Directory.EnumerateFiles(folderPath).Sum(f => new FileInfo(f).Length);
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
                foreach (var folder in CacheFolders)
                {
                    var folderPath = Path.Combine(directory, folder);
                    if (!Directory.Exists(folderPath)) continue;
                    removed += Directory.EnumerateFiles(folderPath).Count();
                    Directory.Delete(folderPath, recursive: true);
                }

                if (File.Exists(IndexPath)) File.Delete(IndexPath);
            }

            _index = new Dictionary<string, Entry>();
            _rejected.Clear();
            _paused.Clear();
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

            foreach (var stale in index.Where(kv => !kv.Value.HasPoster && kv.Value.CheckedUtc < cutoff).Select(kv => kv.Key).ToList())
            {
                index.Remove(stale);
                changed = true;
            }

            // The file name each kept picture is saved under: a TMDb poster path, or an OMDb picture's IMDb id.
            var keep = index.Values
                .Where(e => e.HasPoster && (e.LastSeenUtc ?? e.CheckedUtc) >= cutoff)
                .Select(e => e.FoundBy == OmdbName ? e.ImdbId + ".jpg" : e.PosterPath!.TrimStart('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in CacheFolders)
            {
                var sizeDirectory = Path.Combine(AppPaths.GetPostersDirectory(), folder);
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
            // Remembering is a courtesy: the poster still works for this session. The marks stay pending (Dispose tries again), and the next
            // attempt waits the usual interval instead of failing again on every poll.
            _lastIndexSave = _time.GetUtcNow();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_indexDirty) SaveIndex();
        }

        _stop.Cancel();
        _work.Writer.TryComplete();
        _stop.Dispose();
    }
}
