using System.Reflection;
using System.Text;
using System.Text.Json;
using Compressarr.Core.Config;

namespace Compressarr.Core.FFmpeg;

/// <summary>
/// Compressarr's ffmpeg profile catalog: the built-ins that ship inside the app (locked) plus the
/// user's own, kept in %AppData%\Compressarr\Profiles\ffmpeg-profiles.json (included in backups and
/// in Export/Import config). Names are unique within ffmpeg's own catalog - a HandBrake profile may
/// share a name (the built-ins deliberately do), since a lane only ever picks from its own
/// encoder's list.
/// </summary>
public interface IFFmpegProfileStore
{
    /// <summary>Built-ins first, then the user's profiles in stored order. Copies - changing one does nothing.</summary>
    IReadOnlyList<FFmpegProfile> GetAll();

    IReadOnlyList<FFmpegProfile> GetBuiltIns();

    FFmpegProfile? Find(string name);

    /// <summary>Set when the user's file exists but could not be read - only the built-ins are then
    /// offered, and nothing will overwrite that file.</summary>
    string? UserFileError { get; }

    /// <summary>Appends profiles to the user's file. Names must be non-empty and unique
    /// (case-insensitively) among everything; any clash throws InvalidOperationException and nothing is written.</summary>
    void AddUserProfiles(IEnumerable<FFmpegProfile> profiles);

    /// <summary>Replaces one of the user's profiles (the new one may carry a new name), keeping its place.</summary>
    void ReplaceUserProfile(string currentName, FFmpegProfile profile);

    void RemoveUserProfile(string name);

    /// <summary>Names of catalog profiles whose stored copy no longer matches what was installed. They are not offered to lanes.</summary>
    IReadOnlyList<string> DamagedCatalogProfiles { get; }

    /// <summary>Adds a profile installed from a catalog, with the record of where it came from. The name must be unique among the built-ins,
    /// the catalog profiles and the user's; a clash throws InvalidOperationException and nothing is written.</summary>
    void AddCatalogProfile(FFmpegProfile profile, Compressarr.Core.Catalog.InstalledCatalogProfile record);

    /// <summary>Removes a catalog profile (and its record).</summary>
    void RemoveCatalogProfile(string name);
}

public sealed class FFmpegProfileStore : IFFmpegProfileStore
{
    private const string BundledResourceName = "Compressarr.Core.Assets.ffmpeg-presets.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed class FileShape
    {
        public List<FFmpegProfile> Profiles { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly Lazy<List<FFmpegProfile>> _bundled = new(LoadBundled);

    private (DateTime WriteUtc, long Length) _stamp;
    private List<FFmpegProfile>? _userCache;
    private string? _userError;

    private (DateTime WriteUtc, long Length, DateTime RecordUtc, long RecordLength) _catalogStamp;
    private List<FFmpegProfile>? _catalogCache;
    private List<string> _catalogDamaged = new();

    public string? UserFileError
    {
        get
        {
            lock (_gate)
            {
                LoadUserLocked();
                return _userError;
            }
        }
    }

    public IReadOnlyList<FFmpegProfile> GetBuiltIns() => _bundled.Value.Select(p => p.Clone()).ToList();

    public IReadOnlyList<string> DamagedCatalogProfiles
    {
        get
        {
            lock (_gate)
            {
                LoadCatalogLocked();
                return _catalogDamaged.ToList();
            }
        }
    }

    public IReadOnlyList<FFmpegProfile> GetAll()
    {
        lock (_gate)
        {
            return _bundled.Value.Concat(LoadCatalogLocked()).Concat(LoadUserLocked()).Select(p => p.Clone()).ToList();
        }
    }

    public void AddCatalogProfile(FFmpegProfile profile, Compressarr.Core.Catalog.InstalledCatalogProfile record)
    {
        lock (_gate)
        {
            var existing = LoadCatalogLocked();
            var user = LoadUserLocked();
            var name = profile.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A profile needs a name.");
            if (_bundled.Value.Concat(existing).Concat(user).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A profile named '{name}' already exists.");
            }

            var copy = profile.Clone();
            copy.Name = name;
            copy.IsBuiltIn = false;
            copy.IsCatalog = false; // stored plain; the flag is set when it is read back

            // The file as it is, damaged entries included; one that cannot be read is never overwritten.
            var inFile = ReadCatalogProfilesLocked() ?? throw new InvalidOperationException("The catalog profile file could not be read, so nothing was changed. Delete catalog-ffmpeg.json to start it again.");
            if (inFile.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A catalog profile named '{name}' is already stored but reads as damaged. Delete it first, then add it again.");
            }

            // The record first: a record without its profile is harmless, a profile without its record would read as damaged.
            Compressarr.Core.Catalog.CatalogRecordFile.Add(record with { Name = name, Engine = "ffmpeg", DefinitionSha = Compressarr.Core.Catalog.CatalogHash.OfFFmpegProfile(copy) });
            WriteCatalogLocked(inFile.Append(copy).ToList());
        }
    }

    public void RemoveCatalogProfile(string name)
    {
        lock (_gate)
        {
            LoadCatalogLocked();
            var all = ReadCatalogProfilesLocked() ?? throw new InvalidOperationException("The catalog profile file could not be read, so nothing was changed. Delete catalog-ffmpeg.json to start it again.");
            var index = all.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException($"There is no catalog profile named '{name}'.");

            WriteCatalogLocked(all.Where((_, i) => i != index).ToList());
            Compressarr.Core.Catalog.CatalogRecordFile.Remove("ffmpeg", name);
        }
    }

    public FFmpegProfile? Find(string name) =>
        GetAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddUserProfiles(IEnumerable<FFmpegProfile> profiles)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            EnsureWritableLocked();

            var taken = new HashSet<string>(_bundled.Value.Concat(LoadCatalogLocked()).Concat(existing).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var added = new List<FFmpegProfile>();
            foreach (var profile in profiles)
            {
                var name = profile.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A profile needs a name.");
                if (!taken.Add(name)) throw new InvalidOperationException($"A profile named '{name}' already exists.");

                var copy = profile.Clone();
                copy.Name = name;
                copy.IsBuiltIn = false;
                added.Add(copy);
            }

            if (added.Count == 0) return;
            WriteLocked(existing.Concat(added).ToList());
        }
    }

    public void ReplaceUserProfile(string currentName, FFmpegProfile profile)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            EnsureWritableLocked();

            var index = existing.FindIndex(p => string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw MissingOrLocked(currentName, "changed");

            var newName = profile.Name?.Trim();
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("A profile needs a name.");
            var clash = _bundled.Value.Concat(LoadCatalogLocked()).Concat(existing.Where((_, i) => i != index))
                .Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (clash) throw new InvalidOperationException($"A profile named '{newName}' already exists.");

            var copy = profile.Clone();
            copy.Name = newName;
            copy.IsBuiltIn = false;
            var all = existing.ToList();
            all[index] = copy;
            WriteLocked(all);
        }
    }

    public void RemoveUserProfile(string name)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            EnsureWritableLocked();

            var index = existing.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw MissingOrLocked(name, "deleted");

            WriteLocked(existing.Where((_, i) => i != index).ToList());
        }
    }

    private InvalidOperationException MissingOrLocked(string name, string verb) =>
        _bundled.Value.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ? new InvalidOperationException($"'{name}' is a built-in profile and can't be {verb}. Duplicate it to make your own.")
            : LoadCatalogLocked().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ? new InvalidOperationException($"'{name}' is a catalog profile and can't be {verb}. Duplicate it to make your own.")
                : new InvalidOperationException($"There is no profile named '{name}'.");

    private void EnsureWritableLocked()
    {
        if (_userError is not null)
        {
            throw new InvalidOperationException($"Your ffmpeg profile file could not be read, so nothing was changed: {_userError}");
        }
    }

    private List<FFmpegProfile> LoadUserLocked()
    {
        var path = AppPaths.GetFFmpegProfilesFilePath();
        if (!File.Exists(path))
        {
            _stamp = default;
            _userError = null;
            return _userCache = new List<FFmpegProfile>();
        }

        var info = new FileInfo(path);
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (_userCache is not null && stamp == _stamp) return _userCache;

        _stamp = stamp;
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Options)
                ?? throw new JsonException("The file is empty.");
            _userError = null;
            return _userCache = shape.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _userError = $"Could not read '{path}': {ex.Message}";
            return _userCache = new List<FFmpegProfile>();
        }
    }

    /// <summary>The catalog profiles that are still what was installed; a profile with no record, or whose stored copy no longer matches its
    /// record, is left out and reported in DamagedCatalogProfiles.</summary>
    private List<FFmpegProfile> LoadCatalogLocked()
    {
        var path = AppPaths.GetCatalogFFmpegFilePath();
        var recordPath = AppPaths.GetCatalogRecordsFilePath();
        var info = File.Exists(path) ? new FileInfo(path) : null;
        var recordInfo = File.Exists(recordPath) ? new FileInfo(recordPath) : null;
        var stamp = (info?.LastWriteTimeUtc ?? default, info?.Length ?? 0, recordInfo?.LastWriteTimeUtc ?? default, recordInfo?.Length ?? 0);
        if (_catalogCache is not null && stamp == _catalogStamp) return _catalogCache;

        _catalogStamp = stamp;
        _catalogDamaged = new List<string>();
        if (info is null) return _catalogCache = new List<FFmpegProfile>();

        var records = Compressarr.Core.Catalog.CatalogRecordFile.Load()
            .Where(r => string.Equals(r.Engine, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var profiles = new List<FFmpegProfile>();
        foreach (var profile in ReadCatalogProfilesLocked() ?? new List<FFmpegProfile>())
        {
            if (records.TryGetValue(profile.Name, out var record) && record.DefinitionSha == Compressarr.Core.Catalog.CatalogHash.OfFFmpegProfile(profile))
            {
                var copy = profile.Clone();
                copy.IsCatalog = true;
                profiles.Add(copy);
            }
            else
            {
                _catalogDamaged.Add(profile.Name);
            }
        }

        return _catalogCache = profiles;
    }

    /// <summary>Every profile in the catalog file (also ones that read as damaged); empty when there is no file, null when there is one that cannot be read.</summary>
    private static List<FFmpegProfile>? ReadCatalogProfilesLocked()
    {
        var path = AppPaths.GetCatalogFFmpegFilePath();
        try
        {
            if (!File.Exists(path)) return new List<FFmpegProfile>();
            return (JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Options)?.Profiles ?? new List<FFmpegProfile>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCatalogLocked(List<FFmpegProfile> profiles)
    {
        var path = AppPaths.GetCatalogFFmpegFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FileShape { Profiles = profiles }, Options));

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }

        _catalogCache = null;
    }

    private void WriteLocked(List<FFmpegProfile> profiles)
    {
        var path = AppPaths.GetFFmpegProfilesFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FileShape { Profiles = profiles }, Options));

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }

        _userCache = null;
    }

    private static List<FFmpegProfile> LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{BundledResourceName}' not found.");
        var shape = JsonSerializer.Deserialize<FileShape>(stream, Options)!;
        foreach (var profile in shape.Profiles) profile.IsBuiltIn = true;
        return shape.Profiles;
    }
}
