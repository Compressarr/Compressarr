using System.Globalization;

namespace Compressarr.Core.Logging;

public sealed class FileRunLogger : IRunLogger
{
    private string? _summaryLogFile;
    // Deliberately NOT reset by Initialize - this has to survive across polls (that's the whole
    // point) for as long as the app process itself keeps running. See LogProblem's own doc
    // comment on IRunLogger.
    private readonly Dictionary<string, string> _lastProblemMessages = new();

    // Same "survives across polls, not across a restart" lifetime as _lastProblemMessages above -
    // see HasLaneProblemsChanged's own doc comment on IRunLogger.
    private readonly Dictionary<string, string> _lastReportedLaneProblems = new();

    public event Action<string, LogSeverity>? LineWritten;

    public bool HasLoggedError { get; private set; }

    public string Initialize(string logFilePath, string timestamp)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            throw new InvalidOperationException("Compressarr: Log folder is not configured.");
        }

        Directory.CreateDirectory(logFilePath);

        var logName = $"Compressarr_{timestamp}_Summary.txt";
        _summaryLogFile = Path.Combine(logFilePath, logName);
        if (File.Exists(_summaryLogFile)) File.Delete(_summaryLogFile);
        HasLoggedError = false;

        return _summaryLogFile;
    }

    // Lines held back from the log file while a file of a not-fully-tracked lane is processed (see IRunLogger.BeginFileScope).
    private List<(string Message, LogSeverity Severity)>? _held;
    private readonly object _heldLock = new();

    public void Log(string message, LogSeverity severity = LogSeverity.Info)
    {
        lock (_heldLock)
        {
            if (_held is not null)
            {
                _held.Add((message, severity));
                LineWritten?.Invoke(message, severity);       // the live log always shows it
                return;
            }
        }

        if (severity == LogSeverity.Error) HasLoggedError = true;

        if (_summaryLogFile is not null)
        {
            File.AppendAllText(_summaryLogFile, message + Environment.NewLine);
        }

        LineWritten?.Invoke(message, severity);
    }

    public void BeginFileScope()
    {
        lock (_heldLock) _held = new List<(string, LogSeverity)>();
    }

    public void EndFileScope(bool discard)
    {
        List<(string Message, LogSeverity Severity)>? held;
        lock (_heldLock)
        {
            held = _held;
            _held = null;
        }
        if (held is null) return;

        // An error line is always worth keeping, even for a file that was otherwise meant to leave no trace.
        if (discard && !held.Any(l => l.Severity == LogSeverity.Error)) return;

        foreach (var (message, severity) in held)
        {
            if (severity == LogSeverity.Error) HasLoggedError = true;
            if (_summaryLogFile is not null) File.AppendAllText(_summaryLogFile, message + Environment.NewLine);
        }
    }

    public void LogProblem(string key, string message)
    {
        var changed = !_lastProblemMessages.TryGetValue(key, out var last) || last != message;
        Log(changed ? message : $"{message} (still unresolved, same as last check)", changed ? LogSeverity.Error : LogSeverity.Info);
        _lastProblemMessages[key] = message;
    }

    public void ClearProblem(string key) => _lastProblemMessages.Remove(key);

    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes)
    {
        var current = string.Join(",", problemCodes.OrderBy(c => c, StringComparer.Ordinal));
        var changed = !_lastReportedLaneProblems.TryGetValue(laneId, out var last) || last != current;
        _lastReportedLaneProblems[laneId] = current;
        return changed;
    }

    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset)
    {
        var rule = new string('-', 80);
        Log("");
        Log(rule);
        Log($"[{laneDisplayName}] File {index} of {total}");
        Log("  Name   : " + fileName);
        Log("  Size   : " + sizeGb.ToString("N3", CultureInfo.InvariantCulture) + " GB");
        Log("  Type   : " + contentType);
        Log("  Preset : " + preset);
        Log(rule);
    }

    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile)
    {
        if (success)
        {
            var savings = Math.Round(beginSizeGb - endSizeGb, 3);
            var pct = beginSizeGb > 0 ? Math.Round(100 - (endSizeGb / beginSizeGb) * 100, 1) : 0;
            Log("  Completed : " + fileName);
            Log("  End size  : " + endSizeGb.ToString("N3", CultureInfo.InvariantCulture) + " GB    Saved: " +
                savings.ToString("N3", CultureInfo.InvariantCulture) + $" GB ({pct}%)");
            Log($"  Duration  : {duration.Hours}h {duration.Minutes}m {duration.Seconds}s");
        }
        else
        {
            Log("  FAILED    : " + fileName, LogSeverity.Error);
            Log("  Detail log: " + detailLogFile, LogSeverity.Error);
        }
        Log(new string('-', 80));
        Log("");
    }
}
