using System.Diagnostics;

namespace Compressarr.Core.Conversion;

/// <summary>
/// "Passthrough (no re-encode)": a choice in a lane's TV and Movie preset dropdowns (and a queued file's own preset) that gives the file every
/// other benefit of Compressarr - Output folder, routing into the show/movie library, renaming, the source delete/recycle/keep setting,
/// companion files, Sonarr/Radarr, the title-tag clearing, reports - without re-encoding it. It is not a real profile (it never appears on the
/// Profiles page); this name is what is stored on a lane or queue entry, and the conversion step recognises it and copies the file instead.
/// </summary>
public static class PassthroughPreset
{
    public const string Name = "Passthrough (no re-encode)";

    public static bool Is(string? presetName) =>
        !string.IsNullOrWhiteSpace(presetName) && string.Equals(presetName.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when every preset a lane has set is Passthrough (and it has at least one), so the lane never needs an encoder.</summary>
    public static bool OnlyPassthrough(string? tvPreset, string? moviePreset) =>
        (Is(tvPreset) || Is(moviePreset))
        && (string.IsNullOrWhiteSpace(tvPreset) || Is(tvPreset))
        && (string.IsNullOrWhiteSpace(moviePreset) || Is(moviePreset));
}

/// <summary>
/// The "encoder" behind Passthrough: it copies the source to the Output step's temp file, byte for byte, and then everything after an encode runs
/// as usual. It reports progress as a percentage of bytes copied, checks the copy is the same size, keeps the source's modified time on the copy,
/// and cleans up its partial file if it fails or is aborted. A full disk is written to the detail log in the words the conversion step already
/// recognises, so the run stops monitoring for it exactly as it does for an encode.
/// </summary>
public sealed class PassthroughRunner : IEncoderRunner
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    public async Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        var source = request.SourcePath;
        var destination = request.OutputPath;
        var created = false;   // only a file this runner created is ever cleaned up

        try
        {
            var total = new FileInfo(source).Length;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var buffer = new byte[BufferSize];
            long copied = 0;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var lastPercent = -1.0;

            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                created = true;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;

                    if (onProgress is not null && total > 0 && clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        var percent = Math.Min(99.9, copied * 100.0 / total);
                        if (percent - lastPercent >= 0.1)
                        {
                            lastPercent = percent;
                            onProgress(new EncodeProgress(percent, null, Eta(copied, total, clock.Elapsed)));
                        }
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            if (new FileInfo(destination).Length != total)
            {
                throw new IOException($"The copy is {new FileInfo(destination).Length:N0} bytes but the source is {total:N0} - the copy was discarded.");
            }

            // The copy keeps the original's modified time, so it looks like the same file to a media server.
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            onProgress?.Invoke(new EncodeProgress(100, null, null));
            return new EncodeResult(true, request.DetailLogFile);
        }
        catch (OperationCanceledException)
        {
            if (created) TryDelete(destination);
            return new EncodeResult(false, request.DetailLogFile, Cancelled: true);
        }
        catch (Exception ex)
        {
            if (created) TryDelete(destination);
            var message = $"Passthrough copy failed: {ex.Message}";
            WriteDetailLog(request.DetailLogFile, message);
            return new EncodeResult(false, request.DetailLogFile, Failure: EncodeFailureKind.None, FailureMessage: message);
        }
    }

    // Same shape HandBrake's own ETA text has ("00h21m40s"), so the Monitor page reads the same either way.
    private static string? Eta(long copied, long total, TimeSpan elapsed)
    {
        if (copied <= 0 || elapsed.TotalSeconds < 2) return null;
        var remaining = TimeSpan.FromSeconds((total - copied) / (copied / elapsed.TotalSeconds));
        return $"{(int)remaining.TotalHours:00}h{remaining.Minutes:00}m{remaining.Seconds:00}s";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* the conversion step tries again, and logs it */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    private static void WriteDetailLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, message + Environment.NewLine);
        }
        catch (Exception)
        {
            // The message is also the failure reason; the log file is only a convenience.
        }
    }
}
