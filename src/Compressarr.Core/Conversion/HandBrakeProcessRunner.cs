using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Compressarr.Core.Conversion;

/// <summary>The HandBrakeCLI implementation of <see cref="IEncoderRunner"/>: invokes HandBrakeCLI
/// and determines success the same way v1 did, plus a real exit-code check - the output temp file
/// must exist, be non-empty, the stderr detail log must contain a line matching "*Finished work
/// at*" (HandBrake's own completion banner), AND the process must have exited 0. The exit-code
/// check is not optional - confirmed live against a genuinely full disk that HandBrakeCLI still
/// writes "Finished work at" to its log even when the encode fails (mux error, exit code 4) - the
/// log-banner check alone reports a mid-air-truncated file as a success, which without the exit
/// code would also delete/route the source out from under it.
///
/// Streams stdout line-by-line as HandBrakeCLI writes it (its own progress updates, "Encoding:
/// task 1 of 1, NN.NN % ...") and reports each parsed reading to onProgress.</summary>
public sealed class HandBrakeProcessRunner : IEncoderRunner
{
    private readonly IActiveEncodeProcess _activeProcess;

    public HandBrakeProcessRunner(IActiveEncodeProcess activeProcess)
    {
        _activeProcess = activeProcess;
    }

    /// <summary>The exact HandBrakeCLI argument list for a request. A pure function so the command
    /// line - the one thing that must never change by accident - is pinned by golden tests without
    /// starting a process. ArgumentList entries, not a manually quoted Arguments string: each
    /// element is passed to the child process exactly as given, with no shell-style re-parsing or
    /// re-quoting step that a path or preset name containing spaces (or, in principle, an embedded
    /// quote) could ever trip up.</summary>
    internal static IReadOnlyList<string> BuildArguments(EncodeRequest request)
    {
        var args = new List<string>
        {
            "-i", request.SourcePath,
            "-t", "1",
            "-o", request.OutputPath,
            "--preset-import-file", request.PresetSource,
            "--preset", request.PresetName
        };

        if (!string.IsNullOrWhiteSpace(request.ExtraOptions))
        {
            // ExtraOptions is the free-form "Extra CLI options" setting - deliberately meant to
            // represent MULTIPLE arguments (e.g. "--two-pass --optimize"), so it needs splitting
            // into individual tokens rather than being added as one single (and wrong) argument.
            args.AddRange(SplitExtraOptions(request.ExtraOptions));
        }

        return args;
    }

    public async Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        var tempOutputPath = request.OutputPath;
        var detailLogFile = request.DetailLogFile;

        if (File.Exists(detailLogFile))
        {
            File.Delete(detailLogFile);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ToolPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stderr = new StringBuilder();
        var cancelled = false;
        var exitCode = -1;
        double? averageFps = null;   // the last "avg" reading: it covers the whole encode (the last pass of a multi-pass one)

        using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
        {
            // HandBrakeCLI writes its progress updates ("Encoding: task 1 of 1, NN.NN % ...") to
            // stdout as it runs - event-driven reading (rather than the old ReadToEndAsync, which
            // only ever saw the output after the process had already exited) is what makes live
            // progress possible at all.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (HandBrakeProgressParser.TryParseAverageFps(e.Data) is { } average) averageFps = average;
                if (onProgress is null) return;
                var progress = HandBrakeProgressParser.TryParse(e.Data);
                if (progress is not null) onProgress(progress);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            process.Start();
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
                    // Best-effort - process may have already exited between the check and Kill.
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

            // WaitForExitAsync doesn't reliably guarantee pending ErrorDataReceived events have
            // fully fired before it returns when streams are redirected to async handlers - a
            // documented .NET behavior gap (dotnet/runtime#34294, #42556), not a theoretical one.
            // HandBrakeCLI's own "Finished work at" completion line is among the last things it
            // emits right as it exits - exactly the content DetermineSuccess depends on and most
            // at risk of not having arrived in stderr yet. The parameterless synchronous
            // WaitForExit() forces the async stream reader to fully drain before this reads
            // stderr's accumulated content - a plausible source of a rare, hard-to-reproduce false
            // "encode failed" result otherwise. Harmless/instant here regardless of path: the
            // process has either already exited normally or was just killed above.
            process.WaitForExit();

            File.WriteAllText(detailLogFile, stderr.ToString());
        }

        if (cancelled)
        {
            return new EncodeResult(Success: false, DetailLogFile: detailLogFile, Cancelled: true);
        }

        var success = DetermineSuccess(tempOutputPath, detailLogFile, exitCode);
        return new EncodeResult(success, detailLogFile, AvgFps: success ? averageFps : null);
    }

    /// <summary>Success requires ALL of: the temp output file exists, is non-empty, the detail
    /// log contains a line matching "*Finished work at*" (HandBrake's own completion banner), AND
    /// the process exited 0. The exit-code check matters on its own, not just as a formality -
    /// HandBrakeCLI still writes "Finished work at" even on a failed encode (confirmed against a
    /// genuinely full disk: mux error, non-zero exit, but the banner line was there anyway), so
    /// without it a failed encode reports as a success. Extracted as an internal static method so
    /// these conditions can be tested independently of actually invoking a process.</summary>
    internal static bool DetermineSuccess(string tempOutputPath, string detailLogFile, int exitCode)
    {
        if (!File.Exists(tempOutputPath)) return false;
        if (exitCode != 0) return false;

        var hasFinishedLine = File.Exists(detailLogFile) &&
            File.ReadLines(detailLogFile).Any(l => l.Contains("Finished work at", StringComparison.Ordinal));
        var nonEmpty = new FileInfo(tempOutputPath).Length > 0;

        return hasFinishedLine && nonEmpty;
    }

    /// <summary>Splits a free-form "Extra CLI options" string into individual ArgumentList
    /// entries, honoring double quotes so a quoted segment (e.g. --custom-anamorphic "16:9") stays
    /// one argument - the same shell-style splitting a plain Arguments string used to get for free
    /// before this moved to ArgumentList, which needs pre-split tokens instead. Not a full Win32
    /// CommandLineToArgvW port (no backslash-escaping rules) - just enough for the flag/value
    /// shapes HandBrakeCLI options actually use. A pure static function so it's unit-testable
    /// without invoking a process, same pattern as DetermineSuccess above.</summary>
    internal static IReadOnlyList<string> SplitExtraOptions(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>True if input has an unterminated quote - the same "still inside a quote" state
    /// SplitExtraOptions' own tokenizer would be left in at the end of the string, which silently
    /// folds everything after the stray quote into one final token rather than actually rejecting
    /// it. Deliberately surfaced as a Settings-page validation warning (see SettingsValidator)
    /// instead of making SplitExtraOptions itself throw at encode time - a malformed value should
    /// be visible and fixable on the Settings page before it's ever used, not crash an otherwise-
    /// healthy run the moment it's used. A pure static function, same testable-without-a-process
    /// pattern as SplitExtraOptions/DetermineSuccess above.</summary>
    internal static bool HasUnbalancedQuotes(string input)
    {
        var inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"') inQuotes = !inQuotes;
        }
        return inQuotes;
    }
}

/// <summary>
/// Parses HandBrakeCLI's own stdout progress line, e.g.:
///   "Encoding: task 1 of 1, 42.10 % (23.45 fps, avg 20.12 fps, ETA 00h05m32s)"
/// A pure static function (same pattern as HandBrakeProcessRunner.DetermineSuccess) so parsing is
/// unit-testable against real-shaped fixture strings without invoking a process.
/// </summary>
public static class HandBrakeProgressParser
{
    private static readonly Regex ProgressLine = new(
        @"^Encoding:\s*task\s*\d+\s*of\s*\d+,\s*(?<percent>[\d.]+)\s*%(?:\s*\((?:(?<fps>[\d.]+)\s*fps[^,)]*,\s*)?avg\s*(?<avg>[\d.]+)\s*fps(?:,\s*ETA\s*(?<eta>[\dhms]+))?\))?",
        RegexOptions.Compiled);

    public static EncodeProgress? TryParse(string line)
    {
        var match = ProgressLine.Match(line);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        double? fps = match.Groups["fps"].Success &&
            double.TryParse(match.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fpsValue)
                ? fpsValue
                : null;

        string? eta = match.Groups["eta"].Success ? match.Groups["eta"].Value : null;

        return new EncodeProgress(percent, fps, eta);
    }

    /// <summary>The running average ("avg 20.12 fps") of a progress line, or null for any other line or a zero reading (HandBrake prints avg 0.00
    /// before it has measured anything). Kept apart from <see cref="TryParse"/> so the live progress reading stays as it was.</summary>
    public static double? TryParseAverageFps(string line)
    {
        var match = ProgressLine.Match(line);
        if (!match.Success || !match.Groups["avg"].Success) return null;
        return double.TryParse(match.Groups["avg"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var avg) && avg > 0 ? avg : null;
    }
}
