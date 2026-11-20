using System.Text.Json;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Microsoft.Data.Sqlite;
using FilePresence = Compressarr.Core.Conversion.FileOutcomeHelpers.FilePresence;

namespace Compressarr.Core.Renaming;

/// <summary>Everything a rename of one video is going to do, written down BEFORE the first file is moved: the video, the files that follow it, what it was matched to, and whether its queue row is
/// Unmatched afterwards. It is what lets a stop half way (after the video moved, after a subtitle, before the rename log or the queue caught up) be finished on the next start.</summary>
public sealed record RenameIntent(string InputPath, string OldPath, string NewPath, IReadOnlyList<RenameMove> Companions, int? TmdbId, bool IsTv, int? Season, IReadOnlyList<int> Episodes, string Source, bool Unmatched)
{
    /// <summary>The video's move first, then each companion's.</summary>
    public IReadOnlyList<RenameMove> Moves => new[] { new RenameMove(OldPath, NewPath) }.Concat(Companions).ToList();

    public static RenameIntent From(string inputPath, IReadOnlyList<RenameMove> moves, int? tmdbId, bool isTv, int? season, IReadOnlyList<int>? episodes, string source, bool unmatched) =>
        new(inputPath, moves[0].From, moves[0].To, moves.Skip(1).ToList(), tmdbId, isTv, season, episodes ?? Array.Empty<int>(), source, unmatched);
}

/// <summary>A rename that was begun and not yet finished.</summary>
public sealed record JournalledRename(long Id, RenameIntent Intent);

/// <summary>The open renames (3.2). <see cref="Begin"/> is called before any file is moved; <see cref="End"/> once the files, the rename log and the queue row all agree (or when nothing was moved).
/// Anything still here at the start of a lane's next pass is reconciled by <see cref="RenameReconciler"/>.</summary>
public interface IRenameJournal
{
    /// <summary>Writes the plan down. Returns its id, or null when it could not be written (the database cannot be opened, or an earlier rename of the same file is still open): the caller must then
    /// not move anything - a rename nobody could finish after a stop is worse than a rename that waits.</summary>
    long? Begin(RenameIntent intent);

    void End(long id);

    /// <summary>The open renames of the lane whose Input folder is <paramref name="inputPath"/>.</summary>
    IReadOnlyList<JournalledRename> Pending(string inputPath);
}

public sealed class SqliteRenameJournal : IRenameJournal
{
    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;
    private readonly object _lock = new();

    public SqliteRenameJournal(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath) { }

    internal SqliteRenameJournal(SqliteResumeStateStore store, Func<string> resumeFilePath)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
    }

    private static string KeyOf(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private sealed record Companion(string From, string To);

    public long? Begin(RenameIntent intent)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return null;

                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO rename_journal (old_key, input_path, old_path, new_path, companions, tmdb_id, is_tv, season, episodes, source, unmatched, created_at) " +
                                      "VALUES (@key, @input, @old, @new, @companions, @tmdb, @tv, @season, @episodes, @source, @unmatched, @now); SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@key", KeyOf(intent.OldPath));
                command.Parameters.AddWithValue("@input", intent.InputPath);
                command.Parameters.AddWithValue("@old", intent.OldPath);
                command.Parameters.AddWithValue("@new", intent.NewPath);
                command.Parameters.AddWithValue("@companions", JsonSerializer.Serialize(intent.Companions.Select(c => new Companion(c.From, c.To))));
                command.Parameters.AddWithValue("@tmdb", (object?)intent.TmdbId ?? DBNull.Value);
                command.Parameters.AddWithValue("@tv", intent.IsTv ? 1 : 0);
                command.Parameters.AddWithValue("@season", (object?)intent.Season ?? DBNull.Value);
                command.Parameters.AddWithValue("@episodes", intent.Episodes.Count > 0 ? string.Join(',', intent.Episodes) : DBNull.Value);
                command.Parameters.AddWithValue("@source", intent.Source);
                command.Parameters.AddWithValue("@unmatched", intent.Unmatched ? 1 : 0);
                command.Parameters.AddWithValue("@now", HistoryDatabase.Iso(DateTime.UtcNow));
                return Convert.ToInt64(command.ExecuteScalar());
            }
            catch (SqliteException)
            {
                return null;
            }
        }
    }

    public void End(long id)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return;

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM rename_journal WHERE id = @id;";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Left open: the next pass finds the files and the queue in agreement and closes it then.
            }
        }
    }

    public IReadOnlyList<JournalledRename> Pending(string inputPath)
    {
        lock (_lock)
        {
            var rows = new List<JournalledRename>();
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return rows;

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, input_path, old_path, new_path, companions, tmdb_id, is_tv, season, episodes, source, unmatched FROM rename_journal ORDER BY id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!SamePath(reader.GetString(1), inputPath)) continue;

                    var companions = (JsonSerializer.Deserialize<List<Companion>>(reader.GetString(4)) ?? new List<Companion>()).Select(c => new RenameMove(c.From, c.To)).ToList();
                    var episodes = reader.IsDBNull(8) ? Array.Empty<int>() : reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    rows.Add(new JournalledRename(reader.GetInt64(0), new RenameIntent(reader.GetString(1), reader.GetString(2), reader.GetString(3), companions,
                        reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetInt32(7), episodes, reader.GetString(9), reader.GetInt32(10) != 0)));
                }
            }
            catch (Exception ex) when (ex is SqliteException or JsonException)
            {
                // Nothing can be reconciled this time; it is asked again on the next pass.
            }

            return rows;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

/// <summary>What finishing the open renames of a lane found. <see cref="Completed"/> are renames whose VIDEO carries the new name now (old path to new path): the caller applies them to the queue
/// (the entry follows the video) and closes the finished ones with <see cref="Close"/>. <see cref="Unresolved"/> are paths (old and new) of renames that could not be put right (a file is locked, or something now stands in the way): the files
/// stay as they are, the journal keeps them, and the caller holds those files back from being encoded so nothing is filed - and cleaned up - around a half-renamed set.</summary>
public sealed record RenameReconciliation(IReadOnlyDictionary<string, string> Completed, IReadOnlyDictionary<string, bool> UnmatchedAfter, IReadOnlySet<string> Unresolved, IReadOnlyList<long> Close);

/// <summary>Finishes what a stop left half done (3.2). For each open rename it looks at where the files are, never at what it hopes happened (and a path that cannot be examined is neither there nor gone: it keeps the record and holds the file): the video at its old name and nothing moved means the
/// rename never started (anything that did move is put back); the video at its new name means it is carried through (the companions still under the old name follow it); anything else (the files are
/// gone, or both names exist) is not something to touch, and the record is dropped. A rename it cannot finish stays open and is retried on the next pass.</summary>
public sealed class RenameReconciler
{
    private readonly IRenameJournal _journal;
    private readonly IRenameLog? _log;

    public RenameReconciler(IRenameJournal journal, IRenameLog? log = null)
    {
        _journal = journal;
        _log = log;
    }

    public RenameReconciliation Reconcile(string inputPath, IRunLogger logger)
    {
        var completed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var close = new List<long>();

        foreach (var open in _journal.Pending(inputPath))
        {
            var intent = open.Intent;
            var name = Path.GetFileName(intent.OldPath);

            // Every decision below is made on what can be SAID about a path, not on File.Exists: that answers false for a file that is merely out of reach (access denied, a share that dropped),
            // and a record is only ever closed on what is definitely there or definitely not. Anything un-examinable keeps the record, holds the file and is asked again on the next pass.
            var oldVideo = Presence(intent.OldPath);
            var newVideo = Presence(intent.NewPath);
            if (oldVideo == FilePresence.Unknown || newVideo == FilePresence.Unknown)
            {
                Hold(unresolved, intent);
                logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and \"{Path.GetFileName(oldVideo == FilePresence.Unknown ? intent.OldPath : intent.NewPath)}\" cannot be looked at right now (is the drive or share reachable?); the file is held and it is looked at again on the next pass.", LogSeverity.Error);
                continue;
            }

            if (oldVideo == FilePresence.Present && newVideo == FilePresence.Absent)
            {
                // The video never moved. If a companion did, it goes back so the set is as it was.
                var moved = new List<RenameMove>();
                RenameMove? unreadable = null;
                foreach (var companion in intent.Companions)
                {
                    var to = Presence(companion.To);
                    var from = Presence(companion.From);
                    if (to == FilePresence.Unknown || from == FilePresence.Unknown) unreadable ??= companion;
                    else if (to == FilePresence.Present && from == FilePresence.Absent) moved.Add(companion);
                }

                if (unreadable is not null)
                {
                    Hold(unresolved, intent);
                    logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and \"{Path.GetFileName(unreadable.From)}\" cannot be looked at right now; the file is held and it is looked at again on the next pass.", LogSeverity.Error);
                    continue;
                }

                var stranded = RenameApplier.Undo(moved);
                if (stranded.Count > 0)
                {
                    Hold(unresolved, intent);
                    logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and \"{Path.GetFileName(stranded[0].To)}\" could not be put back; the file is held until it is.", LogSeverity.Error);
                    continue;
                }

                close.Add(open.Id);
                continue;
            }

            if (oldVideo == FilePresence.Absent && newVideo == FilePresence.Present)
            {
                // The video moved. The queue entry and the rename log follow the video whatever becomes of the files behind it, so its place in line, its skip and its profile, and its way back, are kept;
                // the rename log is written when the interrupted rename got that far, otherwise now (never twice, or the first name the file ever had would be overwritten).
                if (_log is not null && _log.Find(intent.NewPath) is null) _log.Record(intent.OldPath, intent.NewPath, intent.TmdbId, intent.IsTv, intent.Season, intent.Episodes, intent.Source);
                completed[intent.OldPath] = intent.NewPath;
                unmatched[intent.OldPath] = intent.Unmatched;

                // The files that follow it move too - or the video is held, and the record stays open, until they can. A companion that cannot be looked at is not "already done": its place is unknown.
                var pending = new List<RenameMove>();
                RenameMove? blocked = null;
                RenameMove? unreadable = null;
                foreach (var companion in intent.Companions)
                {
                    var from = Presence(companion.From);
                    var to = Presence(companion.To);
                    if (from == FilePresence.Unknown || to == FilePresence.Unknown) unreadable ??= companion;
                    else if (from == FilePresence.Present && to == FilePresence.Present) blocked ??= companion;
                    else if (from == FilePresence.Present) pending.Add(companion);
                }

                if (unreadable is not null)
                {
                    Hold(unresolved, intent);
                    logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and \"{Path.GetFileName(unreadable.From)}\" cannot be looked at right now; the file is held and it is looked at again on the next pass.", LogSeverity.Error);
                    continue;
                }

                if (blocked is not null)
                {
                    Hold(unresolved, intent);
                    logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and \"{Path.GetFileName(blocked.From)}\" cannot follow its video because \"{Path.GetFileName(blocked.To)}\" now exists. Rename it by hand; the file is held until then.", LogSeverity.Error);
                    continue;
                }

                try
                {
                    foreach (var companion in pending) File.Move(companion.From, companion.To);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Hold(unresolved, intent);
                    logger.Log($"[TMDB] A rename of \"{name}\" was interrupted and could not be finished: {ex.Message}. The file is held and it is tried again on the next pass.", LogSeverity.Error);
                    continue;
                }

                close.Add(open.Id);
                logger.Log($"[TMDB] Finished an interrupted rename of \"{name}\" to \"{Path.GetFileName(intent.NewPath)}\".");
                continue;
            }

            // Both names are definitely there, or definitely neither: not a state this rename can have made. Nothing is moved; the record is dropped.
            logger.Log($"[TMDB] An interrupted rename of \"{name}\" was dropped: {(oldVideo == FilePresence.Present ? "both names exist now" : "the file is no longer there")}.");
            close.Add(open.Id);
        }

        return new RenameReconciliation(completed, unmatched, unresolved, close);
    }

    private static FilePresence Presence(string path) => FileOutcomeHelpers.PresenceOf(path);

    /// <summary>Closes records the caller has applied.</summary>
    public void Close(IEnumerable<long> ids)
    {
        foreach (var id in ids) _journal.End(id);
    }

    /// <summary>Closes the open records of renames this pass has just made and the queue has taken (old path to new path, as the renamer reported them). A record whose rename the renamer did not report
    /// (it is still open from a stop, or its files are split) stays for <see cref="Reconcile"/>.</summary>
    public void CloseApplied(string inputPath, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) return;

        foreach (var open in _journal.Pending(inputPath))
        {
            if (renames.TryGetValue(open.Intent.OldPath, out var newPath) && string.Equals(newPath, open.Intent.NewPath, StringComparison.OrdinalIgnoreCase)) _journal.End(open.Id);
        }
    }

    private static void Hold(HashSet<string> unresolved, RenameIntent intent)
    {
        unresolved.Add(intent.OldPath);
        unresolved.Add(intent.NewPath);
    }
}
