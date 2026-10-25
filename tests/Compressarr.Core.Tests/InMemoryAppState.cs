using Compressarr.Core.Config;

namespace Compressarr.Core.Tests;

/// <summary>The app state kept in memory, for tests of the schedulers and the backup service (the real one is in the state database).</summary>
public sealed class InMemoryAppState : IAppState
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateOnly> _digests = new();

    private int _viewedThrough;
    private DateTimeOffset? _backupLastRunUtc;

    public int HistoryViewedThroughRunNumber { get { lock (_lock) return _viewedThrough; } }

    public void SetHistoryViewedThroughRunNumber(int runNumber) { lock (_lock) _viewedThrough = runNumber; }

    public DateTimeOffset? BackupLastRunUtc { get { lock (_lock) return _backupLastRunUtc; } }

    public void SetBackupLastRunUtc(DateTimeOffset value) { lock (_lock) _backupLastRunUtc = value; }

    public DateOnly? GetDigestSent(string target, DigestKind kind)
    {
        lock (_lock) return _digests.TryGetValue(Key(target, kind), out var day) ? day : null;
    }

    public void SetDigestSent(string target, DigestKind kind, DateOnly day) { lock (_lock) _digests[Key(target, kind)] = day; }

    public bool AdoptLegacyValues() => false;

    private static string Key(string target, DigestKind kind) => target + "|" + kind;
}
