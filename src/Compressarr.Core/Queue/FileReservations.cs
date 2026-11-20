using System.Collections.Concurrent;

namespace Compressarr.Core.Queue;

/// <summary>Who may act on a file right now (3.2). The encoder reserves a file for as long as it works on it, and a rename (the automatic renamer, Match, Fix match, Restore) reserves it for the moment
/// it changes the name and the queue, so the two can never overlap: a rename never touches a file being encoded, and the encoder never starts on a file in the middle of being renamed. A reservation is
/// by path (case-insensitive), is not re-entrant, and is released by disposing it.</summary>
public interface IFileReservations
{
    /// <summary>Reserves the file, or returns null when someone holds it already (the caller then leaves the file alone).</summary>
    IDisposable? TryReserve(string fullPath);

    /// <summary>Waits until the file is free and reserves it. <c>Waited</c> says whether it had to wait, i.e. whether something else held the file in the meantime and may have changed it.</summary>
    Task<(IDisposable Lease, bool Waited)> ReserveAsync(string fullPath, CancellationToken cancellationToken);
}

public sealed class FileReservations : IFileReservations
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    private static string KeyOf(string fullPath) => Path.GetFullPath(fullPath).ToUpperInvariant();

    public IDisposable? TryReserve(string fullPath)
    {
        var key = KeyOf(fullPath);
        return _held.TryAdd(key, 0) ? new Lease(_held, key) : null;
    }

    public async Task<(IDisposable Lease, bool Waited)> ReserveAsync(string fullPath, CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReserve(fullPath) is { } lease) return (lease, waited);

            waited = true;
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _held;
        private readonly string _key;
        private int _released;

        public Lease(ConcurrentDictionary<string, byte> held, string key)
        {
            _held = held;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _held.TryRemove(_key, out _);
        }
    }
}
