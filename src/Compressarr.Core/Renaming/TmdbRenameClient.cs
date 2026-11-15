using System.Text.Json;

namespace Compressarr.Core.Renaming;

/// <summary>TMDB refused the API key (HTTP 401). Nothing can be looked up until it is fixed.</summary>
public sealed class TmdbKeyRejectedException : Exception
{
    public TmdbKeyRejectedException() : base("TMDB rejected the API key.") { }
}

/// <summary>TMDB could not be reached or answered with an error (network, too many requests, a server fault). Never read as "no such title": the file is left alone and tried again on a later pass.</summary>
public sealed class TmdbUnavailableException : Exception
{
    public TmdbUnavailableException() : base("TMDB could not be reached.") { }
}

/// <summary>The two TMDB lookups the renamer needs. Both throw <see cref="TmdbKeyRejectedException"/> or <see cref="TmdbUnavailableException"/>; neither ever puts the key in a message.</summary>
public interface ITmdbRenameClient
{
    /// <summary>The first page of TMDB's search for a film or a show, in TMDB's own order. Empty when TMDB knows nothing by that name.</summary>
    Task<IReadOnlyList<TmdbCandidate>> SearchAsync(string apiKey, bool tv, string title, int? year, string language, CancellationToken cancellationToken);

    /// <summary>One film or show by its TMDB id (the Match dialog trusts the id, never a title sent by the page). Null when TMDB has no such id.</summary>
    Task<TmdbCandidate?> GetDetailsAsync(string apiKey, bool tv, int id, string language, CancellationToken cancellationToken);

    /// <summary>The episodes of one season of a show: episode number to name. Empty when TMDB has no such season.</summary>
    Task<IReadOnlyDictionary<int, string>> GetSeasonAsync(string apiKey, int showId, int season, string language, CancellationToken cancellationToken);
}

/// <summary>TMDB's v3 API over the shared HTTP client factory, one request at a time with a small gap between them (TMDB allows far more; this is courtesy, and keeps a large first scan gentle).
/// Only the title, year and language are sent, never a path or anything else about the file.</summary>
public sealed class TmdbRenameClient : ITmdbRenameClient
{
    private const string DefaultApiBase = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiBase;
    private readonly TimeSpan _gap;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public TmdbRenameClient(IHttpClientFactory httpFactory) : this(httpFactory, DefaultApiBase, TimeSpan.FromMilliseconds(120)) { }

    internal TmdbRenameClient(IHttpClientFactory httpFactory, string apiBase, TimeSpan gap)
    {
        _httpFactory = httpFactory;
        _apiBase = apiBase.TrimEnd('/');
        _gap = gap;
    }

    public async Task<IReadOnlyList<TmdbCandidate>> SearchAsync(string apiKey, bool tv, string title, int? year, string language, CancellationToken cancellationToken)
    {
        var url = $"{_apiBase}/search/{(tv ? "tv" : "movie")}?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(title)}&language={Uri.EscapeDataString(language)}&include_adult=false"
                  + (year is { } y ? $"&{(tv ? "first_air_date_year" : "year")}={y}" : "");

        var body = await GetAsync(url, cancellationToken) ?? throw new TmdbUnavailableException();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return Array.Empty<TmdbCandidate>();

            return results.EnumerateArray().Select(r => ReadCandidate(r, tv)).OfType<TmdbCandidate>().ToList();
        }
        catch (JsonException)
        {
            throw new TmdbUnavailableException();
        }
    }

    public async Task<TmdbCandidate?> GetDetailsAsync(string apiKey, bool tv, int id, string language, CancellationToken cancellationToken)
    {
        var url = $"{_apiBase}/{(tv ? "tv" : "movie")}/{id}?api_key={Uri.EscapeDataString(apiKey)}&language={Uri.EscapeDataString(language)}";
        var body = await GetAsync(url, cancellationToken, notFoundIsEmpty: true);
        if (body is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ReadCandidate(doc.RootElement, tv);
        }
        catch (JsonException)
        {
            throw new TmdbUnavailableException();
        }
    }

    /// <summary>One result of a search, or the details of one title: the same fields under the same names. Null when it has no id or no name.</summary>
    private static TmdbCandidate? ReadCandidate(JsonElement r, bool tv)
    {
        if (!r.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) return null;

        var name = Text(r, tv ? "name" : "title");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var countries = new List<string>();
        if (r.TryGetProperty("origin_country", out var origin) && origin.ValueKind == JsonValueKind.Array)
        {
            countries.AddRange(origin.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!));
        }

        return new TmdbCandidate(id.GetInt32(), tv, name, Text(r, tv ? "original_name" : "original_title"), YearOf(Text(r, tv ? "first_air_date" : "release_date")), countries,
            r.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0, Text(r, "overview"), Text(r, "poster_path"));
    }

    public async Task<IReadOnlyDictionary<int, string>> GetSeasonAsync(string apiKey, int showId, int season, string language, CancellationToken cancellationToken)
    {
        var url = $"{_apiBase}/tv/{showId}/season/{season}?api_key={Uri.EscapeDataString(apiKey)}&language={Uri.EscapeDataString(language)}";
        var body = await GetAsync(url, cancellationToken, notFoundIsEmpty: true);
        var episodes = new Dictionary<int, string>();
        if (body is null) return episodes;      // TMDB has no such season

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("episodes", out var list) || list.ValueKind != JsonValueKind.Array) return episodes;

            foreach (var e in list.EnumerateArray())
            {
                if (e.TryGetProperty("episode_number", out var n) && n.ValueKind == JsonValueKind.Number) episodes[n.GetInt32()] = Text(e, "name") ?? "";
            }

            return episodes;
        }
        catch (JsonException)
        {
            throw new TmdbUnavailableException();
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) && year > 0 ? year : null;

    /// <summary>One GET. The body on success; null for a 404 when <paramref name="notFoundIsEmpty"/> (the lookup answered, there is just nothing there). A 401 is a rejected key; anything else that is
    /// not a 200 (a 429, a 5xx, no network, a timeout) is unavailable. A cancelled request is cancelled, not unavailable.</summary>
    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken, bool notFoundIsEmpty = false)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            var wait = _gap - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);

            try
            {
                using var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr");
                using var response = await client.GetAsync(url, cancellationToken);
                var status = (int)response.StatusCode;
                if (status == 401) throw new TmdbKeyRejectedException();
                if (status == 404 && notFoundIsEmpty) return null;
                if (status != 200) throw new TmdbUnavailableException();

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not (TmdbKeyRejectedException or TmdbUnavailableException))
            {
                throw new TmdbUnavailableException();      // never carries the request (and so never the key)
            }
        }
        finally
        {
            _lastRequestUtc = DateTime.UtcNow;
            _turn.Release();
        }
    }
}
