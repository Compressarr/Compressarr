# Design note: the Compressarr API (3.0.0-rc.3, "Phase B")

Status: PROPOSED 2026-10-25, nothing built. Asked for twice (2026-10-23 "API key, let Home Assistant control things, especially scheduling"; 2026-10-24 "add the api with sql").
rc.3 is the first public 3.0 pre-release (rc.1 and rc.2 stay local), so this is the headline of that release.

**Decided 2026-10-25 (user: "yes to all"):** every recommendation below is accepted - including Decision 5, so **"Protect the web interface" (a username and password) is part of rc.3**, not a follow-up; a
hold may be created without an expiry (with the visible banner); read and control scopes with several named keys; no anonymous v1 (401 until a key exists); the first cut of v1 is as listed (no abort,
queue edits or maintenance). Stage 0 (app-written values into the state database) is built; stage 1 onward are next.

## Where things stand

- About 130 minimal-API endpoints under `src/Compressarr.Web/Endpoints/` serve the web UI. They are internal, undocumented, and change whenever a page does. Only `GET /api/run/status` is
  documented (wiki *Home Assistant Integration*), and only because people asked.
- **There is no authentication anywhere.** The web host binds `0.0.0.0` over plain HTTP (`App.axaml.cs`), so anyone on the LAN can use every endpoint, including `PUT /api/settings`, which can
  set the post-execution command (a command that Compressarr then runs as the user), and `GET /api/settings`, which returns the Sonarr/Radarr/TMDb keys. This matches the \*arr apps' default
  posture, but an "API key" feature does not change it: **a key can only protect what sits behind it** (see "Decision 5").
- The data an API wants is now in SQL: runs, per-file rows and daily totals in `compressarr.history.db`; the queue in `compressarr.state.db`. The engine already has the verbs an automation
  wants: run now, start/stop monitoring, pause/resume, "run anyway" (lift the off-hours hold until the next window change), and the schedule settings.

## What the user asked for

Read the numbers from outside (dashboards, scripts, Home Assistant), and let Home Assistant **control scheduling** ("people love their automations"): hold encoding while the TV is on or the
power price is high, allow it when nobody is home, change the window, run now.

## Decisions (recommendation first)

1. **A new, small, versioned namespace: `/api/v1/...`.** Stable JSON contracts with their own DTOs, decoupled from the UI's internals. The 130 internal endpoints are untouched and
   documented as "may change without notice". Rules for v1: additive changes only (new fields, new endpoints), a breaking change is a `/api/v2`; ISO 8601 UTC timestamps; sizes in bytes
   and gigabytes as named; paging with `?page=&pageSize=` plus an `X-Total-Count` header (as `/api/history/reports` already does); errors as
   `{ "error": { "code": "...", "message": "..." } }` with a real HTTP status.
2. **API keys, hashed, in `compressarr.state.db`.** Settings > Web UI > *Automation API* lists keys (name, scope, created, last used), creates one (shown **once**, with a Copy button,
   like a GitHub token) and revokes one. Only a SHA-256 hash is stored. Because it lives in the state database and not in `compressarr.settings.json`, a settings export (the thing people
   attach to support requests) never carries a key; a backup does (it carries the state database).
   - **Two scopes**, so a dashboard key cannot change anything: `read` (GET endpoints) and `control` (read, plus the actions below). A key can be named ("Home Assistant").
   - Sent as `X-Api-Key: <key>` (also accepted as `Authorization: Bearer <key>`). Compared in constant time. Failed attempts are logged and slowed (a short delay after repeated failures).
   - **Secure by default:** with no key created, every `/api/v1` call answers `401` with a message that says where to create one. There is no anonymous mode for v1.
3. **`/api/run/status` stays open and unchanged.** It is documented, people have Home Assistant cards on it, and breaking them would be a bad first impression. The wiki marks it as the
   legacy open status and points to `/api/v1/status` (same data, stable names, behind a key) for anything new.
4. **Scheduling control is an external hold, plus the schedule settings.** Home Assistant needs a switch, not a settings editor:
   - `POST /api/v1/hold` `{ "reason": "TV on", "until": "2026-10-25T23:00:00Z" (optional), "suspendCurrent": false }` and `DELETE /api/v1/hold`. While held, **no new file starts**; the
     file in flight finishes, unless `suspendCurrent` is true (it is frozen, as the schedule's "suspend it until off-hours" does). The hold is persisted in the state database so a restart
     during movie night keeps it, shows in `/api/v1/status` (`hold: { active, reason, since, until }`), and shows in the Monitor's top bar with a **Release** button, so nobody wonders why
     nothing is running.
   - It works with or without the Scheduler page being enabled (today `EncodeSchedule.GetStatus` returns early when the schedule is off): `IsHeld` becomes "scheduled hold or external hold".
   - `POST /api/v1/schedule/run-anyway` (the existing `RunAnyway`), and `GET`/`PATCH /api/v1/schedule` for the persistent settings (enabled, only-off-hours, window start and end per
     the existing three modes), validated by the existing `ScheduleValidator`. `PATCH` changes what the user sees on the Scheduler page, so the response says so and the docs say so.
   - Open question for the user: should a hold without `until` be allowed? Recommendation: yes, but the Monitor shows "Held by automation since ..." prominently, and the docs tell Home
     Assistant users to send `until` (a hold left behind by a dead automation is the failure to design against).
5. **DECIDED: Protect the web interface ships in rc.3 (new stage 3b below).** Original reasoning: **Be honest about the open web UI.** The recommendation was to ship the API as above and write the LAN warning into the wiki and the Settings card ("anyone on your network can
   still use the web interface; the API key only protects the Automation API"), then treat **"Protect the web interface"** (a username and password, off by default, like Sonarr's) as the next
   security item: it is the thing that actually closes the `PUT /api/settings` hole. Not in rc.3 unless the user wants it; it needs a login page, session cookie, and care for the tray
   app's own browser window. Related and smaller: stop returning stored secrets (Sonarr/Radarr/TMDb/OMDb keys, webhook URLs) from `GET /api/settings`, showing "saved" instead.
6. **Transport:** HTTP on the LAN, so the key travels in the clear; the docs recommend a reverse proxy with TLS for anything beyond a trusted LAN. No HTTPS built in for rc.3.
7. **OpenAPI:** a document for `/api/v1` only, built with .NET 10's built-in OpenAPI support and served at `/api/v1/openapi.json` (no key needed to read the description). Shipped
   alongside a hand-written wiki page, so people can read it without tooling.

## The v1 surface (first cut)

Read (`read` scope):

| Endpoint | What it returns |
|---|---|
| `GET /api/v1/status` | running / paused / monitoring, current lane and file, preset, progress, fps, ETA, queue counts and ETA, schedule state, hold, version |
| `GET /api/v1/queue?lane=&status=` | the In Queue list in processing order: lane, file name, position, status (pending, skipped, error, held), preset, destination lane |
| `GET /api/v1/runs?from=&to=&page=&pageSize=` | runs from the database: number, ended, duration, files, before/after GB, errors, warnings, movies/TV, avg fps, engine split |
| `GET /api/v1/runs/{id}` | one run with its per-file rows (name, lane, engine, profile, sizes, time, fps, error code and reason) |
| `GET /api/v1/stats?period=all\|year\|d30\|d7\|today` | the Overview figures from the daily totals: runs, files, saved GB, time, movie/TV, lane / engine / profile breakdowns |
| `GET /api/v1/lanes` | lane id, name, enabled, preset names, queued count (no folder paths unless `?paths=1` with a `control` key) |
| `GET /api/v1/schedule` | the schedule settings and its current state |
| `GET /api/v1/reports/{id}` | the report HTML (same as `/api/reports/{id}`), for a notification or card link |

Control (`control` scope):

| Endpoint | What it does |
|---|---|
| `POST /api/v1/run` | run one pass now (the Monitor's Run Now; does not start monitoring) |
| `POST /api/v1/monitoring/start` and `/stop` | start or stop watching the lanes |
| `POST /api/v1/pause` and `/resume` | pause or resume the file in flight |
| `POST /api/v1/hold`, `DELETE /api/v1/hold` | the external hold (Decision 4) |
| `POST /api/v1/schedule/run-anyway` | lift the off-hours hold until the next window change |
| `PATCH /api/v1/schedule` | change the schedule settings |

Deliberately **not** in v1: aborting an encode (destructive; add later with a confirm parameter), editing settings, lanes or profiles, queue edits (skip, reorder, remove: the next release,
since the queue is a table now), maintenance buttons, backup/restore.

## Home Assistant

The wiki's *Home Assistant Integration* page is rewritten around the key: `secrets.yaml` holds the key; the REST sensor sends `X-Api-Key`; `rest_command` entries for `hold`, `release`,
`run now`, `pause`, `resume`; an example automation (hold while the media player is playing, release when it stops, with `until` as a safety net); a `number` helper that sets the window
through `PATCH /api/v1/schedule`. A custom HACS integration is parked: REST sensors and commands cover it.

## Build stages

0. **BUILT 2026-10-25 (uncommitted).** Differences from the text below: state database v2 adds only the `kv` table; `api_keys` and `holds` arrive in their own stages as v3 and v4 (no tables before
   their columns are settled). `IAppState` (`SqliteAppState`) holds the values; the legacy properties stay on the config classes, nullable and not written when null, so an older file can be read once;
   `AdoptLegacyValues()` runs at start-up before the schedulers and after a backup restore; a settings import drops them. If the database cannot be opened the values live in memory for the session.
   Original text: **Move the app-written values out of the settings file** (user, 2026-10-25: small, finishes the queue-database idea, and stops the scheduler rewriting the user's settings). State database v2: a
   `kv` table, so the values the app writes into `compressarr.settings.json` today (`HistoryViewedThroughRunNumber`, the backup's `LastRunUtc`, the four digest-sent dates) live in
   `compressarr.state.db` instead (copied once from the settings file, then no longer written there; a restored older backup that still carries them is picked up once). The same
   migration adds `api_keys` and `holds`. Ships with rc.3 (rc.1 and rc.2 stay local).
1. **Keys:** key store, endpoint filter for the `/api/v1` group (scope check, constant-time compare, throttle), the Settings card (create, copy once, revoke), tests.
   **Superseded 2026-10-25 by the two fixed Sonarr-style keys (docs/design-login.md, "option A"): the named, hashed keys described next were replaced in the login work; the filter, throttle and scope
   rules below still stand.** Originally **BUILT 2026-10-25.** State database v3 adds `api_keys` (id, name, scope, hash, prefix, created_at, last_used_at; revoking deletes the row). `IApiKeyStore` / `SqliteApiKeyStore`
   (Core/Api), key format `cmpk_` plus 43 URL-safe characters, SHA-256 of the key looked up by hash (the key is 256 random bits, so a plain indexed lookup leaks nothing useful), at most 20 keys,
   `last_used_at` written at most once a minute. Web: `ApiKeyFilter` (X-Api-Key or Bearer; 401 `no_keys`/`missing_key`/`invalid_key`, 403 `insufficient_scope`, 429 `too_many_attempts` with
   Retry-After) on the `/api/v1` group, scope marked per endpoint with `.RequireApiScope(...)` (unmarked = read), `ApiKeyThrottle` (five wrong keys from an address within a minute lock it for a
   minute), `GET /api/v1/whoami`, and the UI's own `GET/POST/DELETE /api/automation/keys` (not under /api/v1: a key cannot make keys). If the state database cannot be opened nothing is accepted.
   `X-Api-Key` and bearer are both accepted; missing keys are not counted as failures, only wrong ones.
2. **Read endpoints** and their DTOs, new query methods on the archive (runs list and detail, stats), golden-JSON contract tests so a field cannot be renamed by accident.
   **BUILT 2026-10-25 (uncommitted).** `IRunQueries` (Core/Logging, implemented by `SqliteRunHistoryStore`): `ListRuns` (kind = run, newest first, day window, paged, count), `GetRun` (lanes + files; the file's long
   detail log is deliberately not exposed), `GetStats` (sums `daily_totals`, lane and profile breakdowns from `daily_lanes`/`daily_profiles`). Web: `status`, `queue`, `lanes`, `schedule`, `runs`, `runs/{id}`,
   `stats`, `reports/{id}` in `ApiV1Endpoints.cs`, each response built field by field there (never a UI DTO). Decisions made while building: times are server-local with their UTC offset
   (`2026-10-25T09:30:00-04:00`; a run imported from before 3.0 may only know the day, then 00:00:00); `?period=` takes the History page's windows (today, d7, d30, year, all) and `?from=`/`?to=` (yyyy-MM-dd) win;
   pages: `page`/`pageSize` with the body carrying `total`, `page`, `pageSize` (runs default 25, max 200; queue default 100, max 500); file names only in the queue (no paths); `stats` counts only runs that
   produced files, as the History page does, while `runs` also lists all-failed runs with `counted: false`; a CSV-fallback history answers 503 `history_unavailable`; the status queue ETA is `etaState`
   (`none`, `estimating`, `projected`) plus `eta`. The `hold` field joins `status` and `schedule` in stage 3. `ApiV1ReadEndpointTests` lists every field name of every response.
3. **Control endpoints,** the external hold in the engine and `EncodeSchedule` (including with the schedule disabled), the Monitor banner and Release button, schedule `PATCH`.
   **BUILT 2026-10-25 (uncommitted).** State database v4 adds `holds` (one row, id 1). `IExternalHold` / `SqliteExternalHold` (Core/Scheduling; in-memory if the database cannot be opened; expiry is
   checked on every read; placing it again keeps `since`). `ScheduleStatus` gains `Hold`; `IsHeld` is "scheduled hold or external hold", computed even when the schedule is off. `RunAnyway` lifts only the
   schedule's hold, but also thaws a file an external hold froze for as long as that hold stands (so a manual Resume and a Stop Monitoring request, which both call it, cannot be defeated by the next
   tick or leave Stop waiting on a frozen file). `RunOrchestrator` logs "held by automation (reason) since ... until ..." once per hold. `status.hold` and `GET /hold`; `status.queue.etaState` is
   `held` (eta null) while a hold stands. Control calls (`run`, `monitoring/start|stop`, `pause`, `resume`, `hold` POST/DELETE, `schedule/run-anyway`, `PATCH schedule`) need the control scope and
   write one run-log line naming the key. `run` answers 202 when a pass started (with `held` so the caller knows), `monitoring/stop` answers at once (202; status shows `stopping`), `pause` with no encode is
   409 `not_running`. `until`: ISO 8601, no offset means the server's local time, must be in the future and within a year; `reason` at most 200 characters. `PATCH schedule` takes only the schedule's
   own fields, merges over the saved ones, validates the result as if the schedule were on (a time like 25:00 is refused even while it is off) and saves nothing on any problem. The web UI's own
   `POST /api/run/release-hold` backs the Monitor's Release hold button; nav.js, monitor.js and scheduler.js show the hold.
3b. **Protect the web interface** (decided 2026-10-25, in rc.3). **Revised 2026-10-25 at the user's request to work the way Sonarr and Radarr do:** a Security section with Authentication (None / Forms / Basic),
   "Authentication Required" (Enabled, or Disabled for Local Addresses, the default) where "local" means the local network (private, loopback and link-local ranges, decided from the TCP peer address only,
   never a header), a login only for external addresses, and the API key(s) in that same section. The detailed design is `docs/design-login.md` (it also covers the masked secrets and redacted exports
   below, Allowed Hosts, an always-on cross-site check, and the lessons of Sonarr's CVE-2026-30975), with the login in state database v5 so the secrets table of 3c becomes v6. The text below is the first
   draft and is superseded where the two differ: optional username and password (off by default so nothing changes for an existing install), a login page and session cookie for the browser, the
   tray app's own browser window and the notification links still working, `/api/v1` unaffected (it uses keys), the legacy open `/api/run/status` either kept open or covered by a switch (to decide when
   designing this stage). **Secrets leave the open endpoints in this stage (decided 2026-10-25):** the Sonarr/Radarr/TMDb/OMDb keys and the notification channels' secret fields (the ones their
   `NotifierField` marks `Secret: true`: webhook URLs, tokens, user keys) are no longer returned by `GET /api/settings` and `GET /api/notifications/channels`; the page shows "saved" for a field that is set,
   and a save replaces a secret only when a new value is typed (a blank box keeps it). The settings **export** redacts them by default, with an "Include keys and passwords" tick-box that is off unless
   chosen; backups keep them (they exist to restore the same install). Needs its own short design note before it is built (cookie lifetime,
   password storage with a slow hash, lockout, reverse-proxy and HTTP caveats, recovery if the password is forgotten: a start-up switch or deleting a row in the state database).
3c. **Secrets move to the state database** (decided 2026-10-25, after 3b): an `ISecretStore` over a `secrets` table (schema v6, after the login's v5) holds the Sonarr/Radarr/TMDb/OMDb keys and the notification channels'
   secret fields, so `compressarr.settings.json` carries none and is safe to share or attach to a support request. About 24 places read the Arr/poster keys and 5 read channel settings; they go through
   the store. Same pattern as stage 0: the first start moves what an older file holds (and removes it), a restored older backup does the same, an import drops them unless the export included them. The
   values stay readable (Compressarr must send them), so this is hygiene against accidental sharing, not encryption: against anything running as the user nothing changes, and backups still carry them. No
   encryption at rest (DPAPI): it adds almost nothing and breaks moving a profile between machines.
4. **Docs and spec:** OpenAPI document, wiki *API and Automation* page, the rewritten Home Assistant page, README, test plan, CHANGELOG, a short note in the Utilities README.
5. **After rc.3, once the queue database has had a few weeks in production:** simplify the queue code (user, 2026-10-25). Replace the diff-and-merge code (`ResumeResults.SaveChanges`,
   `QueueRules.MergeUserEdits`, the reflection over every field) with plain row updates (`UPDATE ... WHERE lane_id = ? AND file = ?`), and have the Monitor ask for rows (a page of the queue, a
   count per lane) instead of the whole list on every poll. A cleanup with some risk, so it waits for real-world evidence that the database store behaves.
6. **Later (not rc.3):** queue edits over the API, push events (server-sent events or webhooks beyond the notification channels), "Protect the web interface", secrets out of
   `GET /api/settings`, a HACS integration.

## Risks

- The API is a promise: v1 is additive-only from the day it ships, so the first cut stays small and the contract tests are not optional.
- An automation that holds the queue and never releases it: the persisted hold, the visible banner, the `until` field and the docs are the mitigation.
- Hash-only keys cannot be shown again: a lost key is revoked and replaced (said on the card).
- Moving app-written values out of settings (stage 0) touches the scheduler and the digest code paths; it ships with tests that the values survive the move and that a restore of an old backup
  (values still in its settings file) is picked up once.
