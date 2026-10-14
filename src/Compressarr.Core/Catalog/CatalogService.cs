using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;
using Compressarr.Core.Updates;

namespace Compressarr.Core.Catalog;

/// <summary>One published profile version, as the dialog shows it. Status is "new", "installed", "damaged" (installed, but the stored copy no
/// longer matches), "nameUsed" (another profile already has this name) or "needsApp" (needs a newer Compressarr).</summary>
public sealed record CatalogItemView(
    string Family, int Version, string Name, string Engine, string Description, string Changelog, string Author, string SourceUrl,
    string Published, string? Deprecated, IReadOnlyList<string> Requires, string Status, bool NewerThanInstalled, string MinAppVersion,
    string SuggestedName);

public sealed record CatalogFamilyView(string Family, string Engine, IReadOnlyList<CatalogItemView> Versions);

public sealed record CatalogView(
    string SourceName, string SourceUrl, string Kind, string? CatalogName, string? Maintainer, string Release, string? Updated,
    IReadOnlyList<CatalogFamilyView> Families, int NotInstalled);

/// <summary>What the user ticked: a family and version, and optionally a new name to import it under as a profile of their own (when the catalog
/// name is already taken).</summary>
/// <summary>Engine is optional: a family can exist for both engines at one version (same name, like the built-ins), and then it says which.</summary>
public sealed record CatalogInstallRequest(string Family, int Version, string? ImportAs, string? Engine = null);

/// <summary>Status is "installed" (added as a catalog profile), "importedAsUser" (added under the user's own name) or "failed".</summary>
public sealed record CatalogInstallResult(string Family, int Version, string Status, string Name, string? Message);

public interface ICatalogService
{
    CatalogSource Source { get; }

    Task<CatalogView> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogInstallResult>> InstallAsync(IReadOnlyList<CatalogInstallRequest> requests, CancellationToken cancellationToken);
}

/// <summary>
/// Browses a catalog and installs from it. Everything a catalog serves is untrusted input: each file is checked against the checksum the
/// catalog lists, parsed strictly, must carry exactly the name it is published under, and is run through the same validators as any other profile
/// before it is stored. Nothing is installed without the user choosing it, and an installed profile is never changed afterwards.
/// </summary>
public sealed class CatalogService : ICatalogService
{
    private static readonly JsonSerializerOptions FFmpegJson = new(JsonSerializerDefaults.Web);

    private readonly ICatalogClient _client;
    private readonly IHandBrakeProfileStore _handBrake;
    private readonly IFFmpegProfileStore _ffmpeg;

    public CatalogService(ICatalogClient client, IHandBrakeProfileStore handBrake, IFFmpegProfileStore ffmpeg)
    {
        _client = client;
        _handBrake = handBrake;
        _ffmpeg = ffmpeg;
    }

    public CatalogSource Source => _client.Source;

    public async Task<CatalogView> ListAsync(CancellationToken cancellationToken)
    {
        var listing = await _client.LoadAsync(cancellationToken);
        var records = CatalogRecordFile.Load();
        var damagedHb = _handBrake.DamagedCatalogProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var damagedFf = _ffmpeg.DamagedCatalogProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hbAll = _handBrake.GetAll();
        var ffAll = _ffmpeg.GetAll();
        var appVersion = AppVersion.Installed();

        var valid = listing.Document.Profiles.Where(e => (e.IsHandBrake || e.IsFFmpeg) && !string.IsNullOrWhiteSpace(e.Family) && e.Version > 0).ToList();
        var families = new List<CatalogFamilyView>();
        var notInstalled = 0;
        foreach (var group in valid.GroupBy(e => (e.Family.Trim().ToLowerInvariant(), e.Engine.ToLowerInvariant())).OrderBy(g => g.First().Family, StringComparer.OrdinalIgnoreCase))
        {
            var engine = group.First().Engine.ToLowerInvariant();
            var installedVersions = group
                .Where(e => records.Any(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Name, e.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Version).ToList();

            var items = new List<CatalogItemView>();
            foreach (var entry in group.OrderByDescending(e => e.Version))
            {
                var all = engine == "handbrake" ? hbAll.Select(p => (p.Name, p.IsCatalog)) : ffAll.Select(p => (p.Name, p.IsCatalog));
                var damaged = engine == "handbrake" ? damagedHb : damagedFf;
                var sameName = all.FirstOrDefault(p => string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                var hasRecord = records.Any(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

                string status;
                if (damaged.Contains(entry.Name)) status = "damaged";
                else if (sameName.Name is not null && sameName.IsCatalog && hasRecord) status = "installed";
                else if (sameName.Name is not null) status = "nameUsed";
                else if (!MeetsAppVersion(entry.MinAppVersion, appVersion)) status = "needsApp";
                else status = "new";

                if (status is "new" or "nameUsed" or "needsApp") notInstalled++;
                var newer = status != "installed" && installedVersions.Any(v => v < entry.Version);
                items.Add(new CatalogItemView(
                    entry.Family, entry.Version, entry.Name, engine, entry.Description, entry.Changelog, entry.Author, entry.SourceUrl ?? "",
                    entry.Published, string.IsNullOrWhiteSpace(entry.Deprecated) ? null : entry.Deprecated, entry.Requires, status, newer,
                    entry.MinAppVersion, $"{entry.Name} (copy)"));
            }

            families.Add(new CatalogFamilyView(group.First().Family, engine, items));
        }

        var info = listing.Document.Catalog;
        return new CatalogView(listing.Source.Name, listing.Source.Url, listing.Source.Kind.ToString().ToLowerInvariant(), info?.Name, info?.Maintainer,
            listing.Release, listing.Document.Updated, families, notInstalled);
    }

    public async Task<IReadOnlyList<CatalogInstallResult>> InstallAsync(IReadOnlyList<CatalogInstallRequest> requests, CancellationToken cancellationToken)
    {
        var listing = await _client.LoadAsync(cancellationToken);
        var results = new List<CatalogInstallResult>();
        foreach (var request in requests)
        {
            var entry = listing.Document.Profiles.FirstOrDefault(e => string.Equals(e.Family, request.Family, StringComparison.OrdinalIgnoreCase) && e.Version == request.Version
                && (e.IsHandBrake || e.IsFFmpeg)
                && (string.IsNullOrWhiteSpace(request.Engine) || string.Equals(e.Engine, request.Engine, StringComparison.OrdinalIgnoreCase)));
            if (entry is null)
            {
                results.Add(new CatalogInstallResult(request.Family, request.Version, "failed", $"{request.Family} v{request.Version}", "That profile is not in the catalog."));
                continue;
            }

            try
            {
                results.Add(await InstallOneAsync(listing, entry, request.ImportAs, cancellationToken));
            }
            catch (CatalogException ex)
            {
                results.Add(new CatalogInstallResult(entry.Family, entry.Version, "failed", entry.Name, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(new CatalogInstallResult(entry.Family, entry.Version, "failed", entry.Name, ex.Message));
            }
        }

        return results;
    }

    private async Task<CatalogInstallResult> InstallOneAsync(CatalogListing listing, CatalogEntry entry, string? importAs, CancellationToken cancellationToken)
    {
        if (!MeetsAppVersion(entry.MinAppVersion, AppVersion.Installed()))
        {
            throw new CatalogException($"{entry.Name} needs Compressarr {entry.MinAppVersion} or newer.");
        }

        var bytes = await _client.DownloadAsync(listing, entry, cancellationToken);
        if (!string.Equals(CatalogHash.OfBytes(bytes), entry.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogException($"{entry.Name} does not match the checksum the catalog lists for it, so it was thrown away and nothing was installed.");
        }

        var asUser = !string.IsNullOrWhiteSpace(importAs);
        var newName = asUser ? importAs!.Trim() : entry.Name;

        if (entry.IsHandBrake)
        {
            var definition = ParseHandBrake(bytes, entry);
            if (asUser)
            {
                definition["PresetName"] = newName;
                definition["PresetDescription"] = AppendOrigin(definition["PresetDescription"]?.GetValue<string>(), entry, listing);
                _handBrake.AddUserProfiles(new[] { definition });
                return new CatalogInstallResult(entry.Family, entry.Version, "importedAsUser", newName, null);
            }

            _handBrake.AddCatalogProfile(definition, RecordFor(entry, bytes, listing));
            return new CatalogInstallResult(entry.Family, entry.Version, "installed", entry.Name, null);
        }

        var profile = ParseFFmpeg(bytes, entry);
        if (asUser)
        {
            profile.Name = newName;
            profile.Description = AppendOrigin(profile.Description, entry, listing);
            _ffmpeg.AddUserProfiles(new[] { profile });
            return new CatalogInstallResult(entry.Family, entry.Version, "importedAsUser", newName, null);
        }

        _ffmpeg.AddCatalogProfile(profile, RecordFor(entry, bytes, listing));
        return new CatalogInstallResult(entry.Family, entry.Version, "installed", entry.Name, null);
    }

    private static InstalledCatalogProfile RecordFor(CatalogEntry entry, byte[] bytes, CatalogListing listing) => new(
        entry.Name, entry.Engine.ToLowerInvariant(), entry.Family, entry.Version, CatalogHash.OfBytes(bytes), "", listing.Source.Name, listing.Source.Url,
        listing.Release, entry.Author, DateTime.UtcNow);

    private static string AppendOrigin(string? description, CatalogEntry entry, CatalogListing listing)
    {
        var origin = $"Copied from the {listing.Source.Name} catalog: {entry.Name}.";
        return string.IsNullOrWhiteSpace(description) ? origin : description.TrimEnd() + " " + origin;
    }

    /// <summary>A catalog's HandBrake file must be a presets file holding exactly one profile, published under the name the catalog lists.</summary>
    private static JsonObject ParseHandBrake(byte[] bytes, CatalogEntry entry)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
        }
        catch (JsonException)
        {
            throw new CatalogException($"{entry.Name} is not a valid HandBrake presets file.");
        }

        var leaves = HandBrakePresetFile.GetLeaves(root);
        if (leaves.Count != 1) throw new CatalogException($"{entry.Name} should hold exactly one HandBrake profile, but holds {leaves.Count}.");

        var definition = (JsonObject)leaves[0].Definition.DeepClone();
        if (!string.Equals(leaves[0].Name, entry.Name, StringComparison.Ordinal))
        {
            throw new CatalogException($"{entry.Name} contains a profile named '{leaves[0].Name}', not the name the catalog lists.");
        }
        if (string.IsNullOrWhiteSpace(definition["VideoEncoder"]?.ToString()))
        {
            throw new CatalogException($"{entry.Name} does not name a video encoder, so it is not a usable HandBrake profile.");
        }

        return definition;
    }

    /// <summary>A catalog's ffmpeg file is one Compressarr ffmpeg profile, published under the name the catalog lists, that passes the same checks as the editor.</summary>
    private static FFmpegProfile ParseFFmpeg(byte[] bytes, CatalogEntry entry)
    {
        FFmpegProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<FFmpegProfile>(Encoding.UTF8.GetString(bytes), FFmpegJson);
        }
        catch (JsonException)
        {
            throw new CatalogException($"{entry.Name} is not a valid ffmpeg profile.");
        }

        if (profile is null) throw new CatalogException($"{entry.Name} is empty.");
        if (!string.Equals(profile.Name, entry.Name, StringComparison.Ordinal))
        {
            throw new CatalogException($"{entry.Name} contains a profile named '{profile.Name}', not the name the catalog lists.");
        }

        var issues = FFmpegProfileValidator.Validate(profile);
        if (issues.Count > 0) throw new CatalogException($"{entry.Name} is not a valid ffmpeg profile: {issues[0].Message}");

        profile.IsBuiltIn = false;
        profile.IsCatalog = false;
        return profile;
    }

    /// <summary>True when this Compressarr is at least the version the profile needs. Only the numbers count: a pre-release of 2.2.0 meets "2.2.0",
    /// so the catalog works on the betas that lead up to it. An unreadable requirement is treated as "needs a newer Compressarr", never as "fine";
    /// an unreadable running version blocks nothing.</summary>
    internal static bool MeetsAppVersion(string? required, AppVersion? current)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;
        if (AppVersion.Parse(required) is not { } needed) return false;
        return current is null || current.WithoutPreRelease().CompareTo(needed.WithoutPreRelease()) >= 0;
    }
}
