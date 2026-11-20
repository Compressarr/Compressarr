namespace Compressarr.Core.Renaming;

/// <summary>One file move of a rename: the video, or one of the files that belong to it.</summary>
public sealed record RenameMove(string From, string To);

/// <summary>A rename that did not happen as a whole. <see cref="Stranded"/> lists moves that were made and could NOT be undone: when it is empty, every name is as it was before the attempt;
/// when it is not, the set is split between two names and the caller must keep the journal record so it is finished (or undone) later - never leave it as it is.</summary>
internal sealed class RenameFailedException : IOException
{
    public RenameFailedException(string message, IReadOnlyList<RenameMove> stranded) : base(message) => Stranded = stranded;

    public IReadOnlyList<RenameMove> Stranded { get; }

    public bool RolledBack => Stranded.Count == 0;
}

/// <summary>Renames a video file in its own folder, together with the files that belong to it. Shared by the automatic renamer and by the queue's Match / Fix match / Restore, so they all treat a video's
/// companions (subtitles, .nfo) the same way.
/// <para>The video and its companions are ONE unit: Compressarr finds a video's subtitles by the video's own name followed by a ".", so a subtitle left under the old name would no longer belong to
/// the renamed video and would be swept up as an unmatched leftover once the video is done (and deleted, with the right settings). So the whole set is planned and checked first - every target free, none
/// repeated - and then moved; if any move fails the ones already made are undone, and the rename is reported as not done.</para></summary>
internal static class RenameApplier
{
    /// <summary>The moves a rename of <paramref name="fullName"/> to <paramref name="newName"/> (same folder) takes: the video first, then each companion. Nothing is moved. Throws
    /// <see cref="IOException"/> when any target name is already taken (nothing is ever overwritten) or two moves would end up with the same name.</summary>
    public static IReadOnlyList<RenameMove> Plan(string fullName, string newName, IReadOnlyList<string> vidTypes)
    {
        var directory = Path.GetDirectoryName(fullName)!;
        var oldStem = Path.GetFileNameWithoutExtension(fullName);
        var newStem = Path.GetFileNameWithoutExtension(newName);
        var videoExtensions = vidTypes.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => "." + t.Trim().TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moves = new List<RenameMove> { new(fullName, Path.Combine(directory, newName)) };
        foreach (var companion in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(companion, fullName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Path.GetFileName(companion).StartsWith(oldStem + ".", StringComparison.OrdinalIgnoreCase)) continue;
            if (videoExtensions.Contains(Path.GetExtension(companion))) continue;

            moves.Add(new RenameMove(companion, Path.Combine(directory, newStem + "." + Path.GetFileName(companion)[(oldStem.Length + 1)..])));
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            // A name is free only when it is verifiably not there: one that cannot be examined is not "free" (File.Exists is false for a missing file and for one that is out of reach).
            if (Compressarr.Core.Conversion.FileOutcomeHelpers.PresenceOf(move.To) == Compressarr.Core.Conversion.FileOutcomeHelpers.FilePresence.Unknown)
            {
                throw new IOException($"\"{Path.GetFileName(move.To)}\" cannot be looked at right now");
            }

            if (File.Exists(move.To) || Directory.Exists(move.To))
            {
                throw new IOException($"\"{Path.GetFileName(move.To)}\" already exists" + (ReferenceEquals(move, moves[0]) ? "" : $", so \"{Path.GetFileName(move.From)}\" cannot follow its video"));
            }
            if (!targets.Add(move.To)) throw new IOException($"Two files would both be named \"{Path.GetFileName(move.To)}\".");
        }

        return moves;
    }

    /// <summary>Makes the moves of a plan, all or none. When one fails the earlier ones are put back (last first); the failure is rethrown as a <see cref="RenameFailedException"/> that says whether
    /// everything is as it was.</summary>
    public static void Execute(IReadOnlyList<RenameMove> moves)
    {
        var done = new List<RenameMove>();
        try
        {
            foreach (var move in moves)
            {
                File.Move(move.From, move.To);      // overwrite is off: if a name was taken since it was planned, this throws and the earlier moves are undone
                done.Add(move);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var stranded = Undo(done);
            var failed = moves[done.Count];
            throw new RenameFailedException($"\"{Path.GetFileName(failed.From)}\" could not be renamed: {ex.Message}", stranded);
        }
    }

    /// <summary>Puts moves back, last first. Returns the ones that could not be put back.</summary>
    public static List<RenameMove> Undo(IReadOnlyList<RenameMove> done)
    {
        var stranded = new List<RenameMove>();
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var move = done[i];
            try
            {
                // The old name must be verifiably free: taken meanwhile, or out of reach (File.Exists is false for both a missing file and one that cannot be examined), means it is never overwritten.
                if (Compressarr.Core.Conversion.FileOutcomeHelpers.PresenceOf(move.From) != Compressarr.Core.Conversion.FileOutcomeHelpers.FilePresence.Absent) { stranded.Add(move); continue; }
                File.Move(move.To, move.From);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stranded.Add(move);
            }
        }

        return stranded;
    }

    /// <summary>Plans and makes a rename in one go (no journal): returns the video's new full path, or throws <see cref="IOException"/> (nothing changed) or <see cref="RenameFailedException"/>.</summary>
    public static string Rename(string fullName, string newName, IReadOnlyList<string> vidTypes)
    {
        var moves = Plan(fullName, newName, vidTypes);
        Execute(moves);
        return moves[0].To;
    }
}
