using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class BackupEndpoints
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string ExportProfilesKey = "handBrakeUserProfiles";
    private const string ExportFFmpegProfilesKey = "ffmpegUserProfiles";
    private const string ExportCatalogKey = "catalogProfiles";

    private static readonly JsonSerializerOptions FFmpegProfileJson = new(JsonSerializerDefaults.Web);

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        // Exports the fully-merged config (every field populated, not just whatever overrides
        // happen to be in the on-disk file) so the download is a complete, self-contained backup
        // regardless of how partial compressarr.settings.json currently is.
        //
        // Also carries the user's own HandBrake profiles (under "handBrakeUserProfiles") - the
        // lanes in this file name profiles, and an export that couldn't bring them along would
        // restore lanes pointing at nothing. A 2.1.x install importing it just ignores that key.
        app.MapGet("/api/settings/export", (IConfigStore configStore, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var node = JsonSerializer.SerializeToNode(config, ExportOptions)!.AsObject();
            node[ExportProfilesKey] = new JsonArray(profiles.GetAll().Where(p => !p.IsBuiltIn && !p.IsCatalog).Select(p => (JsonNode)p.Definition.DeepClone()).ToArray());
            node[ExportFFmpegProfilesKey] = new JsonArray(ffmpegProfiles.GetAll().Where(p => !p.IsBuiltIn && !p.IsCatalog)
                .Select(p => JsonSerializer.SerializeToNode(p, FFmpegProfileJson)!).ToArray());

            // The profiles added from a catalog, each with its record, so a new machine keeps lanes that use them working with no network.
            var records = Compressarr.Core.Catalog.CatalogRecordFile.Load();
            Compressarr.Core.Catalog.InstalledCatalogProfile? RecordOf(string engine, string name) =>
                records.FirstOrDefault(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var catalogHandBrake = new JsonArray();
            foreach (var p in profiles.GetAll().Where(p => p.IsCatalog))
            {
                if (RecordOf("handbrake", p.Name) is { } record) catalogHandBrake.Add(new JsonObject { ["record"] = JsonSerializer.SerializeToNode(record, FFmpegProfileJson), ["definition"] = p.Definition.DeepClone() });
            }
            var catalogFFmpeg = new JsonArray();
            foreach (var p in ffmpegProfiles.GetAll().Where(p => p.IsCatalog))
            {
                if (RecordOf("ffmpeg", p.Name) is { } record) catalogFFmpeg.Add(new JsonObject { ["record"] = JsonSerializer.SerializeToNode(record, FFmpegProfileJson), ["definition"] = JsonSerializer.SerializeToNode(p, FFmpegProfileJson) });
            }
            node[ExportCatalogKey] = new JsonObject { ["handbrake"] = catalogHandBrake, ["ffmpeg"] = catalogFFmpeg };
            var json = node.ToJsonString(ExportOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Results.File(bytes, "application/json", $"compressarr-config-{DateTime.Now:yyyy-MM-dd}.json");
        });

        app.MapPost("/api/settings/import", async (HttpRequest request, IConfigStore configStore, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles, IHandBrakeProfileMigration migration, Compressarr.Core.Startup.IStartupRegistrationService startupRegistration) =>
        {
            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync();

            // Validate + merge-over-defaults the uploaded file the exact same way
            // JsonConfigStore.Load already does for the real config, by round-tripping it through
            // a throwaway temp file - reuses that logic instead of duplicating it, and means an
            // older/partial export still imports cleanly (missing fields fall back to defaults
            // rather than null or a crash).
            var tempPath = Path.Combine(Path.GetTempPath(), $"compressarr-import-{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(tempPath, raw);

                CompressarrConfig imported;
                try
                {
                    imported = configStore.Load(tempPath);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { message = $"That file isn't a valid Compressarr config: {ex.Message}" });
                }

                configStore.Save(imported, AppPaths.GetConfigFilePath());

                // Profiles the export carried that this install doesn't have yet. Same-named ones
                // are left alone - the built-ins are locked and the user's own are not overwritten.
                if (JsonNode.Parse(raw) is JsonObject root && root[ExportProfilesKey] is JsonArray exported)
                {
                    var missing = exported.OfType<JsonObject>()
                        .Where(d => d["PresetName"]?.GetValue<string>() is { Length: > 0 } name && profiles.Find(name) is null)
                        .DistinctBy(d => d["PresetName"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (missing.Count > 0) profiles.AddUserProfiles(missing);
                }

                if (JsonNode.Parse(raw) is JsonObject root2 && root2[ExportFFmpegProfilesKey] is JsonArray exportedFFmpeg)
                {
                    var missingFFmpeg = exportedFFmpeg
                        .Select(n => { try { return n.Deserialize<Compressarr.Core.FFmpeg.FFmpegProfile>(FFmpegProfileJson); } catch (JsonException) { return null; } })
                        .OfType<Compressarr.Core.FFmpeg.FFmpegProfile>()
                        .Where(p => !string.IsNullOrWhiteSpace(p.Name) && ffmpegProfiles.Find(p.Name) is null)
                        .DistinctBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (missingFFmpeg.Count > 0) ffmpegProfiles.AddUserProfiles(missingFFmpeg);
                }

                // Profiles that came from a catalog: added back (as catalog profiles) when this install doesn't have a profile of that name, and only
                // when the definition still matches the fingerprint its record carries.
                if (JsonNode.Parse(raw) is JsonObject root3 && root3[ExportCatalogKey] is JsonObject exportedCatalog)
                {
                    foreach (var item in (exportedCatalog["handbrake"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        try
                        {
                            var record = item["record"].Deserialize<Compressarr.Core.Catalog.InstalledCatalogProfile>(FFmpegProfileJson);
                            if (item["definition"] is JsonObject definition && record is not null && profiles.Find(record.Name) is null
                                && record.DefinitionSha == Compressarr.Core.Catalog.CatalogHash.OfDefinition(definition))
                            {
                                profiles.AddCatalogProfile((JsonObject)definition.DeepClone(), record);
                            }
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentNullException) { /* one bad entry doesn't stop the rest */ }
                    }

                    foreach (var item in (exportedCatalog["ffmpeg"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    {
                        try
                        {
                            var record = item["record"].Deserialize<Compressarr.Core.Catalog.InstalledCatalogProfile>(FFmpegProfileJson);
                            var profile = item["definition"].Deserialize<Compressarr.Core.FFmpeg.FFmpegProfile>(FFmpegProfileJson);
                            if (profile is not null && record is not null && ffmpegProfiles.Find(record.Name) is null
                                && record.DefinitionSha == Compressarr.Core.Catalog.CatalogHash.OfFFmpegProfile(profile))
                            {
                                ffmpegProfiles.AddCatalogProfile(profile, record);
                            }
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentNullException) { }
                    }
                }

                // Lanes that arrived with this config may name presets only the old presets.json has.
                try { migration.RecoverMissing(); } catch (Exception) { /* a convenience - the import itself succeeded */ }

                // The imported "run at login" choice, applied now rather than at the next Settings save.
                try { startupRegistration.Apply(imported.Startup.RunAtLogin); } catch (Exception) { }

                return Results.Ok();
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        });

        // Distinct from /api/settings/export above - this is the automated multi-file backup
        // (settings/lanes, run counter, resume state, history CSV) written to the configured
        // Backups folder, not a one-shot settings-only download. Same trigger both the scheduled
        // loop and this manual button call into.
        app.MapPost("/api/backups/run", async (IBackupService backupService) =>
        {
            var result = await backupService.RunBackupAsync();
            return result.Success
                ? Results.Ok(new { fileName = result.FileName })
                : Results.Json(new { message = result.Error }, statusCode: 500);
        });

        // folder is optional and, when given, wins over the saved config's Backup.FolderPath -
        // lets Settings list (and restore from) a folder the user has typed/browsed to but not
        // saved yet, including on a first launch before any settings exist at all.
        app.MapGet("/api/backups/list", (string? folder, IBackupService backupService) =>
        {
            return Results.Json(backupService.ListBackups(folder));
        });

        // Hands a backup zip to the browser as a download, so a copy can be kept somewhere other than this computer.
        app.MapGet("/api/backups/download", (string fileName, string? folder, IBackupService backupService) =>
        {
            var path = backupService.GetBackupPath(fileName, folder);
            return path is null
                ? Results.NotFound()
                : Results.File(File.OpenRead(path), "application/zip", Path.GetFileName(path));
        });

        app.MapPost("/api/backups/restore", async (RestoreBackupRequest request, IBackupService backupService, IHandBrakeProfileMigration migration, IConfigStore configStore, Compressarr.Core.Startup.IStartupRegistrationService startupRegistration) =>
        {
            var result = await backupService.RestoreBackupAsync(request.FileName, request.Folder);
            // A backup from 2.1.x has lanes naming presets that only live in the old presets.json.
            if (result.Success)
            {
                try { migration.RecoverMissing(); } catch (Exception) { /* a convenience */ }
                try { startupRegistration.Apply(configStore.Load(AppPaths.GetConfigFilePath()).Startup.RunAtLogin); } catch (Exception) { }
            }
            return result.Success
                ? Results.Ok(new { fileName = result.FileName })
                : Results.Json(new { message = result.Error }, statusCode: 500);
        });
    }
}

public sealed record RestoreBackupRequest(string FileName, string? Folder);
