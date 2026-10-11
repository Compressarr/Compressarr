using System.Diagnostics;
using System.Globalization;
using System.Text;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.FFmpeg;

/// <summary>
/// The ffmpeg implementation of <see cref="IEncoderRunner"/>. One encode is: probe the source with
/// ffprobe; plan what to keep (<see cref="FFmpegPlanner"/>); sample the picture for black bars when the
/// profile asks; build the command (<see cref="FFmpegCommandBuilder"/>); run ffmpeg, reporting progress
/// from "-progress pipe:1"; then verify the result.
///
/// Success needs more than a clean exit: ffmpeg can exit 0 and still leave a truncated file (a corrupt
/// source, a full disk), so the finished file is probed and its length must match the source's. A
/// mismatch is reported as <see cref="EncodeFailureKind.LengthMismatch"/> before the original is ever
/// touched - a safety net HandBrake's output doesn't offer.
/// </summary>
public sealed class FFmpegRunner : IEncoderRunner
{
    private readonly IActiveEncodeProcess _activeProcess;
    private readonly IFFmpegProfileStore _profiles;
    private readonly IMediaProbe _probe;

    public FFmpegRunner(IActiveEncodeProcess activeProcess, IFFmpegProfileStore profiles, IMediaProbe probe)
    {
        _activeProcess = activeProcess;
        _profiles = profiles;
        _probe = probe;
    }

    /// <summary>How far an output's length may differ from the source's: whichever is larger of 3
    /// seconds and 1% - generous for timestamp rounding, far tighter than any real truncation.</summary>
    internal static bool LengthsMatch(double sourceSeconds, double outputSeconds) =>
        Math.Abs(sourceSeconds - outputSeconds) <= Math.Max(3.0, sourceSeconds * 0.01);

    public async Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        EncodeResult Finish(bool success, EncodeFailureKind failure = EncodeFailureKind.None, string? message = null, bool cancelled = false)
        {
            if (message is not null) log.AppendLine(message);
            File.WriteAllText(request.DetailLogFile, log.ToString());
            return new EncodeResult(success, request.DetailLogFile, cancelled, failure, message);
        }

        if (File.Exists(request.DetailLogFile)) File.Delete(request.DetailLogFile);

        var profile = _profiles.Find(request.PresetName);
        if (profile is null)
        {
            return Finish(false, EncodeFailureKind.NotStarted, $"ffmpeg profile '{request.PresetName}' was not found.");
        }

        var probePath = !string.IsNullOrWhiteSpace(request.ProbePath)
            ? request.ProbePath
            : Path.Combine(Path.GetDirectoryName(request.ToolPath) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        var probe = await _probe.ProbeAsync(probePath, request.SourcePath, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return Finish(false, cancelled: true);
        if (probe is null)
        {
            return Finish(false, EncodeFailureKind.NotStarted, File.Exists(probePath)
                ? $"ffprobe could not read '{request.SourcePath}' - the file may be damaged or not a video."
                : $"ffprobe could not read '{request.SourcePath}' (is ffprobe at '{probePath}'?).");
        }

        var plan = FFmpegPlanner.Plan(profile, probe);
        if (plan is null)
        {
            return Finish(false, EncodeFailureKind.NotStarted, "The file has no video stream to encode.");
        }

        if (profile.Crop == "auto" && plan.Video.Width > 0 && plan.Video.Height > 0)
        {
            var crop = await FFmpegCropDetector.DetectAsync(request.ToolPath, request.SourcePath, probe.DurationSeconds, plan.Video, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return Finish(false, cancelled: true);
            plan.Crop = crop;
            plan.CropReason = crop is null ? "no black bars were found" : $"black bars found - cropping to {crop.Width}x{crop.Height}";
        }

        var args = FFmpegCommandBuilder.Build(profile, probe, plan, request.SourcePath, request.OutputPath, request.ExtraOptions);
        WriteHeader(log, request, profile, plan, args);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ToolPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        var progressParser = new FFmpegProgressParser(probe.DurationSeconds);
        var stderr = new StringBuilder();
        var cancelled = false;
        var exitCode = -1;

        using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null || onProgress is null) return;
                var reading = progressParser.Feed(e.Data);
                if (reading is not null) onProgress(reading);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data);
            };

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return Finish(false, EncodeFailureKind.NotStarted, $"ffmpeg could not be started ('{request.ToolPath}'): {ex.Message}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _activeProcess.Register(process);

            using var killRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort - the process may have exited between the check and Kill.
                }
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                _activeProcess.Unregister();
            }

            // Forces the async stream readers to drain before stderr is read (see HandBrakeProcessRunner).
            process.WaitForExit();
        }

        lock (stderr) log.Append(stderr);

        if (cancelled) return Finish(false, cancelled: true);

        if (exitCode != 0)
        {
            return Finish(false, message: $"ffmpeg exited with code {exitCode}.");
        }

        if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length == 0)
        {
            return Finish(false, message: "ffmpeg finished but wrote no output file.");
        }

        // Verification: the result must be readable and as long as the source.
        var outProbe = await _probe.ProbeAsync(probePath, request.OutputPath, cancellationToken);
        if (outProbe is null)
        {
            return Finish(false, message: "The finished file could not be read back with ffprobe, so it was not trusted.");
        }

        if (probe.DurationSeconds is { } sourceLength)
        {
            if (outProbe.DurationSeconds is not { } outLength)
            {
                return Finish(false, EncodeFailureKind.LengthMismatch, "The finished file has no readable length, so it could not be checked against the source.");
            }
            if (!LengthsMatch(sourceLength, outLength))
            {
                return Finish(false, EncodeFailureKind.LengthMismatch,
                    $"The encoded file is {Format(outLength)} long but the source is {Format(sourceLength)} - it was not kept and the original was left alone.");
            }
            log.AppendLine($"Length verified: {Format(outLength)} = {Format(sourceLength)}.");
        }

        return Finish(true);
    }

    private static void WriteHeader(StringBuilder log, EncodeRequest request, FFmpegProfile profile, FFmpegPlan plan, IReadOnlyList<string> args)
    {
        log.AppendLine($"ffmpeg profile: {profile.Name}");
        log.AppendLine($"Source: {request.SourcePath}");
        log.AppendLine($"Video: stream {plan.Video.Index}, {plan.Video.Width}x{plan.Video.Height} {plan.Video.Codec}");
        log.AppendLine($"Deinterlace: {(plan.Deinterlace ? "yes" : "no")} - {plan.DeinterlaceReason}");
        log.AppendLine($"Crop: {plan.CropReason}");
        foreach (var d in plan.Audio) log.AppendLine($"Audio stream {d.Stream.Index} ({d.Stream.Language}, {d.Stream.Codec}, {d.Stream.Channels}ch): {d.Action} - {d.Reason}");
        foreach (var d in plan.Subtitles) log.AppendLine($"Subtitle stream {d.Stream.Index} ({d.Stream.Language}, {d.Stream.Codec}): {d.Action} - {d.Reason}");
        foreach (var d in plan.Dropped) log.AppendLine($"Dropped stream {d.Stream.Index} ({d.Stream.Kind}, {d.Stream.Language}, {d.Stream.Codec}): {d.Reason}");
        foreach (var n in plan.Notes) log.AppendLine($"Note: {n}");
        log.AppendLine("Command: " + request.ToolPath + " " + string.Join(" ", args.Select(Quote)));
        log.AppendLine(new string('-', 60));
    }

    private static string Quote(string arg) => arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
