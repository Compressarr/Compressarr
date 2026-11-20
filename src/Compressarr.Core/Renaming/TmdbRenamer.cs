using System.Collections.Concurrent;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Queue;

namespace Compressarr.Core.Renaming;

/// <summary>What the renamer would do (or did) with one file.</summary>
public enum RenameKind
{
    /// <summary>It gets a new name.</summary>
    Rename,

    /// <summary>It already has the name TMDB gives it (or carries a {tmdb-...} tag, or was matched by hand). Nothing to do, and it counts as matched.</summary>
    AlreadyNamed,

    /// <summary>TMDB could not tell which film or show this is (nothing fits, more than one fits, or the name says too little). Left as it is and shown as Unmatched.</summary>
    Unmatched,

    /// <summary>It was matched, but a file of the new name is already there. Left as it is: nothing is ever overwritten.</summary>
    TargetExists,

    /// <summary>TMDB could not be asked (no network, a rejected key). Left as it is, and not called unmatched: nobody knows yet.</summary>
    LookupFailed
}

/// <summary>One file's line in a rename plan: where it is, what happens, and why. For a rename, the rest says what it was matched to (so the log can keep it).</summary>
public sealed record RenamePlanItem(string FullName, string FileName, RenameKind Kind, string? NewName, string Reason,
    int? TmdbId = null, bool IsTv = false, int? Season = null, IReadOnlyList<int>? Episodes = null);

/// <summary>The built-in TMDB renamer (3.2): an alternative to FileBot for people who do not run Sonarr or Radarr. Run once per lane before the Input folder is scanned, exactly where the FileBot pass runs, and
/// answers in the same shape (<see cref="FileBotRunResult"/>: which files were renamed, and which could not be matched), so the queue treats both alike.</summary>
public interface ITmdbRenamer
{
    /// <summary>Plans and carries out the renames for every video file under <paramref name="inputPath"/>.</summary>
    Task<FileBotRunResult> RunAsync(TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger, CancellationToken cancellationToken);

    /// <summary>The plan only: what would happen to each of the first <paramref name="limit"/> files, with nothing renamed.</summary>
    Task<IReadOnlyList<RenamePlanItem>> PreviewAsync(TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, int limit, CancellationToken cancellationToken);
}

public sealed class TmdbRenamer : ITmdbRenamer
{
    private static readonly TimeSpan FoundLife = TimeSpan.FromHours(6);
    private static readonly TimeSpan NotFoundLife = TimeSpan.FromMinutes(20);

    private readonly IVideoFileScanner _scanner;
    private readonly ITmdbRenameClient _client;
    private readonly IRenameLog? _log;
    private readonly IRenameJournal? _journal;
    private readonly IFileReservations? _reservations;
    private readonly TimeProvider _time;

    private sealed record Cached<T>(T Value, DateTime ExpiresUtc);
    private readonly ConcurrentDictionary<string, Cached<IReadOnlyList<TmdbCandidate>>> _searches = new();
    private readonly ConcurrentDictionary<string, Cached<IReadOnlyDictionary<int, string>>> _seasons = new();

    public TmdbRenamer(IVideoFileScanner scanner, ITmdbRenameClient client, IRenameLog log, IRenameJournal? journal = null, IFileReservations? reservations = null)
        : this(scanner, client, log, TimeProvider.System, journal, reservations) { }

    internal TmdbRenamer(IVideoFileScanner scanner, ITmdbRenameClient client) : this(scanner, client, null, TimeProvider.System) { }

    internal TmdbRenamer(IVideoFileScanner scanner, ITmdbRenameClient client, IRenameLog? log, TimeProvider time, IRenameJournal? journal = null, IFileReservations? reservations = null)
    {
        _scanner = scanner;
        _client = client;
        _log = log;
        _time = time;
        _journal = journal;
        _reservations = reservations;
    }

    public async Task<FileBotRunResult> RunAsync(TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger, CancellationToken cancellationToken)
    {
        var problemKey = $"tmdb-rename-key:{inputPath}";
        var plan = await PlanAsync(settings, apiKey, inputPath, vidTypes, limit: 0, cancellationToken);

        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var undetermined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyRejected = false;
        var unreachable = false;

        foreach (var item in plan.Items)
        {
            switch (item.Kind)
            {
                case RenameKind.Rename:
                    ApplyOne(item, inputPath, vidTypes, logger, renames, unresolved);
                    break;
                case RenameKind.Unmatched:
                    unmatched.Add(item.FullName);
                    logger.Log($"[TMDB] \"{item.FileName}\" left as it is: {item.Reason}");
                    break;
                case RenameKind.TargetExists:
                    logger.Log($"[TMDB] \"{item.FileName}\" not renamed: {item.Reason}");
                    break;
                case RenameKind.LookupFailed:
                    undetermined.Add(item.FullName);
                    keyRejected |= plan.KeyRejected;
                    unreachable |= !plan.KeyRejected;
                    break;
            }
        }

        if (keyRejected) logger.LogProblem(problemKey, "TMDB rejected the API key, so no file was renamed. Check the key under Queue Posters on the Settings page.");
        else logger.ClearProblem(problemKey);
        if (unreachable) logger.Log("[TMDB] TMDB could not be reached, so files not yet looked up were left as they are. They are tried again on the next pass.", LogSeverity.Error);

        return new FileBotRunResult(unmatched, renames, undetermined, unresolved);
    }

    /// <summary>Carries out one planned rename: reserves the file (a Match or the encoder may hold it), looks again at the file and its log row (the plan may be minutes old: TMDB was asked about other files
    /// meanwhile), checks that the video and every file that follows it can take the new name, writes the plan to the journal, then moves all of them or none. The queue and the rename log follow; the journal
    /// record is closed by the caller once the queue has (see RenameReconciler.CloseApplied).</summary>
    private void ApplyOne(RenamePlanItem item, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger, Dictionary<string, string> renames, HashSet<string> unresolved)
    {
        using var lease = _reservations?.TryReserve(item.FullName);
        if (_reservations is not null && lease is null)
        {
            logger.Log($"[TMDB] \"{item.FileName}\" is being worked on elsewhere right now; it is looked at again on the next pass.");
            return;
        }

        if (!File.Exists(item.FullName)) return;                                                                  // it went away (or was renamed by a Match) since the plan was made
        if (_log?.Find(item.FullName) is { IsHandled: true }) return;                                              // matched by hand meanwhile: theirs

        IReadOnlyList<RenameMove> moves;
        try
        {
            moves = RenameApplier.Plan(item.FullName, item.NewName!, vidTypes);
        }
        catch (IOException ex)
        {
            logger.Log($"[TMDB] \"{item.FileName}\" not renamed: {ex.Message}. Nothing was changed.", LogSeverity.Error);
            return;
        }

        long? journalId = null;
        if (_journal is not null)
        {
            journalId = _journal.Begin(RenameIntent.From(inputPath, moves, item.TmdbId, item.IsTv, item.Season, item.Episodes, RenameRecord.AutomaticSource, unmatched: false));
            if (journalId is null)
            {
                logger.Log($"[TMDB] \"{item.FileName}\" not renamed: the rename could not be recorded first. It is tried again on the next pass.", LogSeverity.Error);
                return;
            }
        }

        try
        {
            RenameApplier.Execute(moves);
        }
        catch (RenameFailedException ex)
        {
            if (ex.RolledBack)
            {
                if (journalId is not null) _journal!.End(journalId.Value);
                logger.Log($"[TMDB] Could not rename \"{item.FileName}\" to \"{item.NewName}\": {ex.Message}. Nothing was changed; it is tried again on the next pass.", LogSeverity.Error);
            }
            else
            {
                unresolved.Add(item.FullName);
                unresolved.Add(moves[0].To);
                logger.Log($"[TMDB] Could not rename \"{item.FileName}\" to \"{item.NewName}\": {ex.Message}. \"{Path.GetFileName(ex.Stranded[0].To)}\" could not be put back either; the file is held and the rename is finished or undone on the next pass.", LogSeverity.Error);
            }

            return;
        }

        var newPath = moves[0].To;
        renames[item.FullName] = newPath;
        _log?.Record(item.FullName, newPath, item.TmdbId, item.IsTv, item.Season, item.Episodes, RenameRecord.AutomaticSource);
        logger.Log($"[TMDB] Renamed [{item.FullName}] to [{newPath}]");
    }

    public async Task<IReadOnlyList<RenamePlanItem>> PreviewAsync(TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, int limit, CancellationToken cancellationToken) =>
        (await PlanAsync(settings, apiKey, inputPath, vidTypes, limit, cancellationToken)).Items;

    private sealed record Plan(List<RenamePlanItem> Items, bool KeyRejected);

    private async Task<Plan> PlanAsync(TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, int limit, CancellationToken cancellationToken)
    {
        var files = _scanner.FindVideoFiles(inputPath, vidTypes, minSizeBytes: 0, limit).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new List<RenamePlanItem>(files.Count);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // names handed out in this plan: two files never get the same one
        var keyRejected = false;
        var stopped = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopped)
            {
                items.Add(new RenamePlanItem(file.FullName, file.Name, RenameKind.LookupFailed, null, "TMDB could not be asked"));
                continue;
            }

            try
            {
                items.Add(await PlanOneAsync(file, settings, apiKey, inputPath, vidTypes, targets, cancellationToken));
            }
            catch (TmdbKeyRejectedException)
            {
                stopped = keyRejected = true;      // no point asking again with a key TMDB refuses
                items.Add(new RenamePlanItem(file.FullName, file.Name, RenameKind.LookupFailed, null, "TMDB rejected the API key"));
            }
            catch (TmdbUnavailableException)
            {
                stopped = true;                    // the rest would only fail the same way, one slow timeout at a time
                items.Add(new RenamePlanItem(file.FullName, file.Name, RenameKind.LookupFailed, null, "TMDB could not be reached"));
            }
        }

        return new Plan(items, keyRejected);
    }

    private async Task<RenamePlanItem> PlanOneAsync(FileInfo file, TmdbRenameSettings settings, string apiKey, string inputPath, IReadOnlyList<string> vidTypes, HashSet<string> targets, CancellationToken cancellationToken)
    {
        RenamePlanItem Result(RenameKind kind, string? newName, string reason) => new(file.FullName, file.Name, kind, newName, reason);

        if (ReleaseNameParser.HasTmdbTag(file.Name)) return Result(RenameKind.AlreadyNamed, null, "its name already carries a TMDB tag");

        // A match a person made by hand (or a name they put back) is theirs: the automatic renamer never overrules it.
        if (_log?.Find(file.FullName) is { IsHandled: true }) return Result(RenameKind.AlreadyNamed, null, "it was matched by hand");

        var parsed = ReleaseNameParser.Parse(file.FullName, inputPath);
        if (parsed is null) return Result(RenameKind.Unmatched, null, "the name does not say what it is");

        // What a person chose for this show or film before (Match, "apply to the others") comes first, and needs no lookup of the title.
        var remembered = _log?.Recall(RenameMemoryKey.For(parsed));
        var chosen = remembered is null ? null : new TmdbCandidate(remembered.TmdbId, remembered.IsTv, remembered.Title, null, remembered.Year, Array.Empty<string>(), 0, null, null);

        if (chosen is null)
        {
            // A bare title is never guessed at, so there is nothing to ask TMDB: the Match dialog does its own search when someone chooses to.
            if (!parsed.IsTv && parsed.Year is null) return Result(RenameKind.Unmatched, null, TmdbMatcher.Decide(parsed, Array.Empty<TmdbCandidate>()).Why);

            var results = await SearchAsync(apiKey, parsed.IsTv, parsed.Title, parsed.Year, settings.Language, cancellationToken);
            var decision = TmdbMatcher.Decide(parsed, results);
            if (decision.Outcome != MatchOutcome.Matched) return Result(RenameKind.Unmatched, null, decision.Why);
            chosen = decision.Candidate!;
        }

        string stem;
        if (parsed.IsTv)
        {
            var season = await SeasonAsync(apiKey, chosen.Id, parsed.Season!.Value, settings.Language, cancellationToken);
            var missing = parsed.Episodes.Where(e => !season.ContainsKey(e)).ToList();
            if (missing.Count > 0)
            {
                return Result(RenameKind.Unmatched, null, $"TMDB has no episode {string.Join(", ", missing)} in season {parsed.Season} of {chosen.Display}");
            }

            stem = RenameFormatter.EpisodeStem(chosen.Title, chosen.Year, parsed.Season.Value, parsed.Episodes, parsed.Episodes.Select(e => (string?)season[e]).ToList(), settings.Numbering, parsed.Tags);
        }
        else
        {
            var year = chosen.Year ?? parsed.Year;
            if (year is null) return Result(RenameKind.Unmatched, null, $"TMDB has no release year for {chosen.Title}");

            stem = RenameFormatter.MovieStem(chosen.Title, year.Value, parsed.Tags, settings.AddMovieTmdbId ? chosen.Id : null);
        }

        var newName = stem + file.Extension;
        if (string.Equals(newName, file.Name, StringComparison.OrdinalIgnoreCase)) return Result(RenameKind.AlreadyNamed, null, "it already has that name");

        var target = Path.Combine(file.DirectoryName!, newName);
        if (File.Exists(target) || !targets.Add(target)) return Result(RenameKind.TargetExists, newName, $"\"{newName}\" already exists");

        // The subtitles and other files that follow the video are part of the rename: if one of them cannot take its new name, the whole set stays as it is (and the preview says so).
        try { RenameApplier.Plan(file.FullName, newName, vidTypes); }
        catch (IOException ex) { return Result(RenameKind.TargetExists, newName, $"{ex.Message}. Nothing was changed"); }

        return new RenamePlanItem(file.FullName, file.Name, RenameKind.Rename, newName, "", chosen.Id, parsed.IsTv, parsed.Season, parsed.Episodes);
    }

    private async Task<IReadOnlyList<TmdbCandidate>> SearchAsync(string apiKey, bool tv, string title, int? year, string language, CancellationToken cancellationToken)
    {
        var key = $"{(tv ? "tv" : "movie")}|{language}|{TmdbMatcher.Key(title, dropThe: false)}|{year}";
        var now = _time.GetUtcNow().UtcDateTime;
        if (_searches.TryGetValue(key, out var hit) && hit.ExpiresUtc > now) return hit.Value;

        var results = await _client.SearchAsync(apiKey, tv, title, year, language, cancellationToken);
        _searches[key] = new Cached<IReadOnlyList<TmdbCandidate>>(results, now + (results.Count > 0 ? FoundLife : NotFoundLife));
        return results;
    }

    private async Task<IReadOnlyDictionary<int, string>> SeasonAsync(string apiKey, int showId, int season, string language, CancellationToken cancellationToken)
    {
        var key = $"{showId}|{season}|{language}";
        var now = _time.GetUtcNow().UtcDateTime;
        if (_seasons.TryGetValue(key, out var hit) && hit.ExpiresUtc > now) return hit.Value;

        var episodes = await _client.GetSeasonAsync(apiKey, showId, season, language, cancellationToken);
        _seasons[key] = new Cached<IReadOnlyDictionary<int, string>>(episodes, now + (episodes.Count > 0 ? FoundLife : NotFoundLife));
        return episodes;
    }
}
