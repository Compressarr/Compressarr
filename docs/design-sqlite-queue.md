# Design note: the queue and resume state in SQLite (Compressarr 3.x)

Status: BUILT 2026-10-24 on 3.0-dev (uncommitted until asked), to ship AFTER 3.0.0-rc.1 has been tested on production (user: "build it now, ship it after rc.1 is tested").
Build notes: the database file is `compressarr.state.db`; `StateDatabase` (schema v1, quick_check, backup snapshot) and `SqliteResumeStateStore` are in `Compressarr.Core/Conversion`; the
store derives its database from the resume-file path it is handed (`compressarr.resume.json` maps to `compressarr.state.db`, any other name to `<name>.state.db`), so tests and the app share
one code path. Decisions 1-7 were built as written. Checked on 50, 500 and 5,000 synthetic entries: the import is exact (every compared field identical) and takes 0.07-0.5 s, and a plain
load or one edit costs about the same as the JSON file (the gain is safety, not speed - a load still reads every row; row queries for the Monitor come later). The 4th item under
"Why it is fragile today" (every poll parses the whole file) therefore stays true until the Monitor asks for rows instead of the whole list.

Originally PROPOSED 2026-10-24. Raised by the user ("the resume file has always been very fragile") while discussing whether settings belong in the database.
Settings, lanes and notifications stay in `compressarr.settings.json` (portable, hand-editable, needed before anything else starts). This note is only about the
data the app writes while it works.

## What `compressarr.resume.json` is

One JSON array, one `ResumeEntry` per tracked file, keyed by lane id + full path. It is the whole queue and everything the engine must remember between passes:

| Group | Fields | What is lost if the file is lost |
|---|---|---|
| Queue edits (the user's) | `Order`, `Skipped`, `Removed`, `PresetOverride`, `DestinationLaneId`, `CreatedByQueueEdit` | the order, skips, per-file profiles and "land in another lane" choices |
| Progress | `Status` (Pending, Completed, Error, MoveFailed, CompanionMoveFailed, CleanupPending), `HeldAfterFailure`, `FileBotUnmatched` | which files failed and are held; which are done |
| Recovery pointers | `EncodedFilePath`, `EncodedFileDesiredName`, `PendingCompanionSourceDirectory`, `PendingCompanionVideoDestPath`, `LastRetryFailureMessage` | the only record of an encoded file stranded in Output (a move that failed) and of stranded companions: the file stays on disk but nothing retries or reports it |
| Kept sources | `KeptSourceSize`, `KeptSourceModifiedUtc` | a source kept by Maintain would be converted again, every pass |

It is read by the engine, the Monitor page (every poll), profile and lane delete checks, backup/restore and the maintenance actions: about 60 call sites in 14 files, all through
`IResumeStateStore` (`Load`, `Save`, `Update(path, mutate)`, `DeleteIfComplete`).

## Why it is fragile today

1. **A damaged file stops everything.** `Load` deserializes with no handling: a torn or hand-edited file throws, every Monitor poll and every pass fails, and there is no
   fallback. (The settings file at least reports a clear error; the history has a CSV fallback.)
2. **Every change rewrites the whole file** (temp file + move). It is atomic against a crash, but each Monitor edit and each finished file rewrites every entry, and the move can
   fail when an antivirus or indexer holds the file open.
3. **Lost updates are prevented by a lock and a pile of merge code** (`ResumeResults.SaveChanges`, `MergeUserEdits`, `RefreshResumeState`, reflection over every field).
   The lock is in-process only; two processes (a second app instance, a script) would clobber each other.
4. **Every Monitor poll parses the whole file.** Fine at 50 files, slow at a few thousand queued.
5. **"Nothing left" deletes the file**, so an empty queue and a lost queue look the same.

## Decisions to make (recommendations first)

1. **A separate database file, `compressarr.state.db`**, not the history database. The history is derived data and can be rebuilt or re-imported; the queue is working state.
   A damaged history database must not be able to take the queue down with it, and the reverse. The same file can later hold other app-written values (below).
2. **Keep `IResumeStateStore` exactly as it is.** A `SqliteResumeStateStore` implements the same four methods, so the ~60 call sites, `ResumeResults`, `QueueRules` and the
   orchestrator do not change in the first release. `Update(mutate)` becomes: `BEGIN IMMEDIATE`, load the rows, run the callback, write only the rows that changed (insert,
   update, delete), commit. That is atomic, crash-safe (SQLite's journal rolls back a half-written change) and safe across processes.
3. **One row per file**, unique on (`lane_id`, `full_name` NOCASE), one column per field, plus `created_at` and `updated_at`. No JSON blobs. Indexes on `status` and `order`.
4. **Migration on first start:** if `compressarr.resume.json` exists and the database has no import marker, import all entries in one transaction, check the count, then rename the
   file to `compressarr.resume.json.imported` (never delete it). A file that cannot be parsed is moved aside as `compressarr.resume.json.damaged`, the problem is logged and shown,
   and the app starts with an empty queue (the folders are rescanned, as after "Reset Resume File").
5. **Integrity:** `PRAGMA quick_check` when the database opens. A database that fails it is moved aside as `compressarr.state.db.damaged`, the problem is logged, and the app
   starts empty, never silently. An existing database is never deleted (same rule as the history database).
6. **Backup and restore** take a SQLite backup-API snapshot (as the history database does) in place of the JSON file; restore of an old backup that still holds `resume.json`
   imports it.
7. **Reset Resume File / Clear** become `DELETE FROM`, not a file delete. An empty queue is an empty table.
8. **Version:** this is a second storage migration on top of the history one, which is still waiting for the production test. Recommended: build it on `3.0-dev` as its own commit and
   ship it as the next release candidate after the history database has been tested on the production machine, rather than folding both migrations into one test.

## Later, once it is in (not part of the first release)

- Replace the diff-and-merge code (`SaveChanges`, `MergeUserEdits`) with plain row updates: a queue edit becomes `UPDATE ... WHERE lane_id = ? AND full_name = ?`, and have the Monitor ask for rows
  (a page of the queue, counts per lane) instead of the whole list on every poll. **Gated (user, 2026-10-25): only after the queue database has had a few weeks in production.**
- **Other app-written values that live in the settings file today** and make the scheduler rewrite the user's settings: `HistoryViewedThroughRunNumber`, `LastRunUtc`, and
  the four digest-sent dates (`ToastLast*DigestSentDate`, `Last*DigestSentDate`). They would move to a small key/value table in the same database, which also stops a settings save
  from racing with them.
- A `queue_events` table (when a file was queued, skipped, reordered, failed, completed) for an audit trail and the API.
- API (Phase B): read-only queue endpoints on SQL, then actions (skip, reorder) for Home Assistant.

## Build stages

1. `StateDatabase` (schema v1 through `PRAGMA user_version`, quick_check, damaged-file handling, snapshot) and `SqliteResumeStateStore` behind the existing interface.
   The existing store tests (`ResumeSaveChangesTests`, `QueueHost`, orchestrator and backup tests) run against both the JSON and SQLite stores.
2. JSON import with count check, `.imported` and `.damaged` handling, backup/restore, maintenance actions, `COMPRESSARR_DATA_DIR`.
3. Tests for crash-safety (an exception inside `Update` leaves the rows unchanged), concurrent `Update`s from two connections, a damaged database, a damaged JSON file, and a
   migration check on a copy of this machine's real resume file.
4. Docs: settings reference and troubleshooting in the wiki, CHANGELOG, test plan (queue edits survive a restart, a stranded move is still retried after the import).

## Risks

- A bug in the import could lose queue state: mitigated by the count check, keeping the `.imported` JSON, and testing on a copy of real data.
- SQLite file locking on a network share or a synced folder (OneDrive): the data folder is local `%AppData%`, as for the history database.
- No way back to the JSON store after the migration; 2.x is retired, so the `.imported` file is the only fallback and is enough to rebuild by hand.
