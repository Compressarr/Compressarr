using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Renaming;

/// <summary>What a file's own name says it is, offered to the Match dialog as the starting point of its search.</summary>
public sealed record MatchSuggestion(bool IsTv, string Title, int? Year, int? Season, IReadOnlyList<int> Episodes, string? Region);

/// <summary>The Match dialog's first answer: what to search for, what TMDB found, and what else would follow the choice. <see cref="IsFix"/> is true for a file Compressarr renamed (the dialog
/// then also offers to restore the original name); <see cref="OriginalName"/> is what it was called first.</summary>
public sealed record MatchCandidatesResult(bool Success, string Message, MatchSuggestion? Suggestion = null, IReadOnlyList<TmdbCandidate>? Candidates = null,
    int? CurrentTmdbId = null, string? OriginalName = null, bool IsFix = false, IReadOnlyList<string>? OtherFiles = null);

/// <summary>A choice made in the dialog. For a show, <see cref="Season"/> and <see cref="Episodes"/> say which episode(s) the file is (the dialog starts them from the file name).
/// <see cref="ApplyToOthers"/> carries the choice to the other waiting episodes of the same show.</summary>
public sealed record MatchApplyRequest(string LaneId, string FullName, int TmdbId, bool IsTv, int? Season, IReadOnlyList<int>? Episodes, bool ApplyToOthers);

public sealed record MatchApplyResult(bool Success, string Message, string? NewName = null, int OthersRenamed = 0, IReadOnlyList<string>? Problems = null);

/// <summary>The queue's Match / Fix match / Restore (3.2): a person picks the right film or show for a file the renamer could not place (or placed wrongly), and the file is renamed to match.
/// Only waiting files in a lane's Input folder are touched, never the one being encoded, and nothing is ever overwritten. The page sends a TMDB id, never a title: the name comes from TMDB.</summary>
public interface ITmdbMatchService
{
    /// <summary>What to search for and what TMDB found. <paramref name="query"/> null means "start from the file name"; once the dialog has a query of its own (<paramref name="year"/> and
    /// <paramref name="tv"/> with it) the search is exactly that.</summary>
    Task<MatchCandidatesResult> CandidatesAsync(CompressarrConfig config, string laneId, string fullName, string? query, int? year, bool? tv, string? currentFileFullName, CancellationToken cancellationToken);

    Task<MatchApplyResult> ApplyAsync(CompressarrConfig config, MatchApplyRequest request, string? currentFileFullName, CancellationToken cancellationToken);

    /// <summary>Puts a renamed file back to the name it first had and makes it Unmatched again; the automatic renamer leaves it alone from then on.</summary>
    MatchApplyResult Restore(CompressarrConfig config, string laneId, string fullName, string? currentFileFullName);

    /// <summary>Clears the Unmatched badge without renaming: this file is not something TMDB knows.</summary>
    MatchApplyResult LeaveAsIs(CompressarrConfig config, string laneId, string fullName, string? currentFileFullName);
}

public sealed class TmdbMatchService : ITmdbMatchService
{
    private const int MaxCandidates = 12;

    private readonly ITmdbRenameClient _client;
    private readonly IRenameLog _log;
    private readonly IQueueService _queue;
    private readonly IResumeStateStore _resumeStore;
    private readonly IPathExpander _pathExpander;
    private readonly IRenameJournal? _journal;
    private readonly IFileReservations? _reservations;

    public TmdbMatchService(ITmdbRenameClient client, IRenameLog log, IQueueService queue, IResumeStateStore resumeStore, IPathExpander pathExpander,
        IRenameJournal? journal = null, IFileReservations? reservations = null)
    {
        _client = client;
        _log = log;
        _queue = queue;
        _resumeStore = resumeStore;
        _pathExpander = pathExpander;
        _journal = journal;
        _reservations = reservations;
    }

    /// <summary>The file a choice is about, as it was when it was checked: <see cref="Length"/> and <see cref="ModifiedUtc"/> are what it looked like then, so a file that was replaced or is still being
    /// written while TMDB was being asked about is not renamed on the strength of an old look.</summary>
    private sealed record OtherFile(string Path, ParsedName Parsed, long Length, DateTime ModifiedUtc);

    private sealed record Target(LaneConfig Lane, string Input, IReadOnlyList<string> VidTypes, FileInfo File, long Length, DateTime ModifiedUtc);

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The file must be a waiting video in an ordinary lane's Input folder, and not the one being encoded.</summary>
    private (Target? Target, string? Error) Check(CompressarrConfig config, string laneId, string fullName, string? currentFile)
    {
        var lane = config.Lanes.FirstOrDefault(l => l.Id == laneId);
        if (lane is null) return (null, "That lane no longer exists.");
        if (LaneKinds.IsVideos(lane)) return (null, "A Videos lane's files are never renamed.");
        if (string.IsNullOrWhiteSpace(fullName)) return (null, "No file was named.");

        var input = _pathExpander.Expand(lane.Input);
        var vidTypes = LaneProcessing.ConfigFor(config, lane).Processing.VidTypes;
        var file = new FileInfo(Path.GetFullPath(fullName));
        if (!file.Exists) return (null, "That file is no longer there.");
        if (!PathContainment.IsStrictlyInside(file.FullName, input)) return (null, "That file is not in the lane's Input folder.");
        if (!vidTypes.Any(t => string.Equals("." + t.Trim().TrimStart('.'), file.Extension, StringComparison.OrdinalIgnoreCase))) return (null, "That is not one of the lane's video types.");
        if (Same(currentFile, file.FullName)) return (null, "That file is being encoded right now. Try again when it has finished.");

        var entry = _resumeStore.Load(AppPaths.GetResumeFilePath()).FirstOrDefault(e => e.LaneId == lane.Id && Same(e.FullName, file.FullName));
        if (entry is not null && entry.Status != ResumeStatus.Pending) return (null, "That file is no longer waiting in the queue.");

        return (new Target(lane, input, vidTypes, file, file.Length, file.LastWriteTimeUtc), null);
    }

    /// <summary>Looks at the file once more, now that TMDB has answered and the file is reserved: it must still be a waiting video in the lane's Input folder, in the queue as waiting (or not in it yet), and
    /// the same file (same size and time) that the choice was made about. Anything else is refused, because the queue and the file may have moved on while the answer was on its way.</summary>
    private string? Recheck(CompressarrConfig config, string laneId, string path, long length, DateTime modifiedUtc)
    {
        var (fresh, error) = Check(config, laneId, path, currentFile: null);
        if (fresh is null) return error;
        if (fresh.Length != length || fresh.ModifiedUtc != modifiedUtc) return "That file changed while TMDB was being asked. Try again.";

        return null;
    }

    private static string? KeyOf(CompressarrConfig config) => string.IsNullOrWhiteSpace(config.Posters.TmdbApiKey) ? null : config.Posters.TmdbApiKey.Trim();

    private const string NoKeyMessage = "Enter a TMDB API key under Queue Posters on the Settings page first - Match uses the same one.";

    /// <summary>What the file was called before anything renamed it, placed in the file's own folder (what its name says is what to search for).</summary>
    private string OriginalPathOf(FileInfo file)
    {
        var record = _log.Find(file.FullName);
        return record is null ? file.FullName : Path.Combine(file.DirectoryName!, Path.GetFileName(record.OriginalPath));
    }

    public async Task<MatchCandidatesResult> CandidatesAsync(CompressarrConfig config, string laneId, string fullName, string? query, int? year, bool? tv, string? currentFileFullName, CancellationToken cancellationToken)
    {
        var (target, error) = Check(config, laneId, fullName, currentFileFullName);
        if (target is null) return new MatchCandidatesResult(false, error!);

        var apiKey = KeyOf(config);
        if (apiKey is null) return new MatchCandidatesResult(false, NoKeyMessage);

        var record = _log.Find(target.File.FullName);
        var parsed = ReleaseNameParser.Parse(OriginalPathOf(target.File), target.Input);
        var suggestion = parsed is null ? new MatchSuggestion(false, "", null, null, Array.Empty<int>(), null)
                                        : new MatchSuggestion(parsed.IsTv, parsed.Title, parsed.Year, parsed.Season, parsed.Episodes, parsed.Region);
        var originalName = record is null ? null : Path.GetFileName(record.OriginalPath);
        var isFix = record is not null && record.Source != RenameRecord.RestoredSource;
        var others = parsed is { IsTv: true } ? FindOthers(target, parsed, currentFileFullName).Select(o => Path.GetFileName(o.Path)).ToList() : new List<string>();

        var searchTv = tv ?? suggestion.IsTv;
        var title = query is null ? suggestion.Title : query.Trim();
        var searchYear = query is null ? suggestion.Year : year;
        if (title.Length == 0)
        {
            return new MatchCandidatesResult(true, "Type the title to search for.", suggestion, Array.Empty<TmdbCandidate>(), record?.TmdbId, originalName, isFix, others);
        }

        try
        {
            var found = await _client.SearchAsync(apiKey, searchTv, title, searchYear, config.TmdbRename.Language, cancellationToken);
            return new MatchCandidatesResult(true, found.Count == 0 ? "TMDB has nothing by that name." : "", suggestion, found.Take(MaxCandidates).ToList(), record?.TmdbId, originalName, isFix, others);
        }
        catch (TmdbKeyRejectedException)
        {
            return new MatchCandidatesResult(false, "TMDB rejected the API key. Check it under Queue Posters on the Settings page.");
        }
        catch (TmdbUnavailableException)
        {
            return new MatchCandidatesResult(false, "TMDB could not be reached. Try again in a moment.");
        }
    }

    public async Task<MatchApplyResult> ApplyAsync(CompressarrConfig config, MatchApplyRequest request, string? currentFileFullName, CancellationToken cancellationToken)
    {
        var (target, error) = Check(config, request.LaneId, request.FullName, currentFileFullName);
        if (target is null) return new MatchApplyResult(false, error!);

        var apiKey = KeyOf(config);
        if (apiKey is null) return new MatchApplyResult(false, NoKeyMessage);

        try
        {
            var details = await _client.GetDetailsAsync(apiKey, request.IsTv, request.TmdbId, config.TmdbRename.Language, cancellationToken);
            if (details is null) return new MatchApplyResult(false, "TMDB has no such title.");

            var parsedOriginal = ReleaseNameParser.Parse(OriginalPathOf(target.File), target.Input);
            var tags = ReleaseNameParser.Parse(target.File.FullName, target.Input)?.Tags ?? "";

            var season = request.IsTv ? request.Season ?? parsedOriginal?.Season : null;
            var episodes = request.IsTv ? (request.Episodes is { Count: > 0 } ? request.Episodes.Distinct().OrderBy(e => e).ToList() : parsedOriginal?.Episodes.ToList()) : null;
            if (request.IsTv && (season is null || episodes is null || episodes.Count == 0)) return new MatchApplyResult(false, "Say which season and episode this file is.");

            var named = await NameAsync(config, apiKey, details, request.IsTv, season, episodes, tags, parsedOriginal?.Year, target.File.Extension, cancellationToken);
            if (named.Error is not null) return new MatchApplyResult(false, named.Error);

            // Who else follows is worked out first: once this file is renamed, what it was matched to before is no longer on record under its old name.
            var others = request.ApplyToOthers && request.IsTv && parsedOriginal is not null ? FindOthers(target, parsedOriginal, currentFileFullName) : new List<OtherFile>();

            var problems = new List<string>();
            var finished = Finish(config, target, target.File.FullName, target.Length, target.ModifiedUtc, named.Name!, details, season, episodes, problems, out var failure);
            if (failure is not null) return new MatchApplyResult(false, failure);

            // The choice is remembered, so the rest of this show (and its next episodes) follow it instead of being guessed again.
            if (parsedOriginal is not null) _log.Remember(RenameMemoryKey.For(parsedOriginal), new RememberedMatch(details.Id, request.IsTv, details.Title, details.Year));

            var renamedOthers = 0;
            if (others.Count > 0)
            {
                var seasons = new Dictionary<int, IReadOnlyDictionary<int, string>>();
                foreach (var other in others)
                {
                    var otherTarget = new Target(target.Lane, target.Input, target.VidTypes, new FileInfo(other.Path), other.Length, other.ModifiedUtc);
                    var otherNamed = await NameAsync(config, apiKey, details, true, other.Parsed.Season, other.Parsed.Episodes.ToList(), other.Parsed.Tags, null, otherTarget.File.Extension, cancellationToken, seasons);
                    if (otherNamed.Error is not null) { problems.Add($"{otherTarget.File.Name}: {otherNamed.Error}"); continue; }

                    // Each file is looked at again on its own, under its own reservation: the list was made before TMDB was asked, and a file on it may have started encoding, finished, or been renamed since.
                    Finish(config, otherTarget, other.Path, other.Length, other.ModifiedUtc, otherNamed.Name!, details, other.Parsed.Season, other.Parsed.Episodes.ToList(), problems, out var otherFailure);
                    if (otherFailure is not null) problems.Add($"{otherTarget.File.Name}: {otherFailure}");
                    else renamedOthers++;
                }
            }

            var message = Path.GetFileName(finished) == target.File.Name ? "It already had that name; it is marked as matched." : $"Renamed to {Path.GetFileName(finished)}.";
            if (renamedOthers > 0) message += $" {renamedOthers} other file{(renamedOthers == 1 ? "" : "s")} renamed the same way.";
            return new MatchApplyResult(true, message, Path.GetFileName(finished), renamedOthers, problems);
        }
        catch (TmdbKeyRejectedException)
        {
            return new MatchApplyResult(false, "TMDB rejected the API key. Check it under Queue Posters on the Settings page.");
        }
        catch (TmdbUnavailableException)
        {
            return new MatchApplyResult(false, "TMDB could not be reached. Try again in a moment.");
        }
    }

    /// <summary>The new file name for one file, or why it cannot be named (an episode TMDB does not have, a film with no year).</summary>
    private async Task<(string? Name, string? Error)> NameAsync(CompressarrConfig config, string apiKey, TmdbCandidate details, bool isTv, int? season, List<int>? episodes, string tags, int? fallbackYear,
        string extension, CancellationToken cancellationToken, Dictionary<int, IReadOnlyDictionary<int, string>>? seasonCache = null)
    {
        if (!isTv)
        {
            var year = details.Year ?? fallbackYear;
            if (year is null) return (null, $"TMDB has no release year for {details.Title}.");

            return (RenameFormatter.MovieStem(details.Title, year.Value, tags, config.TmdbRename.AddMovieTmdbId ? details.Id : null) + extension, null);
        }

        IReadOnlyDictionary<int, string> names;
        if (seasonCache is not null && seasonCache.TryGetValue(season!.Value, out var cached)) names = cached;
        else
        {
            names = await _client.GetSeasonAsync(apiKey, details.Id, season!.Value, config.TmdbRename.Language, cancellationToken);
            if (seasonCache is not null) seasonCache[season.Value] = names;
        }

        var missing = episodes!.Where(e => !names.ContainsKey(e)).ToList();
        if (missing.Count > 0) return (null, $"TMDB has no episode {string.Join(", ", missing)} in season {season} of {details.Display}.");

        return (RenameFormatter.EpisodeStem(details.Title, details.Year, season.Value, episodes!, episodes!.Select(e => (string?)names[e]).ToList(), config.TmdbRename.Numbering, tags) + extension, null);
    }

    /// <summary>Renames one file to <paramref name="newName"/>, records it (as a hand-made match) and carries its queue entry over. <paramref name="failure"/> says why nothing was done.
    /// Returns the file's new full path.</summary>
    private string Finish(CompressarrConfig config, Target target, string path, long length, DateTime modifiedUtc, string newName, TmdbCandidate details, int? season, List<int>? episodes, List<string> problems, out string? failure)
    {
        failure = null;
        var current = Path.GetFileName(path);
        if (string.Equals(current, newName, StringComparison.OrdinalIgnoreCase))
        {
            _log.Record(path, path, details.Id, details.IsTv, season, episodes, RenameRecord.ManualSource);
            _queue.SetUnmatched(config, target.Lane.Id, path, unmatched: false);
            return path;
        }

        var renamed = CommitRename(config, target, path, length, modifiedUtc, newName, details.Id, details.IsTv, season, episodes, RenameRecord.ManualSource, unmatched: false, out failure);
        return renamed ?? path;
    }

    /// <summary>The one way a file changes its name here (Match, apply to the others, Restore). The file is reserved (the encoder holds the one it is working on), then looked at again - the answer from
    /// TMDB may have taken a while and the queue moves on meanwhile -, the whole set of files that belong to the video is checked against the new name, the plan is written to the journal, and then the
    /// files are moved all or none. The queue entry follows, and its result is checked: a rename the queue did not take is undone, so a file is never left under a name its queue entry does not know.
    /// Returns the video's new path, or null with <paramref name="failure"/> set.</summary>
    private string? CommitRename(CompressarrConfig config, Target target, string path, long length, DateTime modifiedUtc, string newName, int? tmdbId, bool isTv, int? season, List<int>? episodes,
        string source, bool unmatched, out string? failure)
    {
        failure = null;
        using var lease = _reservations?.TryReserve(path);
        if (_reservations is not null && lease is null)
        {
            failure = "That file is being encoded (or renamed) right now. Try again when it has finished.";
            return null;
        }

        var stale = Recheck(config, target.Lane.Id, path, length, modifiedUtc);
        if (stale is not null)
        {
            failure = stale;
            return null;
        }

        var tracked = _resumeStore.Load(AppPaths.GetResumeFilePath()).Any(e => e.LaneId == target.Lane.Id && Same(e.FullName, path));

        IReadOnlyList<RenameMove> moves;
        try
        {
            moves = RenameApplier.Plan(path, newName, target.VidTypes);
        }
        catch (IOException ex)
        {
            failure = $"It could not be renamed: {ex.Message}. Nothing was changed, and nothing is ever overwritten.";
            return null;
        }

        long? journalId = null;
        if (_journal is not null)
        {
            journalId = _journal.Begin(RenameIntent.From(target.Input, moves, tmdbId, isTv, season, episodes, source, unmatched));
            if (journalId is null)
            {
                failure = "The rename could not be recorded first, so nothing was changed. Try again.";
                return null;
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
                failure = $"It could not be renamed: {ex.Message}. Nothing was changed.";
            }
            else
            {
                failure = $"It could not be renamed: {ex.Message}, and \"{Path.GetFileName(ex.Stranded[0].To)}\" could not be put back. The rename is finished or undone when the next pass starts, and the file waits until then.";
            }

            return null;
        }

        var renamed = moves[0].To;

        // The queue entry follows the file. Its answer is not ignored: a queue that did not take the new name is undone, so the file never sits under a name its entry does not know.
        QueueEditResult committed;
        try
        {
            committed = _queue.ApplyRename(config, target.Lane.Id, path, renamed, unmatched);
        }
        catch (Exception ex)
        {
            return Undone(moves, journalId, $"The queue could not be updated ({ex.Message})", out failure);
        }

        if (committed is not QueueEditResult.Ok && (tracked || committed is QueueEditResult.UnknownLane)) return Undone(moves, journalId, "The queue no longer has that file waiting", out failure);

        _log.Record(path, renamed, tmdbId, isTv, season, episodes, source);
        if (journalId is not null) _journal!.End(journalId.Value);
        return renamed;
    }

    /// <summary>Puts a rename back after the queue refused it. Whatever could not be put back stays in the journal, to be finished on the next pass.</summary>
    private string? Undone(IReadOnlyList<RenameMove> moves, long? journalId, string why, out string? failure)
    {
        var stranded = RenameApplier.Undo(moves);
        if (stranded.Count == 0)
        {
            if (journalId is not null) _journal!.End(journalId.Value);
            failure = $"{why}, so the rename was undone and nothing was changed.";
        }
        else
        {
            failure = $"{why}, and \"{Path.GetFileName(stranded[0].To)}\" could not be put back. The rename is finished or undone when the next pass starts.";
        }

        return null;
    }

    public MatchApplyResult Restore(CompressarrConfig config, string laneId, string fullName, string? currentFileFullName)
    {
        var (target, error) = Check(config, laneId, fullName, currentFileFullName);
        if (target is null) return new MatchApplyResult(false, error!);

        var record = _log.Find(target.File.FullName);
        if (record is null || record.Source == RenameRecord.RestoredSource) return new MatchApplyResult(false, "Compressarr did not rename this file.");

        var originalName = Path.GetFileName(record.OriginalPath);
        if (!Same(Path.GetDirectoryName(record.OriginalPath), target.File.DirectoryName))
        {
            return new MatchApplyResult(false, "It was moved to another folder when it was renamed. Put it back by hand.");
        }

        if (File.Exists(record.OriginalPath)) return new MatchApplyResult(false, $"A file named \"{originalName}\" is already there, and nothing is ever overwritten.");

        var restored = CommitRename(config, target, target.File.FullName, target.Length, target.ModifiedUtc, originalName, tmdbId: null, isTv: false, season: null, episodes: null, RenameRecord.RestoredSource, unmatched: true, out var failure);
        if (restored is null) return new MatchApplyResult(false, failure!);

        // What was remembered about this show goes too: the choice that led here is undone. And the automatic renamer leaves the file alone from now on (the record above says so).
        if (ReleaseNameParser.Parse(restored, target.Input) is { } parsed) _log.ForgetMemory(RenameMemoryKey.For(parsed));
        return new MatchApplyResult(true, $"Put back to {originalName}.", originalName, 0, new List<string>());
    }

    public MatchApplyResult LeaveAsIs(CompressarrConfig config, string laneId, string fullName, string? currentFileFullName)
    {
        var (target, error) = Check(config, laneId, fullName, currentFileFullName);
        if (target is null) return new MatchApplyResult(false, error!);

        if (_queue.SetUnmatched(config, target.Lane.Id, target.File.FullName, unmatched: false) != QueueEditResult.Ok)
            return new MatchApplyResult(false, "That file is no longer waiting in the queue.");

        // Remembered as handled (the name stays what it is), so the next pass does not flag the file Unmatched again and hold it once more. A file that has a record already (put back by hand) is handled as it is.
        if (_log.Find(target.File.FullName) is null) _log.Record(target.File.FullName, target.File.FullName, null, false, null, null, RenameRecord.RestoredSource);
        return new MatchApplyResult(true, "Left as it is.");
    }

    /// <summary>The other waiting episodes of the same show (the same name once read) that a choice could carry to: files left Unmatched or put back by hand, files renamed by Compressarr or FileBot (they may be wrong
    /// along with this one), and files chosen by hand to the very same title as this one (a batch that is being fixed). A file a person matched to a DIFFERENT title is never listed: their choice is not overruled.
    /// Shows only: a film has no "others".</summary>
    private List<OtherFile> FindOthers(Target target, ParsedName thisFile, string? currentFile)
    {
        var key = RenameMemoryKey.For(thisFile);
        var thisMatch = _log.Find(target.File.FullName)?.TmdbId;
        var others = new List<OtherFile>();
        foreach (var entry in _resumeStore.Load(AppPaths.GetResumeFilePath()).Where(e => e.LaneId == target.Lane.Id && e.Status == ResumeStatus.Pending && !e.Removed))
        {
            if (Same(entry.FullName, target.File.FullName) || Same(entry.FullName, currentFile) || !File.Exists(entry.FullName)) continue;
            if (!PathContainment.IsStrictlyInside(entry.FullName, target.Input)) continue;

            var record = _log.Find(entry.FullName);
            var wanted = record switch
            {
                null => entry.FileBotUnmatched,                                              // left Unmatched
                { Source: RenameRecord.RestoredSource } => true,                              // put back by hand: the show's choice may cover it now
                { Source: RenameRecord.ManualSource } => thisMatch is not null && record.TmdbId == thisMatch,   // only a file chosen with this very match
                _ => true                                                                    // the automatic renamer's or FileBot's: wrong along with this one, perhaps
            };
            if (!wanted) continue;

            var original = record is null ? entry.FullName : Path.Combine(Path.GetDirectoryName(entry.FullName)!, Path.GetFileName(record.OriginalPath));
            var parsed = ReleaseNameParser.Parse(original, target.Input);
            if (parsed is { IsTv: true, Season: not null } && RenameMemoryKey.For(parsed) == key)
            {
                var info = new FileInfo(entry.FullName);
                others.Add(new OtherFile(entry.FullName, parsed, info.Length, info.LastWriteTimeUtc));
            }
        }

        return others;
    }
}
