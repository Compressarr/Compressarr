# Design note: capturing average fps per encoded file

Status: plan only, nothing built (2026-10-24). Target: 2.2.1 (not 2.2.0 final).

## Goal

Record a rough "average frames per second" for every file Compressarr encodes, so it can be shown in the report and the History page
later. It does not need to be precise, just a fair representation. The History page's Speed / GB-per-hour card stays dropped (the number
is not relatable to a person); this is only about having the data.

## Decision: one combined number, tagged by engine

Store ONE average fps per file, whichever encoder produced it. Both tools already report a running average, the user only wants a good
representation, and separate HandBrake/ffmpeg columns would add storage and UI nobody asked for. Each result already carries
`EncoderLabel`, so a per-engine split can be derived later from the same data without a new capture.

## What each encoder gives us

| Source | Today | Capture |
|---|---|---|
| HandBrake | Progress line `Encoding: task 1 of 1, 42.10 % (23.45 fps, avg 20.12 fps, ETA 00h05m32s)`. `HandBrakeProcessRunner.cs` (~line 239) matches it but does not capture the `avg` group. | The `avg` value from the LAST progress line (covers the whole encode). |
| ffmpeg | `-progress` key/value stream with `fps=` (`FFmpegProgress.cs`, line ~41). Believed to be effectively a cumulative average; not verified against ffmpeg's source. | The last non-zero `fps` seen, taken at `progress=end`. If it turns out to be an instantaneous value, compute frames / elapsed seconds instead. |
| Passthrough | `PassthroughPreset` reports no fps (it is a copy). | Nothing; left out of every average. |

Excluded (null, never 0): passthrough, failed encodes, cancelled encodes, a reading of 0 or missing. A file ffmpeg hands to HandBrake
(for example Dolby Vision) uses the figure of the encoder that actually produced the file. Multi-pass HandBrake presets print one task
per pass; use the last pass's figure and accept that it is rough.

## Steps

1. **Capture.** Add `double? AvgFps` to `EncodeResult` (`IEncoderRunner.cs`). `HandBrakeProcessRunner` keeps the last `avg`; `FFmpegRunner`
   keeps the last `fps`; each sets it on a successful finish. `ConversionOrchestrator` copies it to `ConversionResult.AvgFps`.
2. **Run figure.** `Sum(fps x encode seconds) / Sum(encode seconds)` over the files that have a value (equal to total frames over total
   time, so no frame count is needed).
3. **Report.** One summary line ("Average speed 23 fps") and the per-file figure in each row's details/tooltip. NOT a new table column:
   the repair scripts in Compressarr-Utilities read report rows by column position.
4. **History.** New trailing CSV column 17 `AvgFps` (0-based 16; blank = unknown, never 0; old readers ignore trailing columns), carried
   through `RunHistoryRecord`, the rollups and the `/api/history/runs` arrays (append, do not reorder). A period's figure weights each
   run by its encoding time.
5. **Token (optional).** `{avg_fps}` for Custom notification messages, like `{movie_count}`.
6. **UI (optional, last).** An off-by-default "Average speed" card in the Stats menu. Skip unless wanted.

## Things to check while building

- Whether the per-file result survives an app restart mid-run (it is persisted with the queue entry); if not, that file's fps is blank.
- ffmpeg's `fps` semantics (cumulative vs instantaneous), by a real run with the ffmpeg lane.
- No backfill: old reports and history rows hold no fps and the repair scripts cannot recover it, so the data starts with the release
  that ships this.

## Tests

- HandBrake parser captures `avg`; ffmpeg keeps the last fps.
- Runner sets `AvgFps` only on success; passthrough, failure and cancel give null.
- Weighted average, including the passthrough exclusion and an all-unknown run (blank, not 0).
- CSV round trip and old rows (16 or fewer columns read as unknown); `/api/history/runs` shape.
- Report line present with a value and absent without.
- Test-plan entry; CHANGELOG; wiki (History page / Notifications if the token is added).

## Size and risk

About half a day. Mostly additive, no migration. It would be the third change to the history CSV in a short time, so ship it with
2.2.1 rather than before 2.2.0 final (an rc.4 only if the user wants it sooner).
