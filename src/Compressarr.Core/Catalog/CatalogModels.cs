using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Compressarr.Core.Config;

namespace Compressarr.Core.Catalog;

/// <summary>The catalog index (catalog.json): which profile versions a source publishes. See docs/design-preset-catalog.md.</summary>
public sealed class CatalogDocument
{
    /// <summary>The catalog format version. An app that doesn't know it shows "update Compressarr" and does nothing else.</summary>
    public int Schema { get; set; }
    public CatalogInfo? Catalog { get; set; }
    public string? Updated { get; set; }
    public List<CatalogEntry> Profiles { get; set; } = new();
}

public sealed class CatalogInfo
{
    public string Name { get; set; } = "";
    public string Maintainer { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>One published profile version. Immutable once published: a change is a new version.</summary>
public sealed class CatalogEntry
{
    /// <summary>The profile name without the version ("Compressarr UHD HEVC (HDR)").</summary>
    public string Family { get; set; } = "";
    public int Version { get; set; }

    /// <summary>"handbrake" or "ffmpeg".</summary>
    public string Engine { get; set; } = "";

    /// <summary>The profile file, relative to the catalog (profiles/handbrake/x.json).</summary>
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string MinAppVersion { get; set; } = "";
    public string Description { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string Published { get; set; } = "";
    public string Author { get; set; } = "";
    public string? SourceUrl { get; set; }

    /// <summary>Null, or why this version should no longer be used. A warning only: installed copies are never changed.</summary>
    public string? Deprecated { get; set; }

    /// <summary>Encoders the profile needs (x265, av1_nvenc...), shown before install.</summary>
    public List<string> Requires { get; set; } = new();

    /// <summary>The name the profile is installed under, and the name inside its file: "family vN".</summary>
    [JsonIgnore]
    public string Name => $"{Family} v{Version}";

    [JsonIgnore]
    public bool IsHandBrake => string.Equals(Engine, "handbrake", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFFmpeg => string.Equals(Engine, "ffmpeg", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where catalog profiles come from. The first version has exactly one source, the official catalog; whether a source is
/// official is decided by the app (this record's Kind), never by anything in the files it serves.</summary>
public sealed record CatalogSource(string Id, string Name, string Url, CatalogSourceKind Kind);

public enum CatalogSourceKind
{
    Official,
    Community
}

/// <summary>What Compressarr remembers about a profile installed from a catalog (catalog-installed.json). The profile itself lives in
/// the catalog profile file for its encoder; this is where it came from and what it must still match.</summary>
public sealed record InstalledCatalogProfile(
    string Name,
    string Engine,
    string Family,
    int Version,
    string Sha256,
    string DefinitionSha,
    string SourceName,
    string SourceUrl,
    string Release,
    string Author,
    DateTime InstalledUtc);

/// <summary>Reads and writes catalog-installed.json. Static, like the stores' other AppPaths-based files: it is small, written
/// atomically, and a missing or damaged file is simply "nothing installed".</summary>
public static class CatalogRecordFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    private sealed class Shape
    {
        public List<InstalledCatalogProfile> Installed { get; set; } = new();
    }

    public static List<InstalledCatalogProfile> Load()
    {
        lock (Gate)
        {
            return LoadLocked();
        }
    }

    public static void Add(InstalledCatalogProfile record)
    {
        lock (Gate)
        {
            var all = LoadLocked();
            all.RemoveAll(r => string.Equals(r.Engine, record.Engine, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Name, record.Name, StringComparison.OrdinalIgnoreCase));
            all.Add(record);
            SaveLocked(all);
        }
    }

    public static void Remove(string engine, string name)
    {
        lock (Gate)
        {
            var all = LoadLocked();
            if (all.RemoveAll(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) > 0) SaveLocked(all);
        }
    }

    /// <summary>Replaces every record (a restore from a backup or an imported configuration).</summary>
    public static void ReplaceAll(IEnumerable<InstalledCatalogProfile> records)
    {
        lock (Gate)
        {
            SaveLocked(records.ToList());
        }
    }

    private static List<InstalledCatalogProfile> LoadLocked()
    {
        var path = AppPaths.GetCatalogRecordsFilePath();
        try
        {
            if (!File.Exists(path)) return new List<InstalledCatalogProfile>();
            return JsonSerializer.Deserialize<Shape>(File.ReadAllText(path), Options)?.Installed ?? new List<InstalledCatalogProfile>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<InstalledCatalogProfile>();
        }
    }

    private static void SaveLocked(List<InstalledCatalogProfile> all)
    {
        var path = AppPaths.GetCatalogRecordsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Shape { Installed = all }, Options), Encoding.UTF8);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>The fingerprints used to notice a catalog profile that no longer matches what was installed.</summary>
public static class CatalogHash
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string OfBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A HandBrake profile's definition, serialised the same way every time.</summary>
    public static string OfDefinition(JsonNode definition) =>
        OfBytes(Encoding.UTF8.GetBytes(definition.ToJsonString(Compact)));

    public static string OfFFmpegProfile(Compressarr.Core.FFmpeg.FFmpegProfile profile) =>
        OfBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false })));
}
