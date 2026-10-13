using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Compressarr.Core.Catalog;

/// <summary>Raised for anything that stops a catalog from being read or a profile from being fetched; the message is shown to the user.</summary>
public sealed class CatalogException : Exception
{
    public CatalogException(string message) : base(message) { }
}

/// <summary>A catalog that has been read: where it came from, the index, and which published release it is (the tag, or "local").</summary>
public sealed record CatalogListing(CatalogSource Source, CatalogDocument Document, string Release);

/// <summary>Fetches catalog.json and the profile files it lists. Pure transport: the checksum and the validators live in
/// <see cref="CatalogService"/>, which treats everything fetched here as untrusted.</summary>
public interface ICatalogClient
{
    CatalogSource Source { get; }

    /// <summary>Reads the catalog. Throws <see cref="CatalogException"/> with a plain-English reason when it can't (offline, no published
    /// release yet, a catalog format this Compressarr doesn't know).</summary>
    Task<CatalogListing> LoadAsync(CancellationToken cancellationToken);

    /// <summary>The bytes of one profile file from the same release as <paramref name="listing"/>.</summary>
    Task<byte[]> DownloadAsync(CatalogListing listing, CatalogEntry entry, CancellationToken cancellationToken);
}

public sealed class CatalogClient : ICatalogClient
{
    /// <summary>The one catalog Compressarr ships with. Fixed in the app: there is no field to change it, so a profile can only come from
    /// somewhere the user can see in the dialog ("Importing from:").</summary>
    public static readonly CatalogSource Official = new("official", "Compressarr Presets", "https://github.com/Compressarr/Compressarr-Presets", CatalogSourceKind.Official);

    /// <summary>For development and tests only: a folder (or an http(s) base address) holding catalog.json and the profile files, used instead of
    /// the official catalog. Environment variable COMPRESSARR_CATALOG_SOURCE.</summary>
    public const string OverrideVariable = "COMPRESSARR_CATALOG_SOURCE";

    private const string Owner = "Compressarr";
    private const string Repository = "Compressarr-Presets";
    private const long MaxCatalogBytes = 2 * 1024 * 1024;
    private const long MaxProfileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _override;

    public CatalogClient(IHttpClientFactory httpClientFactory) : this(httpClientFactory, Environment.GetEnvironmentVariable(OverrideVariable))
    {
    }

    internal CatalogClient(IHttpClientFactory httpClientFactory, string? sourceOverride)
    {
        _httpClientFactory = httpClientFactory;
        _override = string.IsNullOrWhiteSpace(sourceOverride) ? null : sourceOverride.Trim();
    }

    public CatalogSource Source => _override is null ? Official : new CatalogSource("override", "Compressarr Presets (test source)", _override, CatalogSourceKind.Official);

    public async Task<CatalogListing> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string release;
            byte[] bytes;
            if (_override is not null && Directory.Exists(_override))
            {
                release = "local";
                var path = Path.Combine(_override, "catalog.json");
                if (!File.Exists(path)) throw new CatalogException($"There is no catalog.json in {_override}.");
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            else if (_override is not null)
            {
                release = "test";
                bytes = await GetBytesAsync(Combine(_override, "catalog.json"), MaxCatalogBytes, cancellationToken);
            }
            else
            {
                release = await LatestReleaseTagAsync(cancellationToken);
                bytes = await GetBytesAsync(RawUrl(release, "catalog.json"), MaxCatalogBytes, cancellationToken);
            }

            if (bytes.Length > MaxCatalogBytes) throw new CatalogException("The catalog is larger than expected, so it was not used.");
            CatalogDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<CatalogDocument>(Encoding.UTF8.GetString(bytes), Json);
            }
            catch (JsonException)
            {
                throw new CatalogException("The catalog could not be read (it is not valid JSON).");
            }

            if (document is null) throw new CatalogException("The catalog is empty.");
            if (document.Schema != 1) throw new CatalogException("This catalog needs a newer Compressarr - update Compressarr to browse it.");
            return new CatalogListing(Source, document, release);
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogException(Compressarr.Core.Net.NetworkProblem.Describe(ex, "the catalog"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogException("The catalog did not answer in time. Check your internet connection and try again.");
        }
    }

    public async Task<byte[]> DownloadAsync(CatalogListing listing, CatalogEntry entry, CancellationToken cancellationToken)
    {
        var file = entry.File.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(file) || file.StartsWith('/') || file.Contains("..", StringComparison.Ordinal) || file.Contains(':'))
        {
            throw new CatalogException($"The catalog lists an unsafe file name for {entry.Name}, so it was not fetched.");
        }

        try
        {
            if (_override is not null && Directory.Exists(_override))
            {
                var root = Path.GetFullPath(_override);
                var full = Path.GetFullPath(Path.Combine(root, file));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new CatalogException($"{entry.File} is not in the test catalog folder.");
                var data = await File.ReadAllBytesAsync(full, cancellationToken);
                if (data.Length > MaxProfileBytes) throw new CatalogException($"{entry.Name} is larger than expected, so it was not used.");
                return data;
            }

            var url = _override is not null ? Combine(_override, file) : RawUrl(listing.Release, file);
            return await GetBytesAsync(url, MaxProfileBytes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogException($"{entry.Name} couldn't be downloaded. {Compressarr.Core.Net.NetworkProblem.Describe(ex, "the catalog")}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogException($"Downloading {entry.Name} took too long. Try again.");
        }
    }

    private static string Combine(string baseUrl, string file) => baseUrl.TrimEnd('/') + "/" + file.TrimStart('/');

    private static string RawUrl(string tag, string file) =>
        $"https://raw.githubusercontent.com/{Owner}/{Repository}/{Uri.EscapeDataString(tag)}/{file}";

    private async Task<string> LatestReleaseTagAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(CatalogClient));
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new CatalogException("The catalog has no published release yet.");
        }
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) throw new CatalogException("The catalog's latest release has no tag.");
        return tag;
    }

    private async Task<byte[]> GetBytesAsync(string url, long limit, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(CatalogClient));
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.2");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw new CatalogException("A catalog file is larger than expected, so it was not used.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) throw new CatalogException("A catalog file is larger than expected, so it was not used.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
