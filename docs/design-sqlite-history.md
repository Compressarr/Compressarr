# Design note: SQLite run archive, reports on demand (Compressarr 3.0)

Status: BUILT 2026-10-24 on 2.2-dev as 3.0.0-rc.1 (stages 1-4; stage 5, the API, is next). Differences from the plan below: a pass that only has a
report is `runs.kind = 'pass'` (not 'problem'); runs imported with a saved report keep the original html in a blob with the pictures swapped for
tokens (about a fifth of the size); `COMPRESSARR_DATA_DIR` names another data folder; the sidebar badges are one query (`/api/history/badges`);
`/api/reports/{id}` serves a built report (`?download=1` as a file) and still serves a saved copy by file name; Export CSV is
`/api/history/export.csv`. Decided 2026-10-24 (user: "make the jump, now's the time"). First build: 3.0.0-rc.1. The version continues from 2.2.0-rc.3
(nothing of 2.2 was released as final; rc.1 and rc.2 stay as published pre-releases).

## Why

`Compressarr_History.csv` is read by column position (22 columns after three additive changes in a week), has no integrity guarantee (a crash
mid-append can glue two runs into one line; one garbage number makes the whole History page throw), packs lane/profile counts into escaped text,
and is recomputed from scratch for every History request. The reports are HTML files that the throughput estimator and three scripts scrape.
The heavy user (about 10,000 files a year) and the planned API both want a real store.

## Decisions

1. **One database, `compressarr.history.db`** in the app data folder, `Microsoft.Data.Sqlite`, WAL off (single file; backups use SQLite's backup API).
   `PRAGMA user_version` is the schema version; each step is a migration.
2. **Per-run row + per-file rows** (user: yes). File NAMES are stored because the report is regenerated from the rows (the reports already show
   them; they never leave the machine, and `{file_list}` stays the only token that can send one out).
3. **Reports on demand** (user's idea): no HTML file per run. `GET /reports/{id}` builds the same report from the rows with the existing
   `HtmlReportGenerator`. Report links never die (history is the report). Backups carry them (they are in the database).
   An optional setting, "Also save a copy of each report as a file" (off by default, uses the old Report path), keeps the file workflow.
   Runs imported from the CSV that still have a saved report keep their ORIGINAL html (gzip blob), so old reports look exactly as before.
4. **Running totals** (user's idea): `daily_totals` (+ `daily_lanes`, `daily_profiles`), updated in the same transaction as each recorded run;
   `RebuildTotals()` recomputes them from the runs. Period figures = sums over at most 365 rows.
5. **Run counter** moves into `meta` (Clear History deletes runs, never the counter).
6. **CSV import** on first start with no database: rows inserted in one transaction, totals checked against the CSV (runs, files, sizes, seconds),
   the CSV is renamed `Compressarr_History.csv.imported`, never deleted. Saved reports still on disk are parsed for the per-file rows and
   lane/profile/engine breakdown of those runs and stored as blobs. A failed import leaves the CSV in use.
7. **The three PowerShell scripts** in Compressarr-Utilities become pre-3.0 tools (for a CSV before its first 3.0 start); the app does the same
   backfill itself during the import. An "Export history as CSV" button replaces hand-editing.
8. **API** (Phase B, after the storage lands): read-only history/stats endpoints on top of SQL, an API key, documented endpoints and
   Home Assistant actions (scheduling), as in the roadmap's API item.

## Schema v1

```
meta(key PK, value)                                -- run_counter, imported_csv_at
runs(id PK, run_number, ended_date, ended_at, seconds, files, before_gb, after_gb, errors, warnings, redirects,
     movies, tvs, avg_fps, hb_files, ff_files, pass_files, retries_succeeded, summary_log, legacy_report, legacy_report_name)
run_lanes(run_id, ord, lane, problems)             -- the report's lane sections, in order (also lanes with no files)
run_profiles(run_id, profile, files)               -- profile counts, passthrough excluded
run_files(id PK, run_id, lane_ord, name, content_type, preset, encoder_label, passthrough, engine, before_gb, after_gb, seconds,
          started_at, ended_at, success, counted, avg_fps, error_code, failure_reason, warning_text, warning_codes, arr_status,
          detail_log, redirected_to_lane_id, redirected_to_lane, home_lane, disk_full)
daily_totals(day PK, runs, files, before_gb, after_gb, seconds, movies, tvs, unknown_files, errors, warnings, redirects,
             hb_files, ff_files, pass_files, fps_weighted, fps_seconds)
daily_lanes(day, lane, files)  daily_profiles(day, profile, files)
```
A pass that processed nothing because a lane is misconfigured also gets a `runs` row (run_number 0, files 0) so its report can be opened; it is
left out of every total, as today.

## Stages

1. SQLite store behind `IRunHistoryStore` (same records), schema, totals, CSV import, backup/restore, maintenance actions.
2. Per-file rows + reports on demand, throughput estimator from rows, saved-copy setting, report links by id.
3. Rollups and the History page from the totals tables; a light endpoint for the sidebar badges.
4. 3.0.0-rc.1: version, docs, installer.
5. API.
