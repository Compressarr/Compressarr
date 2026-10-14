using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;

namespace Compressarr.Core.Presets;

/// <summary>One HandBrake profile Compressarr knows about: a built-in that ships inside the app
/// (locked), or one of the user's own (created, duplicated or imported). Definition is the full
/// HandBrake preset node, kept exactly as stored so nothing the editor doesn't show is lost.</summary>
public sealed record HandBrakeProfile(string Name, string? FileFormat, bool IsBuiltIn, JsonObject Definition, bool IsCatalog = false);

/// <summary>
/// Compressarr's own HandBrake profile catalog - the only preset source Compressarr ever uses.
/// Built-ins are embedded in the app; the user's profiles live in
/// %AppData%\Compressarr\Profiles\handbrake-profiles.json and are backed up with the rest of the
/// settings. HandBrake itself is handed one generated file (handbrake-active.json: built-ins plus
/// the user's profiles) through a single --preset-import-file; the HandBrake app's own presets.json
/// is never read at run time and never written.
/// </summary>
public interface IHandBrakeProfileStore
{
    /// <summary>Built-ins first, then the user's profiles in stored order.</summary>
    IReadOnlyList<HandBrakeProfile> GetAll();

    HandBrakeProfile? Find(string name);

    /// <summary>The profiles that ship inside the app (locked; always the same for a given version).</summary>
    IReadOnlyList<HandBrakeProfile> GetBuiltIns();

    /// <summary>Set when the user's profile file exists but could not be read - the profiles in it
    /// are then not offered (and no write will overwrite it). Null when everything is fine.</summary>
    string? UserFileError { get; }

    /// <summary>Set when the file holding the profiles added from a catalog exists but could not be read: those profiles are then not offered
    /// (and no install will overwrite the file). Null when everything is fine.</summary>
    string? CatalogFileError { get; }

    /// <summary>Names of catalog profiles whose stored copy no longer matches what was installed (edited by hand, damaged). They are
    /// not offered to lanes; deleting and adding them again fixes it.</summary>
    IReadOnlyList<string> DamagedCatalogProfiles { get; }

    /// <summary>Adds a profile installed from a catalog, with the record of where it came from. The name must be unique among the
    /// built-ins, the catalog profiles and the user's; a clash throws InvalidOperationException and nothing is written.</summary>
    void AddCatalogProfile(JsonObject definition, Compressarr.Core.Catalog.InstalledCatalogProfile record);

    /// <summary>Removes a catalog profile (and its record). Built-ins and the user's own can't be removed this way.</summary>
    void RemoveCatalogProfile(string name);

    /// <summary>Appends profiles to the user's file. Names must be non-empty and unique
    /// (case-insensitively) across the built-ins and the user's existing profiles; any clash throws
    /// InvalidOperationException and nothing is written.</summary>
    void AddUserProfiles(IEnumerable<JsonObject> definitions);

    /// <summary>Replaces one of the user's profiles with a new definition (which may carry a new name),
    /// keeping its place in the list. Built-ins are locked: InvalidOperationException. The new name
    /// must be unique among everything else (case changes of the profile's own name are fine).</summary>
    void ReplaceUserProfile(string currentName, JsonObject definition);

    /// <summary>Removes one of the user's profiles. Built-ins can't be removed.</summary>
    void RemoveUserProfile(string name);

    /// <summary>Makes sure handbrake-active.json matches the built-ins plus the user's profiles
    /// and returns its path. Generated deterministically in memory and compared with what is on
    /// disk - the file is only rewritten (atomically: temp file, then rename) when it is missing or
    /// different, so a normal start or encode touches nothing.</summary>
    string EnsureActiveFile();
}

public sealed class HandBrakeProfileStore : IHandBrakeProfileStore
{
    internal const string BuiltInFolderName = "Compressarr (built-in)";
    internal const string UserFolderName = "Yours";
    internal const string CatalogFolderName = "Compressarr (catalog)";
    private const string BundledResourceName = "Compressarr.Core.Assets.compressarr-presets.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly Lazy<(List<HandBrakeProfile> Profiles, JsonObject Header)> _bundled = new(LoadBundled);

    private (DateTime WriteUtc, long Length) _userStamp;
    private List<HandBrakeProfile>? _userCache;
    private string? _userError;

    private (DateTime WriteUtc, long Length, DateTime RecordUtc, long RecordLength) _catalogStamp;
    private List<HandBrakeProfile>? _catalogCache;
    private List<string> _catalogDamaged = new();
    private string? _catalogError;

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

    public string? CatalogFileError
    {
        get
        {
            lock (_gate)
            {
                LoadCatalogLocked();
                return _catalogError;
            }
        }
    }

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

    public IReadOnlyList<HandBrakeProfile> GetBuiltIns() => _bundled.Value.Profiles;

    public IReadOnlyList<HandBrakeProfile> GetAll()
    {
        lock (_gate)
        {
            return _bundled.Value.Profiles.Concat(LoadCatalogLocked()).Concat(LoadUserLocked()).ToList();
        }
    }

    public void AddCatalogProfile(JsonObject definition, Compressarr.Core.Catalog.InstalledCatalogProfile record)
    {
        lock (_gate)
        {
            var existing = LoadCatalogLocked();
            var user = LoadUserLocked();
            var name = definition["PresetName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A profile needs a name.");
            if (_bundled.Value.Profiles.Concat(existing).Concat(user).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A profile named '{name}' already exists.");
            }

            // Everything already in the file stays - including a profile that reads as damaged, which is the user's to delete - so the file is
            // rebuilt from what is in it, not from the valid list. A file that cannot be read is never overwritten.
            var inFile = ReadCatalogLeavesLocked() ?? throw new InvalidOperationException("The catalog profile file could not be read, so nothing was changed. Delete catalog-handbrake.json to start it again.");
            if (inFile.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A catalog profile named '{name}' is already stored but reads as damaged. Delete it first, then add it again.");
            }

            // The record first: a record without its profile is harmless, a profile without its record would read as damaged.
            Compressarr.Core.Catalog.CatalogRecordFile.Add(record with { Name = name, Engine = "handbrake", DefinitionSha = Compressarr.Core.Catalog.CatalogHash.OfDefinition(definition) });
            var all = inFile.Select(l => (JsonNode)l.Definition.DeepClone()).Append((JsonNode)definition.DeepClone()).ToList();
            WriteCatalogFileLocked(all);
            EnsureActiveFileLocked();
        }
    }

    public void RemoveCatalogProfile(string name)
    {
        lock (_gate)
        {
            var existing = LoadCatalogLocked();
            var index = existing.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var damaged = _catalogDamaged.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && !damaged) throw new InvalidOperationException($"There is no catalog profile named '{name}'.");

            // A damaged profile is not in the list above, but its entry is still in the file: keep every other one as it is.
            var keep = (ReadCatalogLeavesLocked() ?? throw new InvalidOperationException("The catalog profile file could not be read, so nothing was changed. Delete catalog-handbrake.json to start it again.")).Where(l => !string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Select(l => (JsonNode)l.Definition.DeepClone()).ToList();
            WriteCatalogFileLocked(keep);
            Compressarr.Core.Catalog.CatalogRecordFile.Remove("handbrake", name);
            EnsureActiveFileLocked();
        }
    }

    public HandBrakeProfile? Find(string name) =>
        GetAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddUserProfiles(IEnumerable<JsonObject> definitions)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            if (_userError is not null)
            {
                throw new InvalidOperationException($"Your profile file could not be read, so nothing was changed: {_userError}");
            }

            var taken = new HashSet<string>(_bundled.Value.Profiles.Concat(LoadCatalogLocked()).Concat(existing).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var added = new List<JsonObject>();
            foreach (var definition in definitions)
            {
                var name = definition["PresetName"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("A profile needs a name.");
                }
                if (!taken.Add(name))
                {
                    throw new InvalidOperationException($"A profile named '{name}' already exists.");
                }
                added.Add((JsonObject)definition.DeepClone());
            }

            if (added.Count == 0) return;

            var all = existing.Select(p => (JsonNode)p.Definition.DeepClone()).Concat(added).ToList();
            WriteUserFileLocked(all);
            EnsureActiveFileLocked();
        }
    }

    public void ReplaceUserProfile(string currentName, JsonObject definition)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            if (_userError is not null)
            {
                throw new InvalidOperationException($"Your profile file could not be read, so nothing was changed: {_userError}");
            }

            var index = existing.FindIndex(p => string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw _bundled.Value.Profiles.Any(p => string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase))
                    ? new InvalidOperationException($"'{currentName}' is a built-in profile and can't be changed. Duplicate it to make your own.")
                    : LoadCatalogLocked().Any(p => string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase))
                        ? new InvalidOperationException($"'{currentName}' is a catalog profile and can't be changed. Duplicate it to make your own.")
                        : new InvalidOperationException($"There is no profile named '{currentName}'.");
            }

            var newName = definition["PresetName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("A profile needs a name.");

            var clash = _bundled.Value.Profiles.Concat(LoadCatalogLocked()).Concat(existing.Where((_, i) => i != index))
                .Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (clash) throw new InvalidOperationException($"A profile named '{newName}' already exists.");

            var all = existing.Select(p => (JsonNode)p.Definition.DeepClone()).ToList();
            all[index] = definition.DeepClone();
            WriteUserFileLocked(all);
            EnsureActiveFileLocked();
        }
    }

    public void RemoveUserProfile(string name)
    {
        lock (_gate)
        {
            var existing = LoadUserLocked();
            if (_userError is not null)
            {
                throw new InvalidOperationException($"Your profile file could not be read, so nothing was changed: {_userError}");
            }

            var index = existing.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw _bundled.Value.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    ? new InvalidOperationException($"'{name}' is a built-in profile and can't be deleted.")
                    : new InvalidOperationException($"There is no profile named '{name}'.");
            }

            var all = existing.Where((_, i) => i != index).Select(p => (JsonNode)p.Definition.DeepClone()).ToList();
            WriteUserFileLocked(all);
            EnsureActiveFileLocked();
        }
    }

    public string EnsureActiveFile()
    {
        lock (_gate)
        {
            return EnsureActiveFileLocked();
        }
    }

    private string EnsureActiveFileLocked()
    {
        var path = AppPaths.GetHandBrakeActivePresetsFilePath();
        var desired = Encoding.UTF8.GetBytes(BuildActiveJson(LoadCatalogLocked(), LoadUserLocked()));

        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(desired))
        {
            return path;
        }

        WriteAtomically(path, desired);
        return path;
    }

    /// <summary>The generated file: a built-in folder, and a "Yours" folder when the user has any
    /// profiles. Same input always yields byte-identical output (insertion-ordered nodes, fixed
    /// formatting), which is what makes the compare-then-write check meaningful.</summary>
    private string BuildActiveJson(List<HandBrakeProfile> catalog, List<HandBrakeProfile> user)
    {
        var folders = new JsonArray { Folder(BuiltInFolderName, _bundled.Value.Profiles) };
        if (catalog.Count > 0) folders.Add(Folder(CatalogFolderName, catalog));
        if (user.Count > 0) folders.Add(Folder(UserFolderName, user));

        var root = new JsonObject { ["PresetList"] = folders };
        foreach (var (key, value) in _bundled.Value.Header) root[key] = value?.DeepClone();
        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject Folder(string name, IEnumerable<HandBrakeProfile> profiles) => new()
    {
        ["ChildrenArray"] = new JsonArray(profiles.Select(p => p.Definition.DeepClone()).ToArray()),
        ["Folder"] = true,
        ["PresetName"] = name,
        ["Type"] = 1
    };

    private List<HandBrakeProfile> LoadUserLocked()
    {
        var path = AppPaths.GetHandBrakeProfilesFilePath();
        if (!File.Exists(path))
        {
            _userStamp = default;
            _userError = null;
            return _userCache = new List<HandBrakeProfile>();
        }

        var info = new FileInfo(path);
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (_userCache is not null && stamp == _userStamp) return _userCache;

        _userStamp = stamp;
        if (!HandBrakePresetFile.TryRead(path, out var root, out var error))
        {
            _userError = error;
            return _userCache = new List<HandBrakeProfile>();
        }

        _userError = null;
        return _userCache = HandBrakePresetFile.GetLeaves(root)
            .Select(l => new HandBrakeProfile(l.Name, l.FileFormat, false, l.Definition))
            .ToList();
    }

    /// <summary>The catalog profiles that are still what was installed. A profile with no record, or whose stored definition no longer
    /// matches its record, is left out and its name reported in DamagedCatalogProfiles.</summary>
    private List<HandBrakeProfile> LoadCatalogLocked()
    {
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        var recordPath = AppPaths.GetCatalogRecordsFilePath();
        var info = File.Exists(path) ? new FileInfo(path) : null;
        var recordInfo = File.Exists(recordPath) ? new FileInfo(recordPath) : null;
        var stamp = (info?.LastWriteTimeUtc ?? default, info?.Length ?? 0, recordInfo?.LastWriteTimeUtc ?? default, recordInfo?.Length ?? 0);
        if (_catalogCache is not null && stamp == _catalogStamp) return _catalogCache;

        _catalogStamp = stamp;
        _catalogDamaged = new List<string>();
        _catalogError = null;
        if (info is null) return _catalogCache = new List<HandBrakeProfile>();

        var records = Compressarr.Core.Catalog.CatalogRecordFile.Load()
            .Where(r => string.Equals(r.Engine, "handbrake", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var profiles = new List<HandBrakeProfile>();
        var leaves = ReadCatalogLeavesLocked();
        if (leaves is null) _catalogError = $"Your HandBrake catalog profile file could not be read, so the profiles installed from a catalog are not shown. Restore it from a backup, or delete it ({path}) and add them again from the catalog.";
        foreach (var leaf in leaves ?? new List<HandBrakePresetFile.Leaf>())
        {
            if (records.TryGetValue(leaf.Name, out var record) && record.DefinitionSha == Compressarr.Core.Catalog.CatalogHash.OfDefinition(leaf.Definition))
            {
                profiles.Add(new HandBrakeProfile(leaf.Name, leaf.FileFormat, false, leaf.Definition, IsCatalog: true));
            }
            else
            {
                _catalogDamaged.Add(leaf.Name);
            }
        }

        return _catalogCache = profiles;
    }

    /// <summary>Every profile in the catalog file (also ones that read as damaged); empty when there is no file, null when there is one that cannot be read.</summary>
    private static List<HandBrakePresetFile.Leaf>? ReadCatalogLeavesLocked()
    {
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        if (!File.Exists(path)) return new List<HandBrakePresetFile.Leaf>();
        return HandBrakePresetFile.TryRead(path, out var root, out _) ? HandBrakePresetFile.GetLeaves(root) : null;
    }

    private void WriteCatalogFileLocked(List<JsonNode> leaves)
    {
        var root = new JsonObject { ["PresetList"] = new JsonArray(leaves.ToArray()) };
        foreach (var (key, value) in _bundled.Value.Header) root[key] = value?.DeepClone();

        WriteAtomically(AppPaths.GetCatalogHandBrakeFilePath(), Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions)));
        _catalogCache = null;
    }

    private void WriteUserFileLocked(List<JsonNode> leaves)
    {
        var root = new JsonObject { ["PresetList"] = new JsonArray(leaves.ToArray()) };
        foreach (var (key, value) in _bundled.Value.Header) root[key] = value?.DeepClone();

        WriteAtomically(AppPaths.GetHandBrakeProfilesFilePath(), Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions)));
        _userCache = null;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);

        // The target can be open for a moment (HandBrake reading it at the start of an encode, a
        // backup zipping it); a short retry rides that out instead of failing a profile save.
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

    private static (List<HandBrakeProfile>, JsonObject) LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{BundledResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();

        var profiles = HandBrakePresetFile.GetLeaves(root)
            .Select(l => new HandBrakeProfile(l.Name, l.FileFormat, true, (JsonObject)l.Definition.DeepClone()))
            .ToList();

        // Everything except the preset list itself (the Version* fields) is carried into every file
        // Compressarr writes, so HandBrake reads them as the same preset-format version.
        var header = new JsonObject();
        foreach (var (key, value) in root)
        {
            if (key != "PresetList") header[key] = value?.DeepClone();
        }

        return (profiles, header);
    }
}
