using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Presets;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>Create, edit, duplicate, delete and import HandBrake profiles (the Profiles page and its
/// editor). Built-ins are locked: they can be read and duplicated, never changed or deleted.</summary>
public static class HandBrakeProfileEndpoints
{
    private const string TemplateProfile = "Compressarr SD-HD";

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new { message, validationIssues = new[] { new ValidationIssueDto(field, message) } });

    private static IResult Invalid(IEnumerable<Compressarr.Core.Validation.ValidationIssue> issues)
    {
        var list = issues.Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();
        return Results.BadRequest(new { message = list[0].Message, validationIssues = list });
    }

    public static void MapHandBrakeProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profiles/handbrake/{name}", (string name, IHandBrakeProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var profile = store.Find(name);
            if (profile is null) return Results.NotFound(new { message = $"There is no profile named '{name}'." });

            var usage = ProfileReferences.Find(configStore.Load(AppPaths.GetConfigFilePath()), resumeStore.Load(AppPaths.GetResumeFilePath()), profile.Name);
            return Results.Json(new ProfileEditDto(HandBrakeProfileForm.From(profile.Definition), profile.IsBuiltIn || profile.IsCatalog, usage.Lanes.ToList(), AppPaths.GetHandBrakeActivePresetsFilePath(), profile.IsCatalog));
        });

        // The profile exactly as stored (what the editor doesn't show is kept as-is).
        app.MapGet("/api/profiles/handbrake/{name}/stored", (string name, IHandBrakeProfileStore store) =>
        {
            var profile = store.Find(name);
            return profile is null
                ? Results.NotFound(new { message = $"There is no profile named '{name}'." })
                : Results.Content(profile.Definition.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json");
        });

        app.MapPost("/api/profiles/handbrake", (SaveProfileRequest request, IHandBrakeProfileStore store) =>
        {
            var issues = request.Form.Validate();
            if (issues.Count > 0) return Invalid(issues);

            var name = request.Form.Name.Trim();
            if (store.Find(name) is not null) return Invalid("name", $"A profile named '{name}' already exists.");

            var baseProfile = (request.BaseName is null ? null : store.Find(request.BaseName)) ?? store.Find(TemplateProfile) ?? store.GetBuiltIns()[0];
            var definition = (System.Text.Json.Nodes.JsonObject)baseProfile.Definition.DeepClone();
            request.Form.ApplyTo(definition);
            definition["Type"] = 1;
            definition["Default"] = false;

            store.AddUserProfiles(new[] { definition });
            return Results.Json(new { name });
        });

        app.MapPut("/api/profiles/handbrake/{name}", (string name, SaveProfileRequest request, IHandBrakeProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var existing = store.Find(name);
            if (existing is null) return Results.NotFound(new { message = $"There is no profile named '{name}'." });
            if (existing.IsBuiltIn) return Results.BadRequest(new { message = $"'{existing.Name}' is a built-in profile and can't be changed. Duplicate it to make your own." });
            if (existing.IsCatalog) return Results.BadRequest(new { message = $"'{existing.Name}' is a catalog profile and can't be changed. Duplicate it to make your own." });

            var issues = request.Form.Validate();
            if (issues.Count > 0) return Invalid(issues);

            var newName = request.Form.Name.Trim();
            var other = store.Find(newName);
            if (other is not null && !ReferenceEquals(other, existing) && !string.Equals(other.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("name", $"A profile named '{newName}' already exists.");
            }

            var definition = (System.Text.Json.Nodes.JsonObject)existing.Definition.DeepClone();
            request.Form.ApplyTo(definition);
            store.ReplaceUserProfile(existing.Name, definition);

            if (!string.Equals(existing.Name, newName, StringComparison.Ordinal))
            {
                ProfileReferences.Repoint(configStore, resumeStore, new Dictionary<string, string> { [existing.Name] = newName });
            }
            return Results.Json(new { name = newName });
        });

        app.MapDelete("/api/profiles/handbrake/{name}", (string name, IHandBrakeProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var existing = store.Find(name);
            if (existing is null) return Results.NotFound(new { message = $"There is no profile named '{name}'." });
            if (existing.IsBuiltIn) return Results.BadRequest(new { message = $"'{existing.Name}' is a built-in profile and can't be deleted." });

            var usage = ProfileReferences.Find(configStore.Load(AppPaths.GetConfigFilePath()), resumeStore.Load(AppPaths.GetResumeFilePath()), existing.Name);
            if (usage.InUse)
            {
                var where = new List<string>();
                if (usage.Lanes.Count > 0) where.Add($"lane{(usage.Lanes.Count == 1 ? "" : "s")} {string.Join(", ", usage.Lanes)}");
                if (usage.QueuedFiles > 0) where.Add($"{usage.QueuedFiles} queued file{(usage.QueuedFiles == 1 ? "" : "s")}");
                return Results.Conflict(new { message = $"'{existing.Name}' is still used by {string.Join(" and ", where)}. Pick another profile there first." });
            }

            if (existing.IsCatalog) store.RemoveCatalogProfile(existing.Name);
            else store.RemoveUserProfile(existing.Name);
            return Results.Ok();
        });

        app.MapPost("/api/profiles/handbrake/{name}/duplicate", (string name, DuplicateProfileRequest? request, IHandBrakeProfileStore store) =>
        {
            var source = store.Find(name);
            if (source is null) return Results.NotFound(new { message = $"There is no profile named '{name}'." });

            var wanted = string.IsNullOrWhiteSpace(request?.Name) ? $"{source.Name} copy" : request!.Name!.Trim();
            var newName = wanted;
            for (var n = 2; store.Find(newName) is not null; n++) newName = $"{wanted} {n}";

            var copy = (System.Text.Json.Nodes.JsonObject)source.Definition.DeepClone();
            copy["PresetName"] = newName;
            copy["Type"] = 1;
            copy["Default"] = false;
            store.AddUserProfiles(new[] { copy });
            return Results.Json(new { name = newName });
        });

        // ---- Import -------------------------------------------------------------------------------

        static ImportListingDto ToDto(ImportListing listing) => new(
            listing.Path,
            Found: listing.Error is null,
            listing.Error,
            listing.Candidates.Select(c => new ImportCandidateDto(c.Name, c.Group, c.Video, c.Container, c.Status switch
            {
                ImportCandidateStatus.AlreadyBuiltIn => "alreadyBuiltIn",
                ImportCandidateStatus.NameUsed => "nameUsed",
                _ => "new"
            })).ToList());

        // The HandBrake app's own presets file, at the path the old setting pointed to (kept for
        // exactly this): read-only, never written.
        app.MapGet("/api/profiles/import/installed", (IConfigStore configStore, IPathExpander pathExpander, IHandBrakeProfileImporter importer) =>
        {
            var path = pathExpander.Expand(configStore.Load(AppPaths.GetConfigFilePath()).HandBrake.PresetsPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Results.Json(new ImportListingDto(path ?? "", false, "No HandBrake presets file was found at that location. Use 'From a presets file' to pick one.", new List<ImportCandidateDto>()));
            }
            return Results.Json(ToDto(importer.Read(path)));
        });

        app.MapPost("/api/profiles/import/read", (ImportReadRequest request, IPathExpander pathExpander, IHandBrakeProfileImporter importer) =>
        {
            var path = pathExpander.Expand(request.Path?.Trim().Trim('"') ?? "");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Results.Json(new ImportListingDto(path, false, "That file doesn't exist.", new List<ImportCandidateDto>()));
            }
            return Results.Json(ToDto(importer.Read(path)));
        });

        app.MapPost("/api/profiles/import", (ImportRequest request, IPathExpander pathExpander, IHandBrakeProfileImporter importer) =>
        {
            var path = pathExpander.Expand(request.Path?.Trim().Trim('"') ?? "");
            if (!File.Exists(path)) return Results.BadRequest(new { message = "That file doesn't exist." });

            var mode = request.OnConflict switch
            {
                "replace" => ImportConflictMode.Replace,
                "skip" => ImportConflictMode.Skip,
                _ => ImportConflictMode.KeepBoth
            };

            try
            {
                var outcome = importer.Import(path, request.Names, mode);
                return Results.Json(new { imported = outcome.Imported, replaced = outcome.Replaced, skipped = outcome.Skipped });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }
}
