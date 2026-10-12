# Design: per-lane processing settings

Status: plan for review, written 2026-10-11. Nothing is built. Decisions to confirm are in section 10 (recommended answers marked).
Target: 2.3 (2.2 is in beta and this touches the engine's core file handling). Not for 2.1.

## 1. Goal

Let each lane have its own **processing** settings instead of always using the global ones on Settings > Processing, behind an
**Advanced** checkbox on the lane: **"Use global settings for processing"**, ticked by default. A user asked for the flexibility; the
obvious uses are one lane that keeps its sources (Maintain) while the others delete, a kids or archive lane with a different minimum
size, and different companion-file handling per library.

Non-goals (first version):

- No per-lane encoder, profile or schedule settings (profiles are already per lane; the schedule is global on purpose).
- No per-file overrides beyond what the queue already has (preset override, lands-in lane).
- No change for anyone who does not untick the box: with it ticked (the default) a lane behaves exactly as today.

## 2. Principles

1. **Nothing changes by default.** A lane with no override is byte-for-byte today's behaviour, including every existing config, backup and export.
2. **One lane, one set.** The settings that apply to a file are the ones of the lane that owns it. No mixing a lane's value for one field with the global value of another unless the user chose that (section 4).
3. **No new places to forget.** The engine asks one function "what are this lane's processing settings", never reads the global block directly any more.
4. **Safe to leave alone.** Hidden under Advanced, but a lane that overrides is visibly marked, so a custom lane never surprises someone later.
5. **Same words as Settings.** The lane uses the same labels, help text and validation as the Settings > Processing card.

## 3. Which settings

Today `ProcessingSettings` holds 10 fields. Proposed split:

| Field | Per lane? | Why |
|---|---|---|
| **Delete after convert** (Delete / Recycle / Maintain) | **Yes** | The headline case: one lane keeps sources, others do not |
| **Move converted files** | **Yes** | A lane may only convert in place |
| **If the destination already has it** (overwrite / skip / rename) | **Yes** | A library-specific policy |
| **Minimum file size** | **Yes** | Kids/TV lanes differ from movies |
| **Video file types** | **Yes** | A lane of old `.avi`/`.mpg` rips |
| **Clear the title tag** | **Yes** | Per library preference |
| **Companion extensions** and **Unmatched companion action** (one group) | **Yes** | Subtitle/artwork handling per library |
| Per-pass file limit | No (v1) | It is a run-wide throttle, not a lane property |
| Write output to the same folder as input | No (v1) | Interacts with the lane's Output field; revisit if asked |

FileBot's renaming settings (a separate block today) are the other obvious candidate; they get the **same checkbox pattern in a
second phase** (section 9), not in the first.

## 4. Data model

- `LaneConfig` gets an optional `Processing` object (same class as the global one). **Null means "use global"**; that is what every
  existing lane has, so nothing migrates.
- Unticking the box copies the **current global values** into the lane's own object and shows them for editing. From then on the lane
  has its **own complete set**: later changes in Settings do not reach it. Re-ticking the box deletes the lane's copy (with a confirm
  if it differs from global). *(Alternative considered, per-field overrides: more precise, but each field needs its own tick and
  "what does this lane actually do" gets hard to read. See decision 1.)*
- One function does the resolving, in Core:
  `ProcessingSettings LaneProcessing.For(CompressarrConfig config, LaneConfig lane) => lane.Processing ?? config.Processing`.
  Every consumer takes the resolved object, so a field added later is covered automatically.
- **Saved format:** the lane JSON gains a `processing` object only when overridden. Export/Import and the automatic backups already
  carry lanes, so they carry this with no new code. An older Compressarr ignores the unknown property (see the rollback risk, 8).

## 5. Where the engine reads processing settings today (what must change)

About 35 reads across Core and Web. Each is changed to read the lane's resolved object.

| Area | Reads | Notes |
|---|---|---|
| `ConversionOrchestrator` (prepare a lane, encode a file) | scan filters (types, minimum size, limit), output-folder choice, title-tag clearing | The lane is in hand at every site |
| `EncodedFileFinisher` (after an encode) | collision mode, move on/off, delete mode, companion handling, **kept-source note** | Lane in hand through the entry |
| `RetryRecovery` (move / companion / cleanup retries) | the same set again, plus the kept-source note at five places | Entries carry `LaneId`; the lane is looked up once at the top |
| `SourceCleanupCoordinator` (empty-folder cleanup) | video types, delete mode | Needs the lane passed in |
| `QueueRules` (Monitor queue, ordering) | the three scan filters, in three places | Must match the engine exactly or the queue lies |
| `LaneValidator` | move on/off (destination-path check), output-same-as-input | Validates the lane's own settings |
| `RunOrchestrator` | **title-tag clearing as one global switch** | See 5.1 |
| Web `ConfigMapping` and Settings DTO | maps the global block only | Lane DTO gains the override |

### 5.1 The one shared switch

`RunOrchestrator` sets `_metadata.Enabled` once per pass from the global setting, and `ConversionOrchestrator` sets it again before each
file. With a per-lane value that becomes **per file only**: set it from the file's lane immediately before processing it, and drop the
per-pass set. This is the one place where a global flag on a shared service has to become a per-call value, and it needs its own test.

## 6. The UI

- **Lanes page, per lane, under Advanced:** a "Processing for this lane" section with the checkbox **Use global settings
  (Settings > Processing)**, ticked by default. Unticked, it shows the seven settings from section 3 using the same controls and help
  text as Settings, pre-filled from the global values at that moment.
- **Visible when it matters:** a lane that overrides shows a small chip, **Custom processing**, in its header even when Advanced is
  off, and the Monitor log line for the lane's pass says so ("Lane Kids uses its own processing settings").
- **Settings > Processing** gets one line under its title: "Lanes that use their own processing settings are not affected" with a
  count when there are any.
- Validation is the Settings validation (video types not empty, and so on), shown on the lane's own fields.
- Both the Lanes page and the report keep working unchanged for lanes that do not override.

## 7. Behaviour decisions inside the engine

- **Which lane governs a file that "lands in" another lane's library?** The **lane that owns the file** (where it was found). It
  decides what happens to the source and the companions; the destination lane only supplies the library folders, as today.
- **Kept sources (Maintain)** per lane: the "converted and left in place" memory introduced in 2.1.10/2.2 is already keyed by lane and
  file, so a Maintain lane next to a Delete lane works with no new rule; the end-of-pass cleanup remembers only what a lane kept.
- **Deleting a lane** removes its override with it (no orphaned settings); duplicating a lane copies its override.
- **Changing a setting mid-run** keeps today's rule: each file re-reads the configuration, so a change applies from the next file.

## 8. Risks

1. **Rolling back to 2.1** (or any build without this feature) silently drops the override, so a lane that was set to **Maintain** while
   the global setting is Recycle would start **recycling its sources** after the conversion. This is the one real data-loss path, and
   it is the reason this stays out of 2.1/2.2. Mitigation: say it in the release notes and the setting's help text; since 2.1 retires,
   the exposure is small and short.
2. **Queue and engine disagreeing.** If the Monitor queue (`QueueRules`) used different filters from the engine, files would show that
   never run (or the reverse). Mitigation: both use the one resolver, and a test compares them for a lane with its own types and minimum.
3. **A field added to the settings later** being read globally by mistake. Mitigation: consumers take the resolved object, not the
   config; a test asserts the engine never reads `config.Processing` outside the resolver (a simple scan of the source files for
   the pattern) - so a new read cannot slip in unnoticed.
4. **User confusion** ("why is this lane deleting? Settings says Maintain"). Mitigation: the chip, the log line, and Advanced-only
   placement.

## 9. Phasing

1. **Engine, no UI.** The model, the resolver, every site in section 5, the title-tag switch, validation, mapping, tests. Behaviour is
   unchanged for everyone; the override can be set by editing the settings file, which is how the first tests drive it.
2. **Lanes UI.** The Advanced section, the chip and log line, the Settings note, duplicate and delete handling, docs and test-plan items.
3. **FileBot per lane**, with the same checkbox, once phase 2 has proven the pattern.

Rough size: phases 1 and 2 are about a session each; phase 3 about half a session.

## 10. Decisions to confirm

1. **Checkbox with a full copy, or per-field overrides?** *Recommended:* the checkbox with a full copy (principle 2): one clear
   statement of what a lane does, simplest to explain and test. Per-field is more precise but harder to read.
2. **The field list** in section 3 for the first version. *Recommended:* as listed; add the others only when asked. If you know which
   setting the user who asked wanted, that decides the order of the work.
3. **Who governs a file that lands in another lane's library.** *Recommended:* the lane that owns the file.
4. **FileBot in the same release or later.** *Recommended:* a second phase of the same release if time allows, otherwise 2.3.1.
5. **The "Custom processing" chip and log line.** *Recommended:* yes, as in section 6.
6. **Accepting the rollback caveat** (section 8, risk 1) given that this ships only after 2.1 retires. *Recommended:* yes, with the
   release-note line.
7. **Target:** 2.3, as above, rather than squeezing it into 2.2.
