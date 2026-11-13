namespace Compressarr.Core.Logging;

public enum LogSeverity
{
    Info,
    Error
}

/// <summary>
/// Dual-target logging: a per-run summary log file, plus a LineWritten event a UI host (Desktop's
/// live log panel, or a future web monitor) can subscribe to for real-time streaming — replacing
/// v1's console Write-Host target, which has no equivalent in a GUI app. Ported from
/// Write-CompressarrLog/Write-CompressarrFileStart/Write-CompressarrFileComplete.
/// </summary>
public interface IRunLogger
{
    event Action<string, LogSeverity>? LineWritten;

    /// <summary>Creates a fresh summary log file at logFilePath/Compressarr_&lt;timestamp&gt;_Summary.txt
    /// and returns its full path. Resets HasLoggedError to false.</summary>
    string Initialize(string logFilePath, string timestamp);

    /// <summary>True once any Log call this run (since the last Initialize) has used
    /// LogSeverity.Error - lets a caller tell a quiet, uneventful "0 files, nothing wrong" pass
    /// apart from one that hit a real problem despite also processing 0 files (a misconfigured
    /// lane, a missing HandBrakeCLI, etc), so only the former's log file gets discarded rather
    /// than kept forever.</summary>
    bool HasLoggedError { get; }

    void Log(string message, LogSeverity severity = LogSeverity.Info);

    /// <summary>Logs a standing/recurring problem (a missing HandBrakeCLI path, a misconfigured
    /// lane, a broken FileBot path, etc) at Error the first time - and again at Error any time the
    /// message actually changes - but at Info for an unchanged repeat. Real gap found live: a
    /// condition like this gets re-checked and re-logged on literally every poll for as long as it
    /// stays broken, and every one of those was logging at Error, which (combined with "don't keep
    /// an empty/error-free pass's log") meant a kept log file every single poll for the whole
    /// outage - the same file-proliferation problem the empty-pass fix was meant to solve, just
    /// triggered by a standing error instead of an empty pass. This state persists across polls
    /// (NOT reset by Initialize) but not across an app restart - a restart is itself a reasonable
    /// "report it again once" moment, same reasoning the update-check fix uses.
    /// `key` scopes the dedup (e.g. per-lane, per-service) - use a stable identifier distinct from
    /// any other condition that might coincidentally produce the same message text.</summary>
    void LogProblem(string key, string message);

    /// <summary>Clears LogProblem's "already reported" memory for this key - call once a condition
    /// previously flagged via LogProblem is confirmed resolved, so a LATER recurrence of the exact
    /// same problem is treated as new again instead of silently staying "already known" forever.</summary>
    void ClearProblem(string key);

    /// <summary>Same dedup idea as LogProblem, one level up: tells the caller whether laneId's
    /// current set of problem codes differs from what was last reported for it via this same
    /// method, so the HTML report writer can skip writing an unchanged report for a lane whose
    /// configuration problem hasn't changed since the last pass that actually wrote one - LogProblem
    /// alone only dedups the per-pass TEXT LOG line, it has no effect on whether
    /// RunOrchestrator.RunOnceAsync's report-writing gate (`reportModel.HasAnyLaneProblems`) fires,
    /// which is a real gap found live: a persistently misconfigured lane could still force a fresh
    /// HTML report on every single poll forever even though the matching log line was correctly
    /// downgraded to Info. Pass an empty collection for a lane with no current problems - a lane
    /// going from broken to clear (or clear to broken) always counts as changed. Always true the
    /// first time a given laneId is checked. Same persistence lifetime as LogProblem's own memory
    /// (survives across polls, not across an app restart).</summary>
    bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes);

    void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset);

    void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile);

    /// <summary>Starts holding this file's log lines back from the log FILE (3.1): they still reach the live log as they happen, but are only written to the file when
    /// <see cref="EndFileScope"/> says to keep them. Used for a lane whose history is not fully tracked: a file that converts cleanly must leave no file name behind in the log.
    /// The default does nothing (a logger that does not support it simply writes everything, as before).</summary>
    void BeginFileScope() { }

    /// <summary>Ends the scope started by <see cref="BeginFileScope"/>. With <paramref name="discard"/> the held lines are dropped, unless one of them was an error (those are always
    /// kept); otherwise they are written to the log file now.</summary>
    void EndFileScope(bool discard) { }
}
