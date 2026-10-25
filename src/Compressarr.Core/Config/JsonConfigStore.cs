using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Compressarr.Core.Config;

public interface IConfigStore
{
    /// <summary>Loads config from the given path, merged over the built-in defaults so a partial
    /// or missing file still yields a fully-populated config. If the file doesn't exist, returns
    /// the defaults unchanged — the caller decides whether to write them out.</summary>
    CompressarrConfig Load(string path);

    void Save(CompressarrConfig config, string path);

    /// <summary>Atomically loads config, applies mutate, and saves the result — all under one
    /// lock. Multiple HTTP requests (the web UI's endpoints) can call Load+mutate-in-memory+Save
    /// concurrently; without serializing the whole cycle, two concurrent updates race on both the
    /// file write itself (one gets "file in use") and, more subtly, on the read: each starts from
    /// the same pre-mutation snapshot, so the second Save silently clobbers the first's change
    /// (a lost update) even when neither request throws. Every endpoint that changes config
    /// should go through this instead of separate Load()/Save() calls.</summary>
    T Update<T>(string path, Func<CompressarrConfig, T> mutate);
}

public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public CompressarrConfig Load(string path)
    {
        var defaults = DefaultConfigFactory.Create();

        if (!File.Exists(path))
        {
            return defaults;
        }

        JsonNode overrideNode;
        try
        {
            var raw = File.ReadAllText(path);
            overrideNode = JsonNode.Parse(raw)
                ?? throw new InvalidDataException($"Compressarr: config file '{path}' parsed to an empty document.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Compressarr: failed to parse config file '{path}' as JSON. {ex.Message}", ex);
        }

        var defaultsNode = JsonSerializer.SerializeToNode(defaults, SerializerOptions)!;
        var mergedNode = ConfigMerger.Merge(defaultsNode, overrideNode);

        return mergedNode.Deserialize<CompressarrConfig>(SerializerOptions)
            ?? throw new InvalidDataException($"Compressarr: merged config for '{path}' deserialized to null.");
    }

    public void Save(CompressarrConfig config, string path) => Save(config, path, null);

    /// <summary>Saves the settings; <paramref name="adjust"/>, when given, changes the JSON on its way to the file (the secrets are taken out of it, see <see cref="SecretsConfigStore"/>).</summary>
    public void Save(CompressarrConfig config, string path, Action<JsonObject>? adjust)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string json;
        if (adjust is null) json = JsonSerializer.Serialize(config, SerializerOptions);
        else
        {
            var node = JsonSerializer.SerializeToNode(config, SerializerOptions)!.AsObject();
            adjust(node);
            json = node.ToJsonString(SerializerOptions);
        }

        // Write to a temp file and swap it into place rather than writing the target file
        // directly, so a concurrent Load() never observes a partially-written/torn file even if
        // it happens to run outside the Update() lock below.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public T Update<T>(string path, Func<CompressarrConfig, T> mutate)
    {
        _lock.Wait();
        try
        {
            var config = Load(path);
            var result = mutate(config);
            Save(config, path);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
