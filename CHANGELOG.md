# Changelog

All notable changes to Compressarr are documented in this file. Pre-release/RC builds leading up
to v1.0.0 are omitted here - see [GitHub Releases](https://github.com/Compressarr/Compressarr/releases)
for that full history.

> [!NOTE]
> Compressarr contains no AI or machine learning at runtime. Movie/TV detection, file matching,
> and renaming all run on plain, inspectable regex pattern matching - the same deterministic
> logic every time, nothing generative involved.

## [3.0.0] - Unreleased

> [!TIP]
> **What's new in 3.0, compared with 2.1.10 (the last 2.x release):** a second encoder (ffmpeg) and a profile editor, Passthrough (all of Compressarr's filing without a re-encode),
> a day/night Scheduler, lane assignment, a run history and queue that live in databases, a redesigned History page with stat cards and charts, a web sign-in and an
> Automation API for Home Assistant and scripts, posters and Plex editions, and a TV / movie / video icon on every queued file. The pre-release entries below (3.0.0-rc.3 and the
> 2.2 betas and release candidates) hold the full detail of each change.

> [!IMPORTANT]
> **Behaviour change: anyone outside your own network now needs a login.** Until now the web interface - which can change every setting, including the command run after each pass - answered
> any device that could reach its port. From 3.0 it works the way Sonarr and Radarr do: **on your own network and on the computer Compressarr runs on nothing changes, you are never asked for a
> password**; a request from outside is refused until you set a username and password in **Settings > Security** (the refusal says so). If you reach Compressarr from outside your home - a forwarded port,
> a reverse proxy, Home Assistant on another site - set them now. Over Tailscale (or any `100.64.0.0/10` address) tick the box in Settings > Security to count it as local. A request that carries proxy
> headers (`X-Forwarded-For` and similar) always counts as outside, so behind a reverse proxy choose **Authentication Required: Enabled**. Home Assistant and scripts use the API keys, which are
> separate from the login.

> [!NOTE]
> **Upgrading from 2.1:** install over it. Your settings, lanes and queue carry over, and **the first start of 3.0 moves your run history into a database by itself** (every run is imported and checked
> against the old file, which is kept as `Compressarr_History.csv.imported`). 3.0 cannot be undone by installing 2.1 again: 2.1 cannot read the new databases, so runs recorded by 3.0 would not be
> in the old file. Take a backup (Settings > Backup & Restore) before upgrading if you want a way back.

### Encoding
- **ffmpeg as a second encoder (experimental), per lane.** A lane's **Encoder** is HandBrake (the default) or ffmpeg, with its own built-in profiles (including AV1, HEVC and NVIDIA hardware encoders). After an ffmpeg encode the finished file's length is verified (ERROR 111), Dolby Vision / HDR10+ files are never silently flattened (ERROR 112), and a lane whose encoder is not installed is skipped with a clear report entry (ERROR 113). Compressarr can download ffmpeg for you (Stable or Latest channel) and checks what the machine can do, including AV1 on an NVIDIA RTX 40-series GPU.
- **An Encoder page and a Profiles page.** The HandBrake settings moved to **Encoder** (HandBrakeCLI and ffmpeg paths, Check / Install, extra options, Browse buttons). **Profiles** lists every profile for both encoders, with built-in profiles locked and your own marked "user", and a **profile editor** (container, encoder, quality, speed, tune, frame rate, deinterlace, crop, audio rules, subtitles and chapters, with the exact HandBrake command shown beside it; anything the editor does not show is kept exactly as stored). Duplicate, rename and delete follow clear rules, and profiles travel in backups and exports. Compressarr now always encodes with its own profiles; the first start copies the ones your lanes and queue used from HandBrake's `presets.json`.
- **A preset catalog.** **Profiles > Import > From Catalog** installs additional profiles published at github.com/Compressarr/Compressarr-Presets without waiting for a new Compressarr release.
- **Passthrough (no re-encode).** The first choice in every profile dropdown (a lane's, or a single queued file's): the file is copied instead of encoded, and everything else still happens - filing into your TV / Movie folders, FileBot renaming, source deleted, recycled or kept, companion files, Sonarr / Radarr told, title tag cleared. It needs no encoder at all, shows a teal badge, and is counted in History without its bytes so the savings figures stay true.
- **HDR is protected.** A HandBrake profile that would lose a file's HDR (H.264, or an encoder that cannot carry it) no longer re-encodes it, an ffmpeg profile can be told to refuse, hand the file to a HandBrake profile, or encode it anyway without Dolby Vision / HDR10+, and the check looks at which kind of HDR a profile keeps.

### Lanes, queue and scheduling
- **Scheduler (off by default).** An optional day/night schedule: choose when daytime is (every day the same, weekdays and weekends, or each day separately), what priority the encode runs at in the day and at night, whether to encode only off-hours, and what happens when the day starts. **Run anyway** overrides it for now.
- **Lane assignment.** Every queued file has a **Lands in** choice: finish the file normally but file it in another lane's library. The Monitor says where the file will land and the report flags redirected files.
- **Lanes:** a **Duplicate** button on every lane card; a new lane starts with a working profile (Compressarr SD-HD); two enabled lanes cannot watch the same Input folder; the library folders are checked like Input and are never created for you (a renamed or missing library is reported, not quietly recreated); a file that FileBot renames keeps its place in the queue.
- **The queue is a database.** What Compressarr remembers between passes (order, skips, per-file profiles, held failures, kept sources, moves waiting to be retried) is stored in `compressarr.state.db` with one transaction per change, so a crash or two simultaneous edits cannot damage it, and a damaged file no longer stops the Monitor. A failed or HDR-refused file stays held in the queue instead of being retried on every pass.
- **Monitor:** **Run Now** works with monitoring on or off; **View Log** opens a copyable snapshot of the live log; the status line says where the file will land; every queued file shows a **TV**, **film strip** or **camera** icon (a camera for a video that is neither an episode nor a movie with a year; such a file is handled as a movie and recorded as "Video" in reports); the Home Assistant file count can no longer read "65 of 0".

### History and reports
- **The run history is a database and reports are built when you open them.** Every run and every file in it is kept in `compressarr.history.db`, with daily totals updated in the same step, so History loads fast however long it has run, and a backup carries every report. A report's link never goes dead, and **Export CSV** gives the history back in the old layout. A copy of each report as a file is optional.
- **A redesigned History page.** The Overview has a period selector (All time, Last year, Last 30 days, Last 7 days, Today), stat cards you can choose and rearrange (Total Runs, Files Compressed with a movie / TV split, Best Month, Time Spent Encoding, Clean Runs, Space Saved, Average Speed, Busiest Lane, HandBrake vs ffmpeg, Passed Through, Most Used Profile and more), charts, and **History at a glance** (a space-saved ring, space saved per week, the last 48 runs). The Reports card has period buttons, pages, and a table of runs with errors or warnings you have not seen yet.
- **More is recorded:** each run's movie and TV counts, each file's lane, tool, profile and average speed, numbered warnings (WARN 201-205) next to the numbered errors, and a machine name for notifications from more than one computer. Failed encodes no longer count toward the totals.
- **Settings > Logs and Reports** are separate cards, each saying what it does; Purge Logs & Reports no longer deletes the history.

### Security and automation
- **A web sign-in (Settings > Security).** Forms (a sign-in page) or Basic, with **Authentication Required** set to Enabled, Disabled for Local Addresses (the default) or Disabled for Localhost, an optional **Allowed Hosts** list, a lock-out after repeated wrong passwords, and a way back from a forgotten password (a tray menu entry, or `COMPRESSARR_RESET_LOGIN=1`).
- **Keys, tokens and passwords stay out of the browser and out of the settings file.** Saved keys show as dots and are never sent back; they live in the state database; **Export config** leaves them out unless you tick **Include API keys and passwords**; backups carry everything so a restore brings every integration back.
- **The Automation API (`/api/v1`).** Two keys on Settings > Security (an **API Key** that can act and a **Read-only API Key**), with calls for status, the queue, lanes, the schedule, runs, statistics and reports, and control calls to run a pass, start or stop monitoring, pause, resume, change the schedule and **hold the queue** (optionally freezing the current file) - made for Home Assistant. Field names are fixed by tests, so an update cannot rename one under your automation. The wiki has an API and Automation reference and a rewritten Home Assistant page.
- **The settings file stays still:** values the app itself records (last backup, digest dates, how far you viewed History) moved to the state database, so Compressarr no longer rewrites your settings file as it works.

### Integrations, notifications and posters
- **Posters on the Monitor queue** (optional, off by default) from TMDb or OMDb with your own free key, one source tried first and the other as a backup if you give both keys. Only the title and year are ever sent.
- **Plex editions** (`{edition-Name}`): movies share one `Title (Year)` folder with the tag kept in the file name, TV editions get their own show folder as Plex expects, and tags are never searched for an episode number or sent to a poster source.
- **Notifications:** `{error}`, `{warning}`, `{movie_count}`, `{tv_count}` and `{avg_fps}` tokens for Custom messages, `{report_path}` is now a web address that opens from a phone, and a channel card shows its own settings first. A note (and a one-click fix) for Radarr's "Delete empty folders" setting.
- **Announcements:** a bell in the top bar for project news, from a public repository.

### Settings, backups and installer
- **Backups can be downloaded**; backups and exports include your own profiles and the databases; importing or restoring a wrong or damaged file is refused in plain words and changes nothing. `COMPRESSARR_DATA_DIR` points Compressarr at another data folder.
- **The top bar:** icon-over-label buttons, readable and red-for-error messages, an **Advanced** button that hides the destructive Maintenance buttons, and on/off switches that look like the lane's Enabled toggle.
- **A start-up window** appears at once while Compressarr gets ready, and **GitHub being unreachable** gives plain messages instead of system text.
- **The installer:** the desktop shortcut is ticked by default and an upgrade keeps your choice; Windows' "run at login" entry follows restores and is removed on uninstall; uninstalling or upgrading while Compressarr runs closes it first; the license ships with the program; and **every program file and the uninstaller are signed**, so Windows 11 Smart App Control no longer blocks Compressarr itself (the HandBrakeCLI and ffmpeg it downloads are third-party and unsigned; a one-time notice about them may appear the first time Compressarr starts after an installation).

### Fixed
- A library folder that no longer exists is not recreated, and a lane's destination is checked like Input.
- "Run at login" follows an imported or restored configuration; a Lanes field no longer stays red once it holds a valid value; the Extra CLI options example no longer suggests an option HandBrake rejects.
- An ffmpeg Check / Install now says so when the file is not ffmpeg, and gives up after 8 seconds instead of 30; custom HandBrake presets a lane uses are never missed, even after a restore or import.
- A queue change could be wiped by a retry or the start of a pass; a deleted lane's old entries are cleaned up quietly.
- E-AC3 is reported honestly as 5.1; HDR10+ files are described as HDR10+; ffmpeg lanes give clearer messages for damaged files.

### Compatibility
- 3.0 reads a 2.1 history file (found in the Logs folder) and keeps it as `.imported`; a 2.1 install cannot read 3.0's databases (see the note at the top).
- The 2.x line is retired with 2.1.10. The pre-releases 2.2.0-beta.1 to 2.2.0-rc.2 were never released as a final 2.2, and everything in them is in 3.0.

## [3.0.0-rc.3] - 2026-10-29

> The first published 3.0 build. 3.0.0-rc.1 and rc.2 were local test builds that were never released; everything in them is in this entry.

> [!IMPORTANT]
> **Behaviour change: anyone outside your own network now needs a login.** Until now the web interface - which can change every setting, including the command run after each pass - answered
> any device that could reach its port. From 3.0 it works the way Sonarr and Radarr do: **on your own network and on the computer Compressarr runs on nothing changes, you are never asked for a
> password**; a request from outside is refused until you set a username and password in **Settings > Security** (the refusal says so). If you reach Compressarr from outside your home - a forwarded port,
> a reverse proxy, Home Assistant on another site - set them now. Over Tailscale (or any `100.64.0.0/10` address) tick the box in Settings > Security to count it as local. A request that carries proxy
> headers (`X-Forwarded-For` and similar) always counts as outside, so behind a reverse proxy choose **Authentication Required: Enabled**. Home Assistant and scripts use the API keys, which are
> separate from the login (see below). The default for a new install is "Forms" with "Disabled for Local Addresses", the same defaults as Sonarr.

> [!NOTE]
> **This is 3.0, not 2.2.** The work done as 2.2 grew into a new generation: a second encoder, Passthrough, per-lane profiles, posters, Plex editions, a
> redesigned History page, and now a new storage format - the run history moves into a database that 2.x cannot read. Nothing of 2.2 was released as a
> final version; 2.2.0-rc.1 and 2.2.0-rc.2 stay as the pre-releases they were. **2.1.10 is the last 2.x release:** the 2.x line is retired once 3.0 is
> released, and 3.0 takes over a 2.1 install's history by itself (it finds the old history file in the Logs folder and imports it).
>
> Changes since [2.2.0-rc.2](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-rc.2)
> only - everything else is in the entries below.

### Added
- **The stat cards at the top of the History page can be rearranged.** Hover a card and drag it by the dots on its top edge (or focus the dots and press the arrow keys) to put the stats in the order you like; the other cards, the period table and "History at a glance" stay where they are. The order is remembered in the browser next to which stats are shown, a stat you add from the Stats menu goes last, and "Reset to defaults" restores the original order as well.
- **Sign-in (Settings > Security).** **Authentication** is **None**, **Forms (Login Page)** (a sign-in page with Remember me; a sign-in lasts 7 days from when you last used it) or **Basic (Browser Popup)**. **Authentication Required** is **Enabled** (everyone, this computer too), **Disabled for Local Addresses** (the default) or **Disabled for Localhost**. Local means `127.0.0.1`/`::1`, `10.x`, `172.16-31.x`, `192.168.x` and link-local addresses; the decision is made from the connection's real address and ignores anything a request claims about itself. The account is a username and a password of at least 8 characters, kept as a salted PBKDF2 hash in the state database (not in the settings file, not in the settings export; backups carry it). Five wrong passwords lock an address out for five minutes. **Sign out everywhere** ends every browser's sign-in, and changing the account from outside your network asks for the current password. Every page sends you to the sign-in page when a sign-in ends and brings you back to where you were, and a **Sign out** button appears in the toolbar for browsers that signed in.
- **Allowed Hosts (Settings > Host).** An optional list of the names and addresses Compressarr answers to (for example `compressarr, 192.168.0.100`); a request for any other name gets a 400 answer, which stops a web page that points its own name at your network address. Empty means any; `localhost` and `127.0.0.1` always work, so you cannot lock yourself out of the computer. The Web UI card is now **Host** (Port, Allowed Hosts, Install as app).
- **A way back from a forgotten password.** From your own network you can simply type a new one in Settings > Security. If you chose "Enabled" and are locked out, the tray menu has **Turn off web login...** (it puts the setting back to "Disabled for Local Addresses" after you confirm), and starting Compressarr once with `COMPRESSARR_RESET_LOGIN=1` does the same for a container or a service. The Troubleshooting page on the wiki walks through it.
- **Saved keys, tokens and passwords are never sent back to the browser.** A Sonarr, Radarr, TMDb or OMDb key, a notification channel's tokens and webhook addresses (Discord and Slack webhook URLs and the generic Webhook's URL and custom headers are now treated as secrets too), and the web password show as dots once saved. Leave the dots to keep the value, type to replace it, empty the box and Save to remove it. **Test** buttons use the saved value when the box still shows the dots, whatever address is typed, so a machine that got a new address from DHCP can be tested without retyping its key.
- **Export config leaves secrets out** (a file you may send to someone for help); tick **Include API keys and passwords** in Settings > Backup & Restore for a full copy. Importing a file without keys keeps the ones the installation has. Backups are unchanged and complete: they carry the settings, the state database (with the Compressarr API keys and the web login) and the history, so a restore brings every integration back working.
- **Automation API keys (the first piece of the Automation API).** The **Security** card on Settings has an **Automation API** section with two keys, as Sonarr and Radarr show theirs: an **API Key** (can read everything and act) and a **Read-only API Key** (for dashboards). Each is shown in full, always, with **Copy** and **Regenerate** (the old key stops working at once). Calls go to addresses starting `/api/v1` with a key in an `X-Api-Key` header (or `Authorization: Bearer`); `GET /api/v1/whoami` says which key it is and which version answered. There is no anonymous mode, wrong keys are slowed down (five wrong tries lock that address out for a minute), and a Read-only key is refused on anything that acts. The keys work from any address and are separate from the web login; they are kept in the state database (not in the settings file or the settings export) and are included in backups, so a restore brings back the keys your automation already has. The wiki has a new API and Automation reference page listing every call, field and example, and the Home Assistant page is rewritten around the keys.
- **Automation API: the read calls.** With a key (Read access is enough): `GET /api/v1/status` (what is running, paused or monitoring, the current file with progress and speed, the queue's size and projected finish, the schedule), `/queue` (the In Queue list in processing order, filterable by lane and state, paged), `/lanes` (each lane with what waits in it), `/schedule`, `/runs` (the runs, newest first, by period or from/to day, paged), `/runs/{id}` (one run with its lanes and every file: sizes, time, speed, engine, profile, error code and reason), `/stats?period=` (the Overview's figures for today, 7 days, 30 days, a year or all time, with the lane, tool and profile breakdowns) and `/reports/{id}` (the report page). Every field name is fixed by tests, so an update cannot rename one under your automation; new fields may be added. Times carry the server's UTC offset. The control calls (run now, pause, hold the queue, change the schedule) are described next.
- **Automation API: control calls, and a hold for Home Assistant.** With a Control key: `POST /api/v1/hold` holds the queue (no new file starts) with an optional reason and end time, and an option to also freeze the file being encoded (otherwise it finishes); `DELETE /api/v1/hold` releases it. The hold is saved, so a restart during movie night keeps it; it lapses by itself at its end time; placing it again just updates it. It works whether or not the Scheduler is switched on, "Run anyway" does not lift it, and it shows everywhere: `GET /api/v1/status` and `/hold`, the Monitor (State: Held, a note with the reason and a **Release hold** button), the top bar ("Held by automation") and the run log. Also `POST /run` (run one pass now), `/monitoring/start` and `/stop`, `/pause` and `/resume`, `/schedule/run-anyway`, and `PATCH /schedule` to change the schedule settings (only the fields you send; a schedule that would be invalid is refused and nothing is saved). Every action is written to the run log with the name of the key that asked. A Read key is refused (403) on all of these.
- **A film strip, a TV or a camera beside every queued file.** The Monitor page now shows what kind of file Compressarr thinks each one is, just left of its name in the queue and in the top right corner of the Current status card while it is being compressed: a TV when the name has a season and episode (`S02E07`, `2x07`), a film strip when it has a `(year)` like `Inception (2010)`, and a camera for any other video (a home video, a clip, anything that is not a standard film or show). A camera file is still handled like a movie (the lane's movie preset, filed in the lane's Movie folder), so a TV episode with an unusual name that shows a camera is the reason it got the movie preset. The report, the run log and the History archive call such a file "Video" (new runs only; earlier runs keep "Movie"), and History, the notification `{movie_count}` and the run totals count it with the movies, so movies plus TV still add up to the files. The Automation API reports it too: `type` (`"tv"`, `"movie"` or `"video"`) on each `/api/v1/queue` item and on `current` in `/api/v1/status`.
- **The run history is now a database, and reports are built when you open them.** Every run is recorded in `compressarr.history.db` (SQLite, in Compressarr's data folder) instead of `Compressarr_History.csv`: the run, each file in it (name, type, size before and after, time, tool, lane, profile, speed, status, warnings), and running totals per day that are updated in the same step, so the History figures are a few small rows added up instead of the whole history re-read on every page load. A database cannot be left half-written by a crash the way a text file could, and it can grow new fields without breaking anything. **The first start of 3.0 moves your history over by itself:** every run is imported and checked against the old file (runs, files, sizes, time must add up), the old CSV is kept next to it as `Compressarr_History.csv.imported`, and reports that were still saved from earlier versions are kept exactly as they were and read for what the old history never recorded (movie / TV split, and which lane, tool and profile handled each file). If anything goes wrong the old file stays in use and nothing is removed.
- **No more HTML file per run.** The report is built from the stored rows when you open it (from the History page, after a run, or from the link in a notification), so its link never goes dead when old files are cleaned up and a backup carries every report. Settings > Logging & Reports has **Also save a copy of each report as a file** (off by default) for anyone who wants the files, and the folder setting is now the folder for those copies. The report page can be saved as a file from your browser as before; adding `?download=1` to its address downloads it.
- **Export CSV on the History page** downloads the whole history in the old CSV layout, for a spreadsheet or a script.
- **The History page's Reports card has period buttons, pages, and a place for errors you have not seen.** The buttons are the Overview's - **All time**, **Last year**, **Last 30 days**, **Last 7 days** and **Today** (the default) - so the list no longer grows without end, and a long period is shown 25 reports to a page with page buttons below and a **Rows per page** box (25, 50 or 100); the choices are remembered in the browser. Above the list, a red-edged table shows the runs with errors or warnings that you have not looked at yet (the ones the sidebar's History badges count), and opening the page counts as having seen them, which clears the badges. **View recent errors**, at the right of the card's title bar, shows the latest 50 runs that had an error or a warning, seen or not, in the same place, in case one was missed.
- **Backup and restore carry the database** (a consistent copy made with SQLite's own backup, correct even while Compressarr is running), so the history, its reports and the run counter travel together. Restoring a backup made by an earlier version imports its CSV the same way.
- **`COMPRESSARR_DATA_DIR`**: an environment variable that makes Compressarr use another folder for its settings and history (a portable copy, a container, or a trial run against a copy of your data).
- **The History page Overview has stat cards, a period selector and charts.** Next to **Overview** choose **All time** (the default), **Last year**, **Last 30 days**, **Last 7 days** or **Today**: the stat cards below follow the choice, and the matching row of the period table is highlighted (click a row to choose it too). The five default cards are **Total Runs** (for **Today** its chart shows the last 24 hours as eight three-hour blocks, 12-3 AM, 3-6 AM and so on, with the current block last), **Files Compressed** (with a movie / TV split bar), **Best Month** (it becomes Best Week, Best Day or Best Run as the period gets shorter), **Time Spent Encoding** and **Clean Runs** (a green / orange / red bar of clean runs, runs with warnings and runs with errors). The new **Stats** button on the toolbar adds more: **Space Saved**, **Movies vs TV**, **Average Saved / File**, **Biggest Run**, **Active Days** and **Redirected Files**. The choice is remembered in the browser, and the cards always fill their rows evenly. A second group, **History at a glance**, always shows the whole history: a space-saved ring, space saved per week, and a ribbon of the last 48 runs (green, orange or red, each linking to its report). Runs in which every file failed and nothing was produced are left out of the cards, as they always were from the period totals.
- **Compressarr now records a rough average speed (frames per second) for every file it encodes.** It is HandBrake's own running average or ffmpeg's last reading, as one combined figure. It shows as an **Average speed** tile on the report (and as a tooltip on each file's duration), as a new **Average Speed** card on the History page's Stats menu, and as the Custom notification token `{avg_fps}`. A file that was passed through, failed or gave no reading has no speed, and a run with none shows nothing (never 0); runs from before this have none.
- **The History page now also records which lane handled each file, which tool produced it (HandBrake, ffmpeg or passed through) and which profile.** Four new cards on the Stats menu, all off by default: **Busiest Lane**, **HandBrake vs ffmpeg**, **Passed Through** and **Most Used Profile**. The facts are stored as six new columns at the end of `Compressarr_History.csv` (older builds ignore them; older runs show as "not recorded" in these cards, never guessed).
- **Compressarr now records how many movies and how many TV episodes each run compressed.** The History file gets two new columns, `MovieCount` and `TvCount` (appended at the end, so older builds ignore them; a run from before this change shows as "not split" rather than as 0). **Custom notification messages** can use them as `{movie_count}` and `{tv_count}`; like `{files}` they count every file in the run, so the two add up to `{files}`. None of the built-in message styles use them.
- **An `{error}` token for Custom notification messages.** A Custom message template (Notifications > Message Format) can now include `{error}`: a few words about what went wrong in the run, such as `Error 102: File move failure`. The same error on several files adds a count (`Error 102: File move failure (x3)`), and several kinds show the first two and then "(+N more)". A lane that could not run counts too, and a failure with no numbered code reads `Error: see the report`. When the run had no errors the token is nothing at all, and in a template of several lines the whole line it is on is left out, so `Problem: {error}` leaves no dangling "Problem:". The built-in styles are unchanged.
- **Numbered warnings, and a `{warning}` token.** A file that converted and was filed, but with something to note, now has numbered warnings in their own series, **WARN 201 to 205** (errors stay **ERROR 101 to 114**, so no number means two things): 201 move skipped (the destination already exists and the collision setting is Skip), 202 companion files not moved, 203 Sonarr/Radarr unmonitor failed, 204 source-folder cleanup deferred (rescan not confirmed), 205 source-folder cleanup deferred (the cleanup failed). The HTML report (and so the History page) shows each as a `WARN 20x` badge with a help bubble, like an error's code, with the specifics (a path, the reason) still beside it. A Custom notification template can use `{warning}` the same way as `{error}`, such as `Warn 201: Move skipped (already exists)`; it is nothing, and its line is left out, when the run had no warnings.

### Changed
- **Keys, tokens and webhook addresses moved out of the settings file.** The Sonarr, Radarr, TMDb and OMDb keys and each notification channel's tokens, webhook addresses and custom headers are now kept in the state database (`compressarr.state.db`), not in `compressarr.settings.json`, so that file can be shared, attached to a support request or kept in a repository without leaking them. Nothing changes on screen. The first start after upgrading moves what an older settings file holds (and empties it there); restoring a backup made by an earlier version does the same; if the database cannot be written the keys simply stay in the file, so none is ever lost. They are still stored readably (Compressarr has to send them), so this guards against accidental sharing rather than against someone using your account, and backups, which exist to restore the same installation, still carry them.
- **Compressarr no longer rewrites your settings file as it works.** The values the app itself records - when the last backup finished, the day each digest was last sent (so it goes out once a day), and how far you had viewed on the History page (the sidebar badges) - moved from `compressarr.settings.json` into the state database, `compressarr.state.db`. Before, every digest and backup rewrote the settings file (and put those values into every settings export and into support files people shared). The first start moves whatever an older settings file still holds and takes it out of the file; restoring an older backup does the same; importing an export from another computer drops them (its "digest already sent today" must not silence this one). Nothing changes on screen: the Settings page still shows the last backup.
- **Settings > Logging & Reports is now two cards, Logs and Reports, and each says what it does.** *Logs* has the log folder, **Delete logs after (days)** and Keep logs of successful encodes. *Reports* says that reports are kept in the history database and never expire, and has Open report after run, the new **Flag unseen errors from the last (days)** and the optional save-a-copy-as-a-file box; its folder and the new **Delete saved copies after (days)** appear only while that box is ticked. Before, one "Log/report retention" number cleaned logs and saved copies and also set how far back the sidebar badge and the Unseen errors table looked, which looked like it applied to the stored reports too. A blank box for either new setting means "same as Delete logs after", so existing installs behave exactly as before. The red button is now **Purge Logs & Report Copies**.
- **The queue is now a database too.** What Compressarr remembers between passes - the queue order, skips, per-file profiles, "land in another lane" choices, failed files that are held, an encoded file waiting for a move to be retried, and converted sources that were kept - moves from `compressarr.resume.json` into `compressarr.state.db` (SQLite, next to the history database, kept in a separate file so one cannot take the other down). Each change is a single transaction that writes only the rows it changed, so a crash or power cut leaves the queue as it was, two edits at once cannot overwrite each other, and a damaged file no longer stops every Monitor refresh and pass. The first start imports the old file (and renames it to `compressarr.resume.json.imported`, never deleting it); if that file or a later database cannot be read it is set aside as `.damaged`, the log says so, and the queue starts empty (your folders are scanned again). Backup and restore carry the database; restoring an older backup imports its queue file.
- **`{report_path}` in a notification is now the report's web address** (this computer's name, the web port and the run's number, such as `http://my-pc:1212/api/reports/412`), so it opens from a phone on the same network. Before, it was a file path that only meant something on this machine. The Generic Webhook and IFTTT channels still carry it; the other channels still never send it.
- **Clear History and Clear All delete the history from the database** (reports included) instead of moving a file to the Recycle Bin; a backup is the way back, and the confirmation says so. Purge Logs & Reports still leaves the history alone.
- **The starting speed estimate** the Monitor shows before a file has finished is now worked out from the files stored for the last few runs, instead of being read out of report HTML files.
- **A damaged line in an old history file no longer takes the whole History page down**: it is skipped and counted.
- **Opening Compressarr now shows a start-up window at once.** A small window with the logo, the name, the version and what it is doing ("Getting things ready...", "Starting the web interface...", "Almost ready...") appears first thing and closes just before the Monitor opens, so the few seconds of background start-up no longer look like nothing happened. It is not shown when Windows starts Compressarr at login (the login entry now passes `--autostart`; an existing entry is updated the first time the installed copy starts). The first CPU reading, which used to make the first Monitor load wait almost a second, is now taken behind the splash.
- **Purge Logs & Reports no longer deletes the run history.** It is disk cleanup: it permanently deletes every log and every HTML report, and leaves `Compressarr_History.csv` alone (the History page keeps all its numbers; only the report links stop working). It used to remove the history as well. **Clear History** and **Clear All** still delete it. The button's confirmation and help text say so.

### Fixed
- **The file count of a pass can no longer read "65 of 0".** The total was counted once, when the lane was prepared, so a lane that started empty and then received files (imports during a long pass) showed its running count against a total of 0 on the Home Assistant `fileIndex`/`fileTotal` fields (the Monitor hid the line). The total now grows to cover the files already started plus the ones still waiting in that lane.
- **Smart App Control (Windows 11) no longer blocks Compressarr.** Only the main program and the installer were signed, so Smart App Control refused to load the unsigned files next to it (`Compressarr.Desktop.dll` and the other Compressarr and Avalonia libraries) and Compressarr would not start; it also refused the unsigned uninstaller the installer generates. Every program file in the installer (ours and the bundled third-party ones) and the uninstaller are now signed. The HandBrakeCLI and ffmpeg programs that Compressarr downloads are third-party builds that are not signed; on a PC with Smart App Control a one-time notice about them can appear the first time Compressarr starts after an installation (in testing both ran normally afterwards; the Troubleshooting page on the wiki explains it).

## [2.2.0-rc.2] - 2026-10-22

> [!NOTE]
> Changes since [2.2.0-rc.1](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-rc.1)
> only - everything else is in the entries below.

### Added
- **Plex editions (`{edition-Name}`) are filed the way Plex expects.** Plex lets you keep more than one edition of a movie or a TV show (Plex Pass). Compressarr now handles both. **Movies:** every edition of a movie goes into the one `Title (Year)` folder, with the tag left in the file name, as before - this is now pinned by tests. **TV shows:** Plex puts an edition on the show's top-level folder (it has no episode-level edition), so `Babylon 5 (1994) {edition-Bluray Remaster} - S01E01 - Title.mkv` is filed in `Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\`, kept exactly as written and next to the plain `Babylon 5 (1994)` folder. A tag after the episode name stays in the file name only. **A tag carried only by the source show folder works too:** a file at `Input\Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\Babylon 5 (1994) - S01E01 - Midnight.mkv` (no tag in its own name) is filed in `Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\`, also when a failed move is retried later. The folder is the file's own folder or, when that is a season folder ("Season 01", "Specials"), the one above it; it must be inside the lane's Input folder and be the same show as the file name says, so a folder that merely holds shows is never mistaken for it, and a tag already in the file name wins. Anything that is not exactly Plex's form (`{edition Name}` without the hyphen, square brackets, a tag before the year, a name over 32 characters) is filed exactly as it always was.
- **OMDb as a second poster source.** Settings > **Queue Posters** has a new **Poster source** choice: **TMDb** (the best pictures, several sizes, a free account needed, as before) or **OMDb** ([omdbapi.com](https://www.omdbapi.com): no account, just an email address to get a free key of 1,000 lookups a day, and one picture size). Each source has its own key field and **Test Key** button. Enter a key for the other source too and a tick appears, **If _[chosen source]_ has no picture for a title, try _[the other one]_**, so either source can be the backup for the other; the title is sent to the second source only when the first finds nothing. OMDb matches loosely, so its answer is only used when it is plainly the same thing: the same kind (movie or series), the same title (ignoring case, punctuation and a leading "The") and a portrait picture. Anything else counts as no match, so a wrong show never gets a poster (no poster, or the backup source's, is better than the wrong one). **Use smaller images** only affects TMDb pictures. Switching sources shows nothing from the old source: each title is looked up again with the new one, and a title that one source did not know is not asked of that source again. Nothing changes for existing setups: TMDb stays the source unless you pick OMDb.

### Fixed
- **An edition or ID tag in a name is no longer searched for an episode number.** A movie or show with an edition named after its aspect ratio, such as `Blade Runner (1982) {edition-4x3}.mkv`, was read as season 4, episode 3 and filed as a TV episode. The text inside `{edition-...}`, `{tmdb-...}`, `{tvdb-...}` and `{imdb-...}` is now ignored when looking for the season and episode.
- **A Plex tag is no longer sent to a poster source.** For a TV show named `Babylon 5 (1994) {edition-Bluray Remaster}` the tag was part of the title sent to TMDb or OMDb (so no poster was found, and the edition's name was sent out). It is now left out, and the show and its editions share one poster lookup.

## [2.2.0-rc.1] - 2026-10-17

> [!NOTE]
> Changes since [2.2.0-beta.5](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-beta.5)
> only - everything else is in the entries below.

### Added
- **View Log: copy text out of the Monitor page's log.** The Monitor page's log panel (now titled **Live Log**, with a dim "last 200 lines" beside the title) is redrawn every 1.5 seconds, which clears any text you have selected in it. A new **View Log** button under it opens the same lines in a window that does not refresh by itself, with **Copy all** and **Refresh** buttons (Esc or clicking outside closes it).
- **Passthrough: all of Compressarr's benefits without re-encoding.** A lane's TV and Movie profile dropdowns (and a queued file's own profile on the Monitor page) have a new first choice, **Passthrough (no re-encode)**. A file given it is copied to the Output step instead of being encoded, and everything else runs as usual: filing into the show/movie folders, renaming, the delete / recycle / keep-the-source setting, companion files, Sonarr and Radarr, clearing the title tag, the report, "lands in" routing. The ffmpeg and HandBrake HDR / Dolby Vision checks are skipped (nothing is re-encoded, so nothing can be lost), and a lane that only passes through needs no encoder installed. The Monitor shows a teal **Passthrough** badge on the queue row and "Copying File in Lane ..." while it runs (with a byte-based percentage; Pause is not offered during a copy, Abort works). Reports show a teal Passthrough badge in place of the profile name. In History a passed-through file counts as a file but its sizes are not counted, so the savings stay the real ones; the report's totals and the notifications leave its bytes out too. A copy needs free space for the copy until the source is removed, and with the keep-the-source setting the library ends up with a second copy.
- **Posters on the Monitor queue (optional, off by default).** Settings > **Queue Posters** > **Show Posters on Queue** puts a small poster beside each file in the queue, and a larger one beside the file being encoded, looked up on [TMDB](https://www.themoviedb.org) with your own free API key (the card has a Test Key button and the sign-up link). Only the show or movie title (and year) taken from the file name is sent to TMDB, never a path; a TV file is matched by show name, and a movie needs a "(YYYY)" year tag in its name (a file without one gets a neutral placeholder rather than a guess). Each title is looked up once and each picture downloaded once, then served from Compressarr's own data folder (`posters`, safe to delete, with a Clear Cached Posters button under Settings > Maintenance), so nothing is fetched on every refresh and the pictures keep showing offline. A picture whose title has not been in the queue for the Retention days period (Settings > Logging & Reports, 30 days by default, 0 keeps everything) is deleted by a daily background cleanup; what was looked up is kept, so the picture is simply downloaded again if that title returns. Poster size (Small, Medium, Large) and Use smaller images (TMDB's lighter picture sizes) are in the card. Nothing is requested while it is off, and the credit "This product uses the TMDB API but is not endorsed or certified by TMDB" is on the card and the About page.
- **A machine name for notifications.** If you run Compressarr on more than one machine, the Message Format card on the Notifications page has an **Include machine name** checkbox (off by default) and a name field. Ticking it fills the field with the computer's name, which you can change; nothing is added or sent until you tick it and save. With it on, the Minimal, Standard and Detailed styles start the title with it, for example `[Basement NAS] Compressarr run #47 - Success`, on Discord, Slack, Telegram, Pushover, ntfy, Gotify, IFTTT and Notifiarr, and so do the daily/weekly digests and the Test button. A Custom style places it with the new `{machine}` token instead. The Generic Webhook's JSON gets a separate `machine` field and its `title` is left as it was. The desktop toast is unchanged. Names are up to 40 characters, with square brackets and control characters removed.
- **A preset catalog: more profiles without a new Compressarr release.** Profiles > Import > **From Catalog** (between "From installed HandBrake" and "From a presets file") reads the official catalog, https://github.com/Compressarr/Compressarr-Presets, which is shown on screen as "Importing from:". You tick the profiles you want and Import: nothing is added automatically and nothing is checked on a timer (the catalog is read when you open the tab, or press Check for Updates on the new **Preset catalog** card on the About page). Catalog profiles are tagged with a lock and "catalog" and are locked like the built-ins (View and Duplicate), but can be deleted - refused, naming the lane, while a lane uses one. Every download is checked against the SHA-256 the catalog lists and the same validators as any other profile before it is stored, and a stored profile that no longer matches what was installed is not offered to lanes (the Profiles page says so). Published versions never change: a profile is named `<name> vN`, a newer version is added next to the old one (marked "newer version", never an update), and a version can be marked deprecated with a reason. If a catalog name is already taken (a built-in, one of yours, another catalog profile) the row offers **Import as:** a new name (default `<name> (copy)`), and it is saved as one of your own profiles. Built-in profiles are not in the catalog. The profiles you add are included in the automatic backups and in Export/Import configuration, and a restore brings them back with no network. The Profiles page's storage panel has a **Catalog (downloaded)** box. The first catalog has four profiles: **Compressarr UHD HEVC (HDR) v1** (x265 10-bit RF 22, keeps Dolby Vision, HDR10+ and HDR10), **Compressarr HD Quality v1** (x265 10-bit RF 20), **Compressarr Compatibility (H.264) v1** (your original VeryFastDDtoAAC: x264 8-bit, MP4, AAC, 30 fps cap; SDR only) and **Compressarr AV1 (NVENC) v1** (cq 34, needs an RTX 40-series or newer).
- **AV1 on an NVIDIA GPU is checked on its own.** The Encoder page's capability check opened a one-frame GPU session for H.265 only, so an ffmpeg profile using `av1_nvenc` could look fine on a GPU that can't encode AV1 (it needs an RTX 40-series or newer) and then fail mid-run. `av1_nvenc` now has its own test and its own chip on the Encoder page (ticked only when a session really opens), and an AV1 NVENC profile on a GPU without AV1 is flagged on Profiles and Lanes with the real reason.
- **HandBrake lanes no longer re-encode an HDR file with a profile that would lose the HDR.** Before an encode, a lane running a HandBrake profile whose video encoder can't be relied on for HDR reads the file (with ffprobe). An H.264 profile can only produce SDR, so an HDR10, HLG, Dolby Vision or HDR10+ file would come out with washed-out colours; a hardware encoder (NVEnc, QSV, AMF) can't keep Dolby Vision or HDR10+. Such a file is left alone and reported as ERROR 114 (it stays in the queue with its ERROR badge, like other refusals) instead of being re-encoded and replacing the original. HandBrake profiles that keep everything (the 10-bit x265 and SVT-AV1 encoders with both Dolby Vision and HDR10+ passthrough on, as the built-ins do) are never probed, so they pay nothing; a profile with only one of the two on, or an 8-bit x265 / SVT-AV1 encoder (which can't carry either), is checked like any other, and a profile that has switched dynamic-metadata passthrough off has already said it doesn't want it. A lane that really wants it can tick **Allow HDR files to lose their HDR** (under Advanced on the Lanes page); that never covers a Dolby Vision profile 5 file, which would show wrong colours. If ffprobe isn't installed the check is skipped with one note in the log (install ffmpeg on the Encoder page to turn it on). It also applies to a file an ffmpeg lane hands to a HandBrake fallback profile.
- **An ffmpeg profile can now encode Dolby Vision / HDR10+ files anyway.** The profile editor's Dolby Vision & HDR10+ section is a three-way choice: refuse the file (the default), encode it with a HandBrake profile, or **encode it anyway without Dolby Vision / HDR10+**. The last drops the dynamic metadata, so the output is plain HDR10 (the file's HDR10 base layer) and the original is replaced by that lesser copy; choosing it asks for confirmation, the log says what was dropped, and the report tag reads "ffmpeg (Dolby Vision dropped)" or "ffmpeg (HDR10+ dropped)". A Dolby Vision profile 5 file has no HDR10 base layer and would show wrong colours without the Dolby Vision data, so it is still refused (ERROR 112, with that reason). A named HandBrake profile always wins over "encode anyway", and a broken one is never quietly replaced by it. Preview decisions says which of the three will happen to the file you preview.
- **An Advanced button in the top bar** (on Settings and Encoder, the pages that have something to reveal). It shows
  options that are tucked away by default and is remembered per browser: it changes what that browser shows, not any
  setting. For now it holds the two things below.
- **ffmpeg download channel: Stable or Latest.** Check/Install used to download whatever the newest development (master)
  build was. It now defaults to **Stable**, the newest numbered release (ffmpeg 9.0 today; it follows 9.1, 10.0 and so on as
  BtbN publishes them): bug fixes only, so encodes behave the same from one update to the next. **Latest** is the
  development build, as before. The choice is under Advanced on the Encoder page. Check/Install, the About page's
  Check for Updates and the download prompt all follow it, and an ffmpeg that Compressarr installed from the other
  channel is reported as that (not "newer") with an offer to switch, and Check/Install now also offers to download a newer build
  of the same channel instead of just saying ffmpeg was found. Nothing changes for an ffmpeg you already have until
  you choose to switch. The download is still checked against the checksum GitHub publishes for it.

### Changed
- **A note about Radarr's "Delete empty folders" setting.** The ...arr Integrations card on Settings now says to turn that setting off (in Radarr: Settings > Media Management > Folders), and has a **Turn it off in Radarr** button that does it for you (after a confirmation, through Radarr's API, changing that one setting only); Test Connection also tells you whether it is on. With it on, Radarr removes the movie's now-empty folder in the middle of its own rescan and the rescan ends "failed" with a 'could not find a part of the path' error, so Compressarr can't confirm the rescan and leaves its source-folder cleanup for a later pass (the file itself is filed correctly either way).
- **A notification channel card shows its own settings first.** On Notifications, each channel's webhook URL, token or other fields now come before the Receive Digest row, instead of after it.
- **Plain messages when GitHub can't be reached.** With no internet, the About page's Check for Updates buttons (Compressarr, HandBrakeCLI, ffmpeg and the preset catalog), the catalog's Import dialog and the Encoder page's Check/Install used to show raw system text such as "No such host is known. (api.github.com:443)", worded differently on each page. They now all say one plain sentence: "Couldn't reach GitHub. Check your internet connection and try again." ("...the catalog" for the catalog), with matching wording for a timeout, a rate limit (HTTP 403/429), a missing file (404) and a GitHub server problem (5xx). HandBrakeCLI's Check for Updates (and the Encoder page's HandBrake Check/Install) used to fail offline with the browser's "Failed to execute 'json' on 'Response': Unexpected end of JSON input"; they now show the same sentence.
- **On/off switches are toggles.** The switches that turn a whole feature on or off now look like the lane card's Enabled switch, instead of checkboxes: on Settings, FileBot, Enable Monitoring at Startup, Launch Monitor at Startup, Start with Windows, Sonarr, Radarr, Show Posters on Queue and Show announcements; on Notifications, the desktop toast and each Daily / Weekly digest (the toast's and every channel's); on Scheduler, Enable the day/night schedule. Options inside a feature (for example Use smaller images, Keep logs of successful encodes, Allow HDR files to lose their HDR) stay checkboxes. Nothing changes in how they work or what is saved.
- **The built-in ffmpeg profiles no longer hand Dolby Vision or HDR10+ files to HandBrake.** Their HandBrake fallback is now None: such a file is left alone and reported as ERROR 112 (and the report's help-bubble says how to fix it); it stays in the queue with its ERROR badge instead of being refused and reported again on every pass, until you change the profile and use Remove on its row instead of being re-encoded with a profile you didn't choose, which would replace the original. To have those files encoded, duplicate the profile and pick a HandBrake fallback on the copy, or use a HandBrake lane (HandBrake keeps Dolby Vision and HDR10+). Duplicate as ffmpeg also starts with the fallback set to None. Your own ffmpeg profiles keep whatever fallback they have.
- **Failure messages in the top bar are red.** Many errors ("Could not delete the profile", "Failed to save...", "Import failed", a failed notification test and so on) showed as plain text; they now use the same red chip as other errors. Progress messages ("Saving...") stay plain and successes stay green.
- **Clearer messages when a file can't be read or is HDR-blocked.** An ffmpeg lane given a damaged file or a non-video file now says the file may be damaged or not a video, instead of asking whether ffprobe is installed (it still asks that when ffprobe really is missing). The ERROR 114 text no longer reads "a HDR file".
- **Cleaning up a deleted lane's old entries is no longer shown as an error.** "Resume entry ... belongs to a lane that no longer exists - removing it" is routine housekeeping, so it is now an ordinary log line instead of a red one.
- **A lane's TV and Movie Destination paths are checked like Input.** On an enabled lane, a destination folder that does not exist (with Move converted files on) turns red with a message, and the red clears within half a second of fixing it, no Save needed. Compressarr still never creates the folder, and a run is not stopped by this.
- **E-AC3 audio is honest about 5.1.** ffmpeg's E-AC3 encoder (and HandBrake's, which is the same encoder) cannot write 7.1 - a 7.1 or 8-channel source always came out as 5.1 while the profiles said "up to 7.1". The ffmpeg built-ins now say 5.1, and an ffmpeg profile whose codec cannot hold the channels (E-AC3 and AC3: 5.1, MP3: stereo) mixes down explicitly and the Preview says why. The HandBrake built-ins are unchanged (HandBrake has always capped them itself, and editing them would make your 2.1 copies look edited). Want 7.1 kept? Use a profile that copies E-AC3/TrueHD/DTS-HD through, or AAC/FLAC/Opus, which carry 7.1.
- **Catalog: one name can be both a HandBrake and an ffmpeg profile.** Installing now says which engine, so a catalog family published for both (like the built-ins) installs each one on its own. The first such family is Compressarr SD-HD Opus 7.1 (SD-HD with Opus audio, which keeps 7.1 surround) once the catalog is updated.
- **HDR10+ files are described as HDR10+.** The report tag for an ffmpeg lane that hands an HDR10+ file to HandBrake now reads "HandBrake (HDR10+ fallback)" instead of "Dolby Vision fallback", and the ffmpeg Preview note says what this profile will actually do with the file (hand it to HandBrake, drop the metadata, or leave it alone as an error) instead of a general rule.
- **Importing or restoring the wrong or a damaged file is refused in plain words.** A truncated export, an empty file, text, or a damaged backup zip now says the file looks damaged or cut short (no more temp-file paths or "Central Directory corrupt"), and nothing is changed. A valid JSON file that has none of Compressarr's settings - an empty `{}`, or another program's file such as HandBrake's presets.json - used to import "successfully" and reset every lane and setting to defaults; it is now refused.
- **With "Maintain" a converted file is no longer converted again on every pass.** Maintain leaves the source in the Input folder, but the finished file was forgotten at the end of the pass and then found again as new, so the same file was re-encoded over and over. Compressarr now remembers a converted file whose source was kept, by its size and date: it stays out of the queue and is not encoded again while it is unchanged. Put a different file at the same path (or edit it) and it is converted as new; delete the kept source yourself and it is forgotten.
- **The "Where profiles are stored" cards on the Profiles page sit in two rows of two** (one column on a narrow screen), instead of three and one.
- **Announcements.** A bell in the top bar lights up when the Compressarr project has posted news (releases, betas, catalog releases) in the last 30 days that you have not seen; click it to read them. The messages come from a public GitHub repo (Compressarr/Compressarr-Announcements), are fetched once at start-up and then about daily, and are plain text with at most a link to the Compressarr GitHub page or compressarr.tv. Nothing about you or your computer is sent. It is on by default and can be turned off in Settings > Announcements, which stops the requests completely. A new install does not show news from before it was installed, except one welcome message: the project marks a message as a welcome, and an install that has not read it yet shows it as new once, whenever it was posted.
- **Update channel: Stable, or Stable and beta.** A new Settings > Updates choice sets which releases the update check offers. Stable is real releases only, as before. **During the 2.2 betas the default is Stable and beta**, so testers are offered each new beta (2.2.0 final will go back to Stable by default; a choice you have saved is kept). Stable and beta offers the highest version of any release, so a 2.2 beta counts as newer than 2.1.x and a final 2.2.0 counts as newer than its betas. This also fixes the check for beta users: it compared plain numbers, so someone on 2.2.0-beta.5 was never told that 2.2.0 itself was out.
- **Compressarr has a new home on GitHub: github.com/Compressarr/Compressarr** (the organization), with the preset catalog and the announcements in Compressarr-Presets and Compressarr-Announcements next to it. Update checks, the catalog, announcements and every link in the app use the new address. The old github.com/MrWizardCT/... addresses keep redirecting, so earlier versions keep working.
- **Your own profiles are tagged "user" on the Profiles page**, the way the shipped ones are tagged "built-in", so the two are easy to tell apart.
- **The destructive Maintenance buttons on Settings are under Advanced.** Clear Logs, Clear History, Purge Logs & Reports,
  Clear All, Reset Lanes and Clear Configuration are hidden until Advanced is on. Reset Resume File and Clean Up Now stay
  visible. Each of the six keeps its confirmation prompt.

### Fixed
- **A source folder's cleanup could remove a video that was still waiting.** Input folders are scanned recursively, so a movie folder can hold a subfolder (Extras, Featurettes) with a video that is still queued, skipped, or under the size filter. When the movie next to it finished, the cleanup treated that subfolder as an unmatched leftover and sent the whole thing to the Recycle Bin (or deleted it, if recycling failed). A subfolder is now only cleaned up when nothing at any depth below it is a video; a link (junction) or a folder that can't be read (including one that denies listing) is left alone, and the folder above it then stays too. (Also fixed in 2.1.10.)
- **A queue change could be wiped by a retry or by the start of a pass.** Retrying a failed move, a companion-file move or a deferred source-folder cleanup can wait minutes on the Sonarr/Radarr rescan, and a pass that tracks new files, follows a FileBot rename, prunes entries of a deleted lane or fills in missing order numbers works on a list it read a moment earlier; a skip, reorder, profile change or new file added on the Monitor page in between was lost when it saved. Each of these now saves only what it changed, merged into the file as it is now, and the end-of-pass tidy-up is decided against the file as it is now.
- **A file whose encode failed, or that was refused for its HDR, now stays held after a restart.** The "held" mark was kept in memory but not written to the resume file, so the next pass tried the file again, and repeated the failure and its error report, until monitoring was stopped.
- **With "Maintain" (keep the source), a converted file is remembered across a restart.** Its size and time were noted in memory but not saved with the file's result, so a restart, or a pass with other work outstanding, could convert the same unchanged source again.
- **The HDR check now looks at which kind of HDR a profile keeps.** A HandBrake profile set to pass through only HDR10+ (or only Dolby Vision) was treated like "all", so a Dolby Vision profile 5 file could skip the check and be encoded with wrong colours; an 8-bit x265 or SVT-AV1 profile was treated as keeping Dolby Vision and HDR10+, which only the 10-bit and 12-bit encoders do.
- **Renaming or deleting a HandBrake profile no longer breaks an ffmpeg profile's fallback.** Deleting a HandBrake profile that an ffmpeg profile names as its Dolby Vision / HDR10+ fallback is refused (the message names the ffmpeg profile); renaming it updates your own ffmpeg profiles' fallback to follow, and is refused when a built-in or catalog ffmpeg profile depends on the name, since those can't be edited.

## [2.2.0-beta.5] - 2026-10-10

> [!NOTE]
> Changes since [2.2.0-beta.4](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-beta.4)
> only - everything else is in the entries below.

### Changed
- **A new lane starts with a working profile.** Add lane (and Reset lanes) now pre-fill both the Movie and the TV profile with **Compressarr SD-HD**, the built-in general-purpose profile, instead of leaving them blank. A new user only has to choose the Input and Output folders and the library folders. Existing lanes are not changed.
- **Run Now runs the queue without starting monitoring.** On the Monitor page, Run Now now works whether monitoring is on or off. With monitoring on it still skips the countdown; with monitoring off it runs one pass and stops, leaving monitoring off. Abort works on it like on any pass. It is unavailable only while a pass is already running.
- **The Start/Stop button says just "Start" or "Stop".** The word "monitoring" was implied; it stays in the tooltip.
- **The top bar's buttons are icon-over-label.** On every page the action buttons (Save, Clear changes, Run once, Add lane, Start/Stop monitoring, Pause, Run now, Abort, the Profiles buttons, Back and Duplicate in the profile editors...) now show an icon above a short label, in groups split by thin dividers, instead of boxed text buttons. The full name is the tooltip. The bar is slightly taller (62 px instead of 56).
- **Messages in the top toolbar are readable.** Success and error messages ("Settings saved.", a failed save, a refused lane change...) were red or green text straight on the dark toolbar, which was hard to read. They are now light chips with an icon and dark text. A long message, or an error that doesn't fit the toolbar, appears in a bar under the toolbar that wraps and shows the whole text, with a close button. This applies to every page.

### Added
- **A Duplicate button on every lane card.** It asks for a name for the new lane and copies the lane exactly as the card shows it, every field. The copy always starts switched off, and appears right after the original.
- **Two enabled lanes can't watch the same Input folder.** Lanes scan their Input folder including subfolders, so the same folder - or one inside another - would have both lanes pick up the same files. Turning a lane on, or pointing it at a different Input, is now refused when another enabled lane already watches that folder or one around or inside it; the reason is shown under the Input field. Folders are compared by whole folder name (`D:\Media Download` and `D:\Media Download Kids` are different folders). A setup that already overlaps (an upgrade, an import) keeps running exactly as before, with both lanes flagged, and unrelated edits to those lanes still save. Save All now saves lanes one at a time, switching lanes off before switching others on.
- **The license now comes with the program.** The installer shows an information page about the GPLv3 (no "I accept"
  step - the GPL grants rights and isn't needed to run the program), and installs `LICENSE` and a new
  `THIRD-PARTY-NOTICES.txt` (every component Compressarr is built with, with its copyright and license text) into the
  install folder. The About page has a License line linking to both, to the source code, and a note that there is no warranty.
- **About page: an ffmpeg card with Check for Updates**, like the HandBrakeCLI one. It shows the installed ffmpeg and checks
  for a newer build. ffmpeg's own version is a commit id, so a newer build is recognised from the build Compressarr recorded when
  it installed ffmpeg; an ffmpeg you installed yourself (or one installed by an earlier beta) can only be shown the latest build.
- **About page: "Built with" now lists ffmpeg.**
- **A downloaded HandBrakeCLI gets a short license note beside it** (`LICENSE-NOTICE.txt`: GPLv2, with a link to its source),
  the same way a downloaded ffmpeg already keeps its license file.

## [2.2.0-beta.4] - 2026-10-08

> [!NOTE]
> Changes since [2.2.0-beta.3](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-beta.3)
> only - everything else is in the entries below.

### Added
- **The Monitor's status line says where the file will land.** It now reads, for example, "Compressing File in Lane UHD using ffmpeg profile Compressarr SD-HD and lands in Kids" - the lane you assigned the file to, or its own lane when you haven't.
- **Backups can be downloaded.** Each backup in the list on the Settings page now has a Download button, so a copy can be kept on another computer. The Backups card also suggests keeping a copy somewhere other than this machine, since backups are saved there by default.
- **"Create a desktop shortcut" is now ticked by default** in the installer, and an upgrade keeps the choice you made before instead of resetting it every time.
- **Browse... buttons on the Encoder page** for the HandBrakeCLI, ffmpeg and ffprobe paths. The Profiles import-from-file box has one too, listing only .json files. It opens Compressarr's own folder browser, which lists the programs (.exe files on Windows) in each folder and starts in the folder the path already points to.

### Changed
- **The run history now lives in Compressarr's own data folder** (`%AppData%\Compressarr\Compressarr_History.csv`) instead of the Logs folder, so clearing out the logs can't delete it and changing the log folder can't leave it behind. A history file already in the Logs folder is copied across the first time 2.2 needs it, and the old copy is removed. Backup, Restore, Clear History and Purge all use the new location.

### Fixed
- **A library folder that no longer exists is no longer recreated.** If a lane's TV Show or Movie base folder was renamed or its drive was gone, Compressarr used to create the whole folder path again and file the video into it, quietly starting a second, empty library. It now reports ERROR 102 (destination unavailable), names the missing folder, and files the video on the next pass once the folder is back. Compressarr still creates the show, season and movie folders inside an existing base folder. (2.1.x behaves the old way.)
- **"Full Details" links on a report now open when the report is viewed in the app.** They pointed at a local file path, which a browser refuses to follow from a page served by the app (History page, another device). They now go through the app, and still work when a report is opened straight from disk.
- **"Run at login" now follows a restored backup or imported settings.** The login entry was only written when Settings was saved, so settings that arrived another way never started Compressarr at login. Compressarr now checks it at startup (without disturbing an entry that already works) and applies it after Import and Restore.
- **Uninstalling removes the "run at login" entry.** It used to stay behind pointing at a program that no longer existed. An upgrade keeps it.
- **Failed encodes no longer count toward the History totals.** A file that failed to encode used to be added as "before" size with nothing "after", so every failure showed as 100% saved and inflated the files, before and savings figures (a file that failed on every pass piled up hundreds). Such files are now left out of the totals, the run's report and Errors count still show them. Earlier runs in which every file failed are skipped by the History rollups too. Files that encoded but could not be moved (ERROR 102-104) still count.
- **History: "Today" and "Last 7 Days" now follow the real date.** On a Compressarr that had been running for days they were stuck on the day it started (the figures at the top of the page no longer matched the reports below). This fix is also in 2.1.x.
- **A Lanes field no longer stays red once it holds a valid value.** A lane showing a configuration problem now re-checks itself as you edit or Browse, so the red outline (and the warning at the top) clears as soon as the problem is fixed instead of waiting for the next Save.
- **A file whose encode failed (ERROR 101) is no longer retried on every pass.** It stays in the queue with its ERROR badge and makes no new error report each time monitoring runs, so you can look into the cause first. Remove on its row queues it afresh once you have fixed the problem. Other errors (an abort, a missing preset) are retried as before.
- **The Extra CLI options example no longer suggests `--two-pass`**, which current HandBrakeCLI rejects ("unknown option"). The placeholder and tooltip on the Encoder page now show `--verbose=1`.
- **Check/Install no longer calls any file "ffmpeg".** It now asks the program at the configured path and says so when it does not answer like ffmpeg (a wrong .exe such as notepad.exe), and that check gives up after 8 seconds instead of 30.
- **Custom HandBrake presets used by a lane are no longer missed.** The one-time migration only ran on the very first start, so lanes that arrived afterwards (a restored backup, imported settings, or a first start on a clean install) still named presets that only lived in HandBrake's `presets.json`. Compressarr now copies any such preset into its own profiles at every startup, after Import settings and after Restoring a backup. Nothing is renamed or repointed by this.

### Compatibility
- **Going back to 2.1.x needs one manual step.** 2.1.x reads the history from the Logs folder, not from the new place. After downgrading, copy `%AppData%\Compressarr\Compressarr_History.csv` into your Logs folder (by default `%AppData%\Compressarr\Logs`, or whichever folder Settings → Logging points to); without that, the History page starts empty and 2.1.x begins a new file. If you used 2.1.x for a while and then come back to 2.2, copy the file the other way (replacing the one in the app data folder), because 2.2 keeps using its own copy once it exists. A backup made by either version restores into the other.

## [2.2.0-beta.3] - 2026-10-07

> [!NOTE]
> Changes since [2.2.0-beta.2](https://github.com/Compressarr/Compressarr/releases/tag/v2.2.0-beta.2)
> only - everything else is in the entries below.

### Fixed
- **Uninstalling (or upgrading) while Compressarr was running left it installed.** The tray app has no
  window to close, so it kept its program file locked: the uninstaller reported success but the
  install folder stayed behind. Setup and the uninstaller now stop a running Compressarr first (its
  settings are saved as they change), and the install folder is removed as a backstop.
- **"Launch Compressarr" at the end of Setup now opens the Monitor page** as well as starting the app.
- **A downloaded HandBrakeCLI now goes under `%AppData%\Compressarr\tools\HandBrakeCLI`**, next to
  ffmpeg, instead of directly in `%AppData%\Compressarr`. An existing install keeps working from
  wherever its saved path points.

## [2.2.0-beta.2] - 2026-10-06

> [!WARNING]
> **Pre-release (beta).** The stable release is still [2.1.8](https://github.com/Compressarr/Compressarr/releases/tag/v2.1.8).
> This build changes where encoding profiles live and adds an optional second encoder - a
> [test plan](docs/test-plan-2.2.html) is included.

> [!TIP]
> **What's new in 2.2.0-beta.2:** **Compressarr's own encoding profiles** (built-ins plus yours,
> with an editor and Import - HandBrake's `presets.json` is no longer used while encoding), new
> **Encoder** and **Profiles** pages, and **ffmpeg as an optional per-lane encoder** (experimental).
> Everything else below carries forward from 2.2.0-beta.1 for context - new changes are in **bold**.

### Added
- **Encoder page** (under Monitor). The HandBrake settings moved here from Settings (HandBrakeCLI path
  with Check/Install, Extra CLI options), plus an "At a glance" strip and the new ffmpeg card. The
  Monitor page shows a *Finish setting up* notice when a tool a lane needs isn't found.
- **Profiles page.** One list of every profile for both encoders: the built-ins (locked) and your
  own. Create, edit, duplicate, delete; renaming updates the lanes and queued files that use a
  profile, and delete is refused while one is in use. **Import** HandBrake presets from an
  installed HandBrake or any presets file, with keep-both / replace / skip for name clashes.
- **HandBrake profile editor.** Container, encoder, quality (RF or bitrate), speed, profile/level/
  tune, frame rate, deinterlace, crop, audio rules and pass-through list, bitrate/mixdown,
  subtitles, chapters - with the exact HandBrake command beside it. Anything the editor doesn't
  show is preserved exactly as stored.
- **ffmpeg (experimental), per lane.** A lane's **Encoder** can be HandBrake (default) or ffmpeg.
  Structured ffmpeg profiles (three built-ins: Compressarr SD-HD, Compressarr UHD AV1, HEVC NVENC
  (fast)), a profile editor with **Preview decisions** for a real file, **Duplicate as ffmpeg...**
  from a HandBrake profile (listing what couldn't be carried over), auto-crop and auto-deinterlace,
  HandBrake-style track selection, and capability detection (including whether an NVIDIA GPU
  session opens). **Check/Install** uses an ffmpeg already on the computer or downloads BtbN's GPL
  build after asking, verified against GitHub's published SHA-256.
- **Length verification (ERROR 111).** After an ffmpeg encode the finished file's length must match
  the source's; a truncated result is rejected and the original is left alone.
- **Dolby Vision / HDR10+ safety (ERROR 112).** Such a file on an ffmpeg lane is encoded by HandBrake
  using the ffmpeg profile's fallback profile, or refused - never encoded in a way that silently
  drops the metadata.
- **ERROR 113.** A lane whose encoder isn't installed is skipped with a clear report entry without
  stopping the other lanes.
- The report tags files with the encoder that really encoded them (only when it isn't plain HandBrake);
  the Monitor queue shows an encoder chip and offers each row its own lane's encoder's profiles.
- Scheduler page - an optional day/night schedule (off by default). A new sidebar page (after
  Lanes) with a live "Right now" card. Choose when "daytime" is - the same every day, one window for
  weekdays and one for weekends, or a window for each day of the week (a window may cross midnight,
  and any day can be "Run Off-hours Priority All Day") - then set the encoder's priority for the
  daytime and for off-hours (Low through Realtime; a running encode switches priority live at the
  boundary, no restart). Optionally **only encode during off-hours**: no new file starts in the
  daytime window, with a choice of finishing the current file first or suspending it until
  off-hours. A **Run anyway** button (Scheduler and Monitor) releases the hold for the rest of the
  window, the queue completion estimate counts held time, and the toolbar shows the current mode.
  Until you switch it on, nothing changes: encodes run at normal priority at all hours, exactly as
  before. Every setting is described on the page itself.
- Lane assignment - land a file in a different lane's library. Each queued file has a "Lands
  in" dropdown on the Monitor page. Choose another lane and the finished file (and its
  subtitles/artwork) is filed into *that* lane's TV/Movie library - the file itself is never moved,
  so Sonarr/Radarr never see it go missing, and its preset, Output folder, queue position and
  source-folder cleanup all stay with its own lane. If the destination is offline, or its lane is
  deleted, the finished file waits safely in Output and is retried every pass - it is never filed in
  the library you were avoiding. The Lanes page warns before you delete a lane that queued files
  are set to land in.
- Redirects are flagged afterward. The HTML report tags each redirected file and shows a banner
  listing them, and the History page highlights a run containing redirects (a violet notice colour,
  deliberately not red or yellow) with a "N redirected" tag. The run history CSV gains one column at
  the end, so 2.1.x still reads it.
- The About page shows pre-release versions in full (e.g. "2.2.0-beta.1").
- A persistent "Install as app" card in Settings > Web UI: installs the mobile-friendly PWA
  on demand (Chrome/Edge/Android), shows "Add to Home Screen" instructions on iOS (no installable
  prompt exists there), or - if already running as the installed app - explains how to uninstall
  it from the browser's own app list, since no web page can trigger that directly.
- A new opt-in `{file_list}` notification token: this run's filenames, one per line. Deliberately
  left out of every built-in Minimal/Standard/Detailed template - it only ever reaches a message
  if you type it into a Custom template yourself, since it's the one token that can put a media
  filename in front of wherever that channel sends its message.
- A close button on every HTML report - most useful in PWA/standalone mode, which has no browser
  chrome at all to close a tab with, but available on desktop too.
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- **Compressarr always uses its own profiles.** HandBrake is handed one generated file (built-ins
  plus yours), rewritten only when it is missing or different. The **presets.json path, Install/
  Merge Presets and Reload** controls are gone; `HandBrake.PresetsPath` stays in the settings file
  (hidden) for compatibility and as Import's default location.
- **First-start migration.** The presets your lanes and queued files use are copied from your old
  `presets.json` into your own profiles; a preset that has your built-in's name but a different
  recipe is kept as "<name> (yours)" and the lanes are repointed.
- **Backups and Export config now include your own profiles; restoring a backup restores them.**
- Settings no longer has the HandBrake card; "Keep logs of successful HandBrake encodes" is now
  "...successful encodes".
- A file that FileBot renames now keeps its place in the queue - along with any skip or preset
  override - instead of being treated as a brand-new arrival at the end, which silently undid a
  manual reorder. The rename is read from FileBot's own output.
- Sidebar order is now Monitor, Encoder, Profiles, Lanes, Scheduler, Notifications, History, Settings, About -
  Settings moves below History.
- Under the hood, with no change in behavior: every rule about which files are tracked, in what
  order, and what the Monitor page shows now lives in one place instead of being re-implemented in
  the Monitor page, the lane preparation and the next-file picker; and the encode step now talks to
  an engine-neutral interface (HandBrake is still the only encoder, with its exact command line
  pinned by tests). The automated test suite roughly doubled alongside this.
- The first-launch "Install as app" toolbar banner is retired, superseded by the persistent
  Settings > Web UI card above - the underlying install capability is unchanged, just no longer
  a one-time, dismiss-and-it's-gone prompt.
- The "Original v1.1 (PowerShell)" credit and link are removed from the About page - v1.1 is no
  longer referenced anywhere in the app.
- Monitor is now the default page when Compressarr starts (opening the web UI or the tray
  icon's "Open Web UI"), instead of Settings - a one-time setup page isn't where you check in
  day-to-day.
- A configuration change saved while monitoring is already running now actually takes effect on
  the next pass, instead of silently having no effect until monitoring is stopped and restarted or
  Compressarr itself is restarted - this covers Notifications, Report, PostExec, and Logging
  settings, and the Lanes list itself (add/remove/reorder/enable/disable).
- The whole app is usable on a phone or tablet: the sidebar becomes a slide-out drawer below
  about 760px width, the toolbar's action buttons and status cluster wrap onto their own rows
  instead of overlapping, and Monitor's queue rows reflow into a two-line card layout. Requires no
  extra setup - just open Compressarr's normal address in a mobile browser.
- The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- New files could still land above files already waiting in the queue, even after 2.1.7's
  queue-order locking. A file only got its permanent position once Compressarr tracked it, and
  that only happened when a Lane had nothing else waiting - so during any backlog or long encode,
  files that arrived were left untracked and ordered on the Monitor page by live folder-scan
  position (not arrival order), and were later locked into that same wrong order once the backlog
  drained. Every file is now tracked, at the end of the queue, the first time anything sees it - the
  Monitor page's own refresh or a monitoring pass, whichever comes first. A file re-added at a
  path that already had a completed or failed entry is also treated as a new arrival and goes to
  the end instead of reusing its old position at the top. A monitoring pass also merges in
  anything the Monitor page tracked before saving, instead of silently overwriting it.
- A file's position in the Monitor page's queue is now permanently locked the moment it first
  appears - only an explicit reorder (drag, or move to top/bottom, which is just a reorder under
  the hood) can ever change it again. Previously, a file with no explicit position sorted by live
  filesystem scan order, which is raw OS enumeration - not alphabetical, and not guaranteed to
  place a newly-added file last - so a brand-new file could land anywhere relative to already-known
  ones, and a single-file action (skip, preset override, remove) touching one untouched file could
  let it jump ahead of still-untouched files sitting alongside it.
- A resume-tracking entry belonging to a Lane that's since been deleted or renamed no longer sits
  permanently stuck - it's now cleaned up automatically, instead of forever inflating the "Resuming
  previous incomplete run" count and blocking the automatic cleanup that clears finished history
  once nothing is genuinely outstanding.
- An in-progress file could, in rare cases, still show up in the Monitor page's queue as if it
  were waiting to be processed, instead of being recognized as the one currently encoding - a name
  collision between two different files in different Lane subfolders was enough to trigger it.
- A file could show a "Resumed" badge it didn't deserve - a single queue-editing action (skip,
  preset override, remove) elsewhere in the same Lane was enough to make every file in that Lane
  look like leftover work from an interrupted run, even when nothing had actually been
  interrupted.
- Daily/Weekly digest notifications always showed "Duration: 0s" regardless of how long the
  underlying runs actually took - the digest summary never tracked a duration at all until now.
- A queue-control edit (reorder, skip, preset override, or remove) made anywhere in the queue
  while a different file was still finishing its own routing, companion-file move, or Sonarr/Radarr
  rescan-confirmation wait (which can take up to about two minutes) could be silently lost the
  instant that other file's own result was saved - found by an external pre-release code review.
  Queue edits are now merged onto the freshest state on disk instead of being overwritten by a
  stale in-memory snapshot.
- Queue-control actions (reorder, skip, preset override, remove) identified a file by its
  filename alone, which could target the wrong file if two different files in different lane
  subfolders happened to share the same name - also found by the same review. These now match on
  the file's full path instead.
- A hung or runaway post-execution command now times out (5 minutes) and is killed instead of
  blocking that pass's report and notifications indefinitely, with nothing - not even Abort - able
  to interrupt it before.
- resume.json is now written atomically (write-to-temp-then-rename) instead of in place,
  preventing a corrupted or truncated resume file if Compressarr is killed or crashes mid-write -
  this file is rewritten after every single file and every queue-control edit, so it's frequent
  enough to matter.
- HandBrakeCLI's stderr stream is now fully drained before being read, closing a rare timing gap
  where its own "Finished work at" completion line - read to determine success - might not have
  fully arrived yet, a plausible source of an occasional false "encode failed" result.
- FileBot is now launched with its arguments passed individually instead of built into one
  manually-quoted command-line string, removing a class of quoting problems from paths or
  arguments containing spaces or special characters.
- Sonarr/Radarr URLs are now validated and composed through .NET's own URL handling instead of
  bare string concatenation, rejecting a malformed URL up front with a clear error instead of
  failing unpredictably later - a custom URL Base (for a reverse-proxy setup) is still respected.
- Aborting a run or stopping monitoring now actually cancels an in-flight Sonarr/Radarr API call
  immediately, instead of waiting for it to finish on its own (up to 15 seconds) before the
  abort/stop took effect for that file.
- Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.
- A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".
- A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.
- A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.
- The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.
- Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.
- Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's own arguments are passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

### Compatibility
- Settings stay compatible with 2.1.8: new fields are additive (`FFmpeg`, a lane's `Engine`). A 2.1.8
  install reading a lane set to ffmpeg finds no HandBrake preset of that name and skips the lane
  with its usual "preset not found" message - it never encodes with the wrong tool.

## [2.2.0-beta.1] - 2026-10-06

> [!WARNING]
> **Pre-release (beta).** Please try it on a real install and tell us what you find - the stable
> release is still [2.1.8](https://github.com/Compressarr/Compressarr/releases/tag/v2.1.8).

> [!TIP]
> **What's new in 2.2.0-beta.1:** an optional **day/night Scheduler** (encode priority by time of
> day, with an optional off-hours-only hold), **lane assignment** (land a file in a different
> lane's library without moving it, with the redirect flagged on the report and History), and
> FileBot-renamed files **keeping their place in the queue**. Everything else below carries forward
> from 2.1.8 for context - new changes are in **bold**.

### Added
- **Scheduler page - an optional day/night schedule (off by default).** A new sidebar page (after
  Lanes) with a live "Right now" card. Choose when "daytime" is - the same every day, one window for
  weekdays and one for weekends, or a window for each day of the week (a window may cross midnight,
  and any day can be "Run Off-hours Priority All Day") - then set the encoder's priority for the
  daytime and for off-hours (Low through Realtime; a running encode switches priority live at the
  boundary, no restart). Optionally **only encode during off-hours**: no new file starts in the
  daytime window, with a choice of finishing the current file first or suspending it until
  off-hours. A **Run anyway** button (Scheduler and Monitor) releases the hold for the rest of the
  window, the queue completion estimate counts held time, and the toolbar shows the current mode.
  Until you switch it on, nothing changes: encodes run at normal priority at all hours, exactly as
  before. Every setting is described on the page itself.
- **Lane assignment - land a file in a different lane's library.** Each queued file has a "Lands
  in" dropdown on the Monitor page. Choose another lane and the finished file (and its
  subtitles/artwork) is filed into *that* lane's TV/Movie library - the file itself is never moved,
  so Sonarr/Radarr never see it go missing, and its preset, Output folder, queue position and
  source-folder cleanup all stay with its own lane. If the destination is offline, or its lane is
  deleted, the finished file waits safely in Output and is retried every pass - it is never filed in
  the library you were avoiding. The Lanes page warns before you delete a lane that queued files
  are set to land in.
- **Redirects are flagged afterward.** The HTML report tags each redirected file and shows a banner
  listing them, and the History page highlights a run containing redirects (a violet notice colour,
  deliberately not red or yellow) with a "N redirected" tag. The run history CSV gains one column at
  the end, so 2.1.x still reads it.
- The About page shows pre-release versions in full (e.g. "2.2.0-beta.1").
- A persistent "Install as app" card in Settings > Web UI: installs the mobile-friendly PWA
  on demand (Chrome/Edge/Android), shows "Add to Home Screen" instructions on iOS (no installable
  prompt exists there), or - if already running as the installed app - explains how to uninstall
  it from the browser's own app list, since no web page can trigger that directly.
- A new opt-in `{file_list}` notification token: this run's filenames, one per line. Deliberately
  left out of every built-in Minimal/Standard/Detailed template - it only ever reaches a message
  if you type it into a Custom template yourself, since it's the one token that can put a media
  filename in front of wherever that channel sends its message.
- A close button on every HTML report - most useful in PWA/standalone mode, which has no browser
  chrome at all to close a tab with, but available on desktop too.
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- **A file that FileBot renames now keeps its place in the queue** - along with any skip or preset
  override - instead of being treated as a brand-new arrival at the end, which silently undid a
  manual reorder. The rename is read from FileBot's own output.
- **Sidebar order is now Monitor, Lanes, Scheduler, Notifications, History, Settings, About** -
  Settings moves below History.
- **Under the hood, with no change in behavior:** every rule about which files are tracked, in what
  order, and what the Monitor page shows now lives in one place instead of being re-implemented in
  the Monitor page, the lane preparation and the next-file picker; and the encode step now talks to
  an engine-neutral interface (HandBrake is still the only encoder, with its exact command line
  pinned by tests). The automated test suite roughly doubled alongside this.
- The first-launch "Install as app" toolbar banner is retired, superseded by the persistent
  Settings > Web UI card above - the underlying install capability is unchanged, just no longer
  a one-time, dismiss-and-it's-gone prompt.
- The "Original v1.1 (PowerShell)" credit and link are removed from the About page - v1.1 is no
  longer referenced anywhere in the app.
- Monitor is now the default page when Compressarr starts (opening the web UI or the tray
  icon's "Open Web UI"), instead of Settings - a one-time setup page isn't where you check in
  day-to-day.
- A configuration change saved while monitoring is already running now actually takes effect on
  the next pass, instead of silently having no effect until monitoring is stopped and restarted or
  Compressarr itself is restarted - this covers Notifications, Report, PostExec, and Logging
  settings, and the Lanes list itself (add/remove/reorder/enable/disable).
- The whole app is usable on a phone or tablet: the sidebar becomes a slide-out drawer below
  about 760px width, the toolbar's action buttons and status cluster wrap onto their own rows
  instead of overlapping, and Monitor's queue rows reflow into a two-line card layout. Requires no
  extra setup - just open Compressarr's normal address in a mobile browser.
- The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- New files could still land above files already waiting in the queue, even after 2.1.7's
  queue-order locking. A file only got its permanent position once Compressarr tracked it, and
  that only happened when a Lane had nothing else waiting - so during any backlog or long encode,
  files that arrived were left untracked and ordered on the Monitor page by live folder-scan
  position (not arrival order), and were later locked into that same wrong order once the backlog
  drained. Every file is now tracked, at the end of the queue, the first time anything sees it - the
  Monitor page's own refresh or a monitoring pass, whichever comes first. A file re-added at a
  path that already had a completed or failed entry is also treated as a new arrival and goes to
  the end instead of reusing its old position at the top. A monitoring pass also merges in
  anything the Monitor page tracked before saving, instead of silently overwriting it.
- A file's position in the Monitor page's queue is now permanently locked the moment it first
  appears - only an explicit reorder (drag, or move to top/bottom, which is just a reorder under
  the hood) can ever change it again. Previously, a file with no explicit position sorted by live
  filesystem scan order, which is raw OS enumeration - not alphabetical, and not guaranteed to
  place a newly-added file last - so a brand-new file could land anywhere relative to already-known
  ones, and a single-file action (skip, preset override, remove) touching one untouched file could
  let it jump ahead of still-untouched files sitting alongside it.
- A resume-tracking entry belonging to a Lane that's since been deleted or renamed no longer sits
  permanently stuck - it's now cleaned up automatically, instead of forever inflating the "Resuming
  previous incomplete run" count and blocking the automatic cleanup that clears finished history
  once nothing is genuinely outstanding.
- An in-progress file could, in rare cases, still show up in the Monitor page's queue as if it
  were waiting to be processed, instead of being recognized as the one currently encoding - a name
  collision between two different files in different Lane subfolders was enough to trigger it.
- A file could show a "Resumed" badge it didn't deserve - a single queue-editing action (skip,
  preset override, remove) elsewhere in the same Lane was enough to make every file in that Lane
  look like leftover work from an interrupted run, even when nothing had actually been
  interrupted.
- Daily/Weekly digest notifications always showed "Duration: 0s" regardless of how long the
  underlying runs actually took - the digest summary never tracked a duration at all until now.
- A queue-control edit (reorder, skip, preset override, or remove) made anywhere in the queue
  while a different file was still finishing its own routing, companion-file move, or Sonarr/Radarr
  rescan-confirmation wait (which can take up to about two minutes) could be silently lost the
  instant that other file's own result was saved - found by an external pre-release code review.
  Queue edits are now merged onto the freshest state on disk instead of being overwritten by a
  stale in-memory snapshot.
- Queue-control actions (reorder, skip, preset override, remove) identified a file by its
  filename alone, which could target the wrong file if two different files in different lane
  subfolders happened to share the same name - also found by the same review. These now match on
  the file's full path instead.
- A hung or runaway post-execution command now times out (5 minutes) and is killed instead of
  blocking that pass's report and notifications indefinitely, with nothing - not even Abort - able
  to interrupt it before.
- resume.json is now written atomically (write-to-temp-then-rename) instead of in place,
  preventing a corrupted or truncated resume file if Compressarr is killed or crashes mid-write -
  this file is rewritten after every single file and every queue-control edit, so it's frequent
  enough to matter.
- HandBrakeCLI's stderr stream is now fully drained before being read, closing a rare timing gap
  where its own "Finished work at" completion line - read to determine success - might not have
  fully arrived yet, a plausible source of an occasional false "encode failed" result.
- FileBot is now launched with its arguments passed individually instead of built into one
  manually-quoted command-line string, removing a class of quoting problems from paths or
  arguments containing spaces or special characters.
- Sonarr/Radarr URLs are now validated and composed through .NET's own URL handling instead of
  bare string concatenation, rejecting a malformed URL up front with a clear error instead of
  failing unpredictably later - a custom URL Base (for a reverse-proxy setup) is still respected.
- Aborting a run or stopping monitoring now actually cancels an in-flight Sonarr/Radarr API call
  immediately, instead of waiting for it to finish on its own (up to 15 seconds) before the
  abort/stop took effect for that file.
- Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.
- A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".
- A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.
- A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.
- The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.
- Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.
- Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's own arguments are passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

## [2.1.8] - 2026-10-04

> [!TIP]
> **What's new in 2.1.8:** a follow-up to 2.1.7's queue-order locking, which still let a new file
> land above files already waiting. A file's place in the Monitor page's queue is now genuinely
> locked from the moment Compressarr first sees it - new arrivals always join at the end, in the
> order they actually arrived, and only a manual reorder can move one afterward. Everything else
> below carries forward from 2.1.7 for context - new changes are in **bold**.

### Added
- A persistent "Install as app" card in Settings > Web UI: installs the mobile-friendly PWA
  on demand (Chrome/Edge/Android), shows "Add to Home Screen" instructions on iOS (no installable
  prompt exists there), or - if already running as the installed app - explains how to uninstall
  it from the browser's own app list, since no web page can trigger that directly.
- A new opt-in `{file_list}` notification token: this run's filenames, one per line. Deliberately
  left out of every built-in Minimal/Standard/Detailed template - it only ever reaches a message
  if you type it into a Custom template yourself, since it's the one token that can put a media
  filename in front of wherever that channel sends its message.
- A close button on every HTML report - most useful in PWA/standalone mode, which has no browser
  chrome at all to close a tab with, but available on desktop too.
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- The first-launch "Install as app" toolbar banner is retired, superseded by the persistent
  Settings > Web UI card above - the underlying install capability is unchanged, just no longer
  a one-time, dismiss-and-it's-gone prompt.
- The "Original v1.1 (PowerShell)" credit and link are removed from the About page - v1.1 is no
  longer referenced anywhere in the app.
- Monitor is now the default page when Compressarr starts (opening the web UI or the tray
  icon's "Open Web UI"), instead of Settings - a one-time setup page isn't where you check in
  day-to-day.
- A configuration change saved while monitoring is already running now actually takes effect on
  the next pass, instead of silently having no effect until monitoring is stopped and restarted or
  Compressarr itself is restarted - this covers Notifications, Report, PostExec, and Logging
  settings, and the Lanes list itself (add/remove/reorder/enable/disable).
- The whole app is usable on a phone or tablet: the sidebar becomes a slide-out drawer below
  about 760px width, the toolbar's action buttons and status cluster wrap onto their own rows
  instead of overlapping, and Monitor's queue rows reflow into a two-line card layout. Requires no
  extra setup - just open Compressarr's normal address in a mobile browser.
- The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- **New files could still land above files already waiting in the queue, even after 2.1.7's
  queue-order locking. A file only got its permanent position once Compressarr tracked it, and
  that only happened when a Lane had nothing else waiting - so during any backlog or long encode,
  files that arrived were left untracked and ordered on the Monitor page by live folder-scan
  position (not arrival order), and were later locked into that same wrong order once the backlog
  drained. Every file is now tracked, at the end of the queue, the first time anything sees it - the
  Monitor page's own refresh or a monitoring pass, whichever comes first. A file re-added at a
  path that already had a completed or failed entry is also treated as a new arrival and goes to
  the end instead of reusing its old position at the top. A monitoring pass also merges in
  anything the Monitor page tracked before saving, instead of silently overwriting it.**
- A file's position in the Monitor page's queue is now permanently locked the moment it first
  appears - only an explicit reorder (drag, or move to top/bottom, which is just a reorder under
  the hood) can ever change it again. Previously, a file with no explicit position sorted by live
  filesystem scan order, which is raw OS enumeration - not alphabetical, and not guaranteed to
  place a newly-added file last - so a brand-new file could land anywhere relative to already-known
  ones, and a single-file action (skip, preset override, remove) touching one untouched file could
  let it jump ahead of still-untouched files sitting alongside it.
- A resume-tracking entry belonging to a Lane that's since been deleted or renamed no longer sits
  permanently stuck - it's now cleaned up automatically, instead of forever inflating the "Resuming
  previous incomplete run" count and blocking the automatic cleanup that clears finished history
  once nothing is genuinely outstanding.
- An in-progress file could, in rare cases, still show up in the Monitor page's queue as if it
  were waiting to be processed, instead of being recognized as the one currently encoding - a name
  collision between two different files in different Lane subfolders was enough to trigger it.
- A file could show a "Resumed" badge it didn't deserve - a single queue-editing action (skip,
  preset override, remove) elsewhere in the same Lane was enough to make every file in that Lane
  look like leftover work from an interrupted run, even when nothing had actually been
  interrupted.
- Daily/Weekly digest notifications always showed "Duration: 0s" regardless of how long the
  underlying runs actually took - the digest summary never tracked a duration at all until now.
- A queue-control edit (reorder, skip, preset override, or remove) made anywhere in the queue
  while a different file was still finishing its own routing, companion-file move, or Sonarr/Radarr
  rescan-confirmation wait (which can take up to about two minutes) could be silently lost the
  instant that other file's own result was saved - found by an external pre-release code review.
  Queue edits are now merged onto the freshest state on disk instead of being overwritten by a
  stale in-memory snapshot.
- Queue-control actions (reorder, skip, preset override, remove) identified a file by its
  filename alone, which could target the wrong file if two different files in different lane
  subfolders happened to share the same name - also found by the same review. These now match on
  the file's full path instead.
- A hung or runaway post-execution command now times out (5 minutes) and is killed instead of
  blocking that pass's report and notifications indefinitely, with nothing - not even Abort - able
  to interrupt it before.
- resume.json is now written atomically (write-to-temp-then-rename) instead of in place,
  preventing a corrupted or truncated resume file if Compressarr is killed or crashes mid-write -
  this file is rewritten after every single file and every queue-control edit, so it's frequent
  enough to matter.
- HandBrakeCLI's stderr stream is now fully drained before being read, closing a rare timing gap
  where its own "Finished work at" completion line - read to determine success - might not have
  fully arrived yet, a plausible source of an occasional false "encode failed" result.
- FileBot is now launched with its arguments passed individually instead of built into one
  manually-quoted command-line string, removing a class of quoting problems from paths or
  arguments containing spaces or special characters.
- Sonarr/Radarr URLs are now validated and composed through .NET's own URL handling instead of
  bare string concatenation, rejecting a malformed URL up front with a clear error instead of
  failing unpredictably later - a custom URL Base (for a reverse-proxy setup) is still respected.
- Aborting a run or stopping monitoring now actually cancels an in-flight Sonarr/Radarr API call
  immediately, instead of waiting for it to finish on its own (up to 15 seconds) before the
  abort/stop took effect for that file.
- Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.
- A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".
- A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.
- A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.
- The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.
- Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.
- Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's own arguments are passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

## [2.1.7] - 2026-09-27

> [!TIP]
> **What's new in 2.1.7:** the Monitor page's queue order is now permanently locked the moment a
> file first appears - only a manual reorder (drag, or move to top/bottom) can ever change it
> again, and a newly-scanned file always joins at the end instead of possibly landing ahead of
> files already in the queue. Settings' Web UI section also gets a persistent "Install as app"
> card for installing (or getting uninstall instructions for) the mobile-friendly PWA on demand,
> replacing the old one-time toolbar banner; a new opt-in `{file_list}` notification token for
> Custom templates that want filenames; and a close button on every HTML report. Also fixes a
> digest notification that always showed 0s for Duration, plus three queue-display bugs (a stale
> "Resumed" badge, an in-progress file that could still show as queued, and orphaned tracking
> entries left behind by a deleted Lane). Everything else below carries forward from 2.1.6 for
> context - new changes are in **bold**.

### Added
- **A persistent "Install as app" card in Settings > Web UI: installs the mobile-friendly PWA
  on demand (Chrome/Edge/Android), shows "Add to Home Screen" instructions on iOS (no installable
  prompt exists there), or - if already running as the installed app - explains how to uninstall
  it from the browser's own app list, since no web page can trigger that directly.**
- **A new opt-in `{file_list}` notification token: this run's filenames, one per line. Deliberately
  left out of every built-in Minimal/Standard/Detailed template - it only ever reaches a message
  if you type it into a Custom template yourself, since it's the one token that can put a media
  filename in front of wherever that channel sends its message.**
- **A close button on every HTML report - most useful in PWA/standalone mode, which has no browser
  chrome at all to close a tab with, but available on desktop too.**
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- **The first-launch "Install as app" toolbar banner is retired, superseded by the persistent
  Settings > Web UI card above - the underlying install capability is unchanged, just no longer
  a one-time, dismiss-and-it's-gone prompt.**
- **The "Original v1.1 (PowerShell)" credit and link are removed from the About page - v1.1 is no
  longer referenced anywhere in the app.**
- Monitor is now the default page when Compressarr starts (opening the web UI or the tray
  icon's "Open Web UI"), instead of Settings - a one-time setup page isn't where you check in
  day-to-day.
- A configuration change saved while monitoring is already running now actually takes effect on
  the next pass, instead of silently having no effect until monitoring is stopped and restarted or
  Compressarr itself is restarted - this covers Notifications, Report, PostExec, and Logging
  settings, and the Lanes list itself (add/remove/reorder/enable/disable).
- The whole app is usable on a phone or tablet: the sidebar becomes a slide-out drawer below
  about 760px width, the toolbar's action buttons and status cluster wrap onto their own rows
  instead of overlapping, and Monitor's queue rows reflow into a two-line card layout. Requires no
  extra setup - just open Compressarr's normal address in a mobile browser.
- The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- **A file's position in the Monitor page's queue is now permanently locked the moment it first
  appears - only an explicit reorder (drag, or move to top/bottom, which is just a reorder under
  the hood) can ever change it again. Previously, a file with no explicit position sorted by live
  filesystem scan order, which is raw OS enumeration - not alphabetical, and not guaranteed to
  place a newly-added file last - so a brand-new file could land anywhere relative to already-known
  ones, and a single-file action (skip, preset override, remove) touching one untouched file could
  let it jump ahead of still-untouched files sitting alongside it.**
- **A resume-tracking entry belonging to a Lane that's since been deleted or renamed no longer sits
  permanently stuck - it's now cleaned up automatically, instead of forever inflating the "Resuming
  previous incomplete run" count and blocking the automatic cleanup that clears finished history
  once nothing is genuinely outstanding.**
- **An in-progress file could, in rare cases, still show up in the Monitor page's queue as if it
  were waiting to be processed, instead of being recognized as the one currently encoding - a name
  collision between two different files in different Lane subfolders was enough to trigger it.**
- **A file could show a "Resumed" badge it didn't deserve - a single queue-editing action (skip,
  preset override, remove) elsewhere in the same Lane was enough to make every file in that Lane
  look like leftover work from an interrupted run, even when nothing had actually been
  interrupted.**
- **Daily/Weekly digest notifications always showed "Duration: 0s" regardless of how long the
  underlying runs actually took - the digest summary never tracked a duration at all until now.**
- A queue-control edit (reorder, skip, preset override, or remove) made anywhere in the queue
  while a different file was still finishing its own routing, companion-file move, or Sonarr/Radarr
  rescan-confirmation wait (which can take up to about two minutes) could be silently lost the
  instant that other file's own result was saved - found by an external pre-release code review.
  Queue edits are now merged onto the freshest state on disk instead of being overwritten by a
  stale in-memory snapshot.
- Queue-control actions (reorder, skip, preset override, remove) identified a file by its
  filename alone, which could target the wrong file if two different files in different lane
  subfolders happened to share the same name - also found by the same review. These now match on
  the file's full path instead.
- A hung or runaway post-execution command now times out (5 minutes) and is killed instead of
  blocking that pass's report and notifications indefinitely, with nothing - not even Abort - able
  to interrupt it before.
- resume.json is now written atomically (write-to-temp-then-rename) instead of in place,
  preventing a corrupted or truncated resume file if Compressarr is killed or crashes mid-write -
  this file is rewritten after every single file and every queue-control edit, so it's frequent
  enough to matter.
- HandBrakeCLI's stderr stream is now fully drained before being read, closing a rare timing gap
  where its own "Finished work at" completion line - read to determine success - might not have
  fully arrived yet, a plausible source of an occasional false "encode failed" result.
- FileBot is now launched with its arguments passed individually instead of built into one
  manually-quoted command-line string, removing a class of quoting problems from paths or
  arguments containing spaces or special characters.
- Sonarr/Radarr URLs are now validated and composed through .NET's own URL handling instead of
  bare string concatenation, rejecting a malformed URL up front with a clear error instead of
  failing unpredictably later - a custom URL Base (for a reverse-proxy setup) is still respected.
- Aborting a run or stopping monitoring now actually cancels an in-flight Sonarr/Radarr API call
  immediately, instead of waiting for it to finish on its own (up to 15 seconds) before the
  abort/stop took effect for that file.
- Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.
- A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".
- A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.
- A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.
- The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.
- Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.
- Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's own arguments are passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

## [2.1.6] - 2026-09-18

> [!TIP]
> **What's new in 2.1.6:** per-run notification messages are now fully customizable (built-in
> Minimal/Standard/Detailed presets or a free-text Custom template, with 18 fill-in tokens), and
> Compressarr can now be installed and used as an app on a phone or tablet, with a responsive
> layout throughout. A config change made while monitoring is already running now actually takes
> effect on the next pass instead of requiring a restart, and Monitor - not Settings - is now the
> default page. Also ships a round of reliability hardening (a hung post-exec command, a
> resume.json corruption risk, and a rare HandBrake false-failure race are all fixed) plus two
> queue-editing bugs an external pre-release code review found: a concurrent queue edit could be
> silently lost while another file was still finishing up, and two same-named files in different
> lane folders could be mismatched. Everything else below carries forward from 2.1.5 for context -
> new changes are in **bold**.

### Added
- **Customizable per-run notification message format (Settings > Notifications, per channel):
  choose a Minimal/Standard/Detailed built-in preset, or write your own free-text Custom template -
  both filled in through the same set of 18 tokens (run number, files processed, size saved in
  GB/percent, before/after size, duration, outcome, error/warning counts, retries succeeded, report
  path, and today/month/year rollups of files processed and space saved). Scoped to the per-run
  notification only - the desktop toast and the Daily/Weekly Digest keep their existing fixed
  wording, and the Generic Webhook's JSON payload is untouched since it's structured data, not
  prose.**
- **Compressarr can now be installed as an app from a phone or tablet's browser (Add to Home
  Screen / install prompt), running standalone with its own icon and no browser chrome.**
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- **Monitor is now the default page when Compressarr starts (opening the web UI or the tray
  icon's "Open Web UI"), instead of Settings - a one-time setup page isn't where you check in
  day-to-day.**
- **A configuration change saved while monitoring is already running now actually takes effect on
  the next pass, instead of silently having no effect until monitoring is stopped and restarted or
  Compressarr itself is restarted - this covers Notifications, Report, PostExec, and Logging
  settings, and the Lanes list itself (add/remove/reorder/enable/disable).**
- **The whole app is now usable on a phone or tablet: the sidebar becomes a slide-out drawer below
  about 760px width, the toolbar's action buttons and status cluster wrap onto their own rows
  instead of overlapping, and Monitor's queue rows reflow into a two-line card layout. Requires no
  extra setup - just open Compressarr's normal address in a mobile browser.**
- The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- **A queue-control edit (reorder, skip, preset override, or remove) made anywhere in the queue
  while a different file was still finishing its own routing, companion-file move, or Sonarr/Radarr
  rescan-confirmation wait (which can take up to about two minutes) could be silently lost the
  instant that other file's own result was saved - found by an external pre-release code review.
  Queue edits are now merged onto the freshest state on disk instead of being overwritten by a
  stale in-memory snapshot.**
- **Queue-control actions (reorder, skip, preset override, remove) identified a file by its
  filename alone, which could target the wrong file if two different files in different lane
  subfolders happened to share the same name - also found by the same review. These now match on
  the file's full path instead.**
- **A hung or runaway post-execution command now times out (5 minutes) and is killed instead of
  blocking that pass's report and notifications indefinitely, with nothing - not even Abort - able
  to interrupt it before.**
- **resume.json is now written atomically (write-to-temp-then-rename) instead of in place,
  preventing a corrupted or truncated resume file if Compressarr is killed or crashes mid-write -
  this file is rewritten after every single file and every queue-control edit, so it's frequent
  enough to matter.**
- **HandBrakeCLI's stderr stream is now fully drained before being read, closing a rare timing gap
  where its own "Finished work at" completion line - read to determine success - might not have
  fully arrived yet, a plausible source of an occasional false "encode failed" result.**
- **FileBot is now launched with its arguments passed individually instead of built into one
  manually-quoted command-line string, removing a class of quoting problems from paths or
  arguments containing spaces or special characters.**
- **Sonarr/Radarr URLs are now validated and composed through .NET's own URL handling instead of
  bare string concatenation, rejecting a malformed URL up front with a clear error instead of
  failing unpredictably later - a custom URL Base (for a reverse-proxy setup) is still respected.**
- **Aborting a run or stopping monitoring now actually cancels an in-flight Sonarr/Radarr API call
  immediately, instead of waiting for it to finish on its own (up to 15 seconds) before the
  abort/stop took effect for that file.**
- Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.
- A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".
- A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.
- A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.
- The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.
- Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.
- Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's own arguments are passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

## [2.1.5] - 2026-09-10

> [!TIP]
> **What's new in 2.1.5:** a full reliability pass on the Sonarr/Radarr post-move handoff and its
> source-folder cleanup. The rescan Compressarr triggers after unmonitoring a file is now actually
> confirmed complete (real polling, not a blind wait), and every way that confirmation - or the
> cleanup itself - can be deferred now gets its own retry state instead of quietly being marked
> done: a stranded companion file, an unconfirmed rescan, or a cleanup that failed outright (a
> locked file, permissions, antivirus, a flaky network share) are all automatically retried on the
> lane's next pass, with no re-encode. A pass whose only work was one of these recoveries no longer
> looks like an empty poll - it keeps its log and gets a report. Also fixes destination-collision
> handling for a file resting in Output, and flags an unmatched quote in Extra CLI Options.
> Everything else below carries forward from 2.1.4 for context - new changes are in **bold**.

### Added
- A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.
- Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.
- Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.
- A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.
- A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.

### Changed
- **The HTML report's recovery banner and the activity it's based on now cover every kind of
  automatic retry - a failed move, a failed companion-file move, or a deferred Sonarr/Radarr
  rescan/cleanup - not just the first two.**
- Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.
- The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.
- Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- **Sonarr/Radarr's post-move library rescan is now actually confirmed complete (real polling of
  its own command status, replacing a blind fixed wait) before the now-empty source folder is
  removed - and if that confirmation times out, fails, or is cancelled, the folder is safely left
  in place and the confirmation is automatically retried on the lane's next pass, instead of
  risking Sonarr/Radarr losing track of the episode/movie because the folder was already gone when
  it rescanned.**
- **A source-folder cleanup that fails outright (a locked file, a permissions error, antivirus
  interference, a flaky network share, etc.) after a confirmed rescan is now retried the same way,
  instead of being silently abandoned - the file's report entry also shows a warning so it's
  visible that cleanup is still pending, rather than reading as a plain, finished "OK".**
- **A companion file (subtitle, .nfo, artwork) that fails to move alongside its video now gets its
  own retry state and is automatically retried on the lane's next pass, without re-encoding -
  previously it was left stranded in the source folder with only a log warning and no way to
  recover on its own.**
- **A monitoring pass whose only activity was successfully recovering a previously-stranded file
  (a failed move, a failed companion move, or a deferred Sonarr/Radarr confirmation/cleanup) no
  longer looks like an empty, idle poll - its log and an HTML report are kept, the same as a pass
  that processed brand-new files.**
- **The destination-collision setting (Rename/Skip) is now honored for a file resting directly in
  Output (MoveFiles off) or left in place after a routing failure - this path previously always
  overwrote regardless of what was configured, independent of the similar Rename/Skip fix already
  shipped in 2.1.4 for the normal routed-move path.**
- **Report generation for a persistent, unchanged lane configuration problem is now deduplicated
  the same way the matching log message already was, instead of writing a fresh report on every
  single poll for as long as the problem stays unresolved.**
- **Extra CLI Options with an unmatched quote (") are now flagged in Settings validation -
  everything after an unclosed quote would otherwise silently fold into a single argument instead
  of being split as intended.**
- The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.
- A failed file move (offline network drive, permissions, etc.) no longer deletes the source file
  before the move is retried - the source is preserved until the move actually succeeds, and a
  failed move is retried automatically on the lane's next pass without re-encoding. A related bug
  this fix exposed - a rescan could mistake that pending retry for a fresh file and force a full
  re-encode instead of just retrying the move - is fixed alongside it.
- Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.
- The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.
- Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.
- The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.
- HandBrakeCLI's arguments are now passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.
- Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback warnings)
  that used to fail silently are now logged instead of swallowed.
- A source folder could be left behind, empty, after all its files successfully moved out.
- A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.
- Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.
- The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.
- Several other save/action confirmations across the app were missing their green success
  styling.
- A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.
- Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.
- The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.

## [2.1.4] - 2026-09-10

> [!WARNING]
> **Critical bug fix — excessive log and report accumulation.** Earlier versions could write a
> brand-new log file *and* HTML report on every single monitoring pass - as often as once a
> minute - whenever the same problem kept recurring (a misconfigured lane, a destination that
> stayed unreachable for an extended period, etc.). Over weeks or months this could leave
> thousands of near-duplicate files sitting in your Logs and Reports folders (by default,
> `%APPDATA%\Compressarr\Logs` and `%APPDATA%\Compressarr\Reports` - check Settings > Logging &
> Reports if you've customized either path). This is now fixed: a repeating problem is only
> logged once, not on every poll.
>
> If you've been running an earlier version for a while, you may already have a large backlog of
> these files. This release adds a **Purge Logs & Reports** button (Settings > Maintenance) that
> permanently deletes every log, every report, and the run-history CSV in one fast pass -
> bypassing the Recycle Bin, so it won't bog down even on a folder with thousands of files.
> Recommended once after upgrading, to clear out anything the old bug left behind.

> [!TIP]
> **What's new in 2.1.4:** configuration validation with red-outlined fields and a consolidated
> toolbar status message across Settings, Lanes, and Notifications, numbered error-code badges on
> the HTML run report, a "Launch Monitor at Startup" option, and an important data-safety fix so a
> failed file move can no longer lose the source file. Destination-collision handling (Rename/Skip)
> now actually applies instead of silently behaving like Overwrite, and companion files follow the
> same setting as their video. Sonarr/Radarr's post-move rescan now waits to finish and the source
> folder isn't removed until after it does, so a rescan can no longer see a disconnected folder
> instead of a genuinely empty one. Everything else below carries forward from 2.1.3 for context -
> new changes are in **bold**.

### Added
- **A "Launch Monitor at Startup" option (Settings > Monitoring): opens the Monitor page in your
  default browser as soon as Compressarr launches, independent of whether monitoring itself
  auto-starts.**
- **Configuration validation: required fields on Settings and Lanes (HandBrake/FileBot paths,
  video extensions, a lane's preset or Output folder) are checked on load and on save, with
  invalid fields outlined in red and a toolbar error message pointing you to them. Notifications
  validates its own required fields (e.g. a channel's webhook URL) the same way before you save.
  Saving still succeeds either way - this is a warning, not a gate - matching how Test Connection
  and other checks already behave in this app.**
- **Numbered error-code badges (101-110) on the HTML run report, with a hover tooltip explaining
  each one - covers missing HandBrake/presets, a misconfigured lane, and FileBot path problems, so
  a problem is identifiable from the report itself, not just the log.**
- **A "Keep Logs of successful HandBrake Encodes" setting (Settings > Maintenance, off by default),
  so successful-encode detail logs don't accumulate forever - failed-encode logs are always kept
  since the report links to them.**
- **A "Purge Logs & Reports" button (Settings > Maintenance) - same as Clear Logs + Clear History
  combined, but a permanent delete instead of Recycle Bin, for a faster cleanup on a large
  accumulated backlog.**

### Changed
- **Every page's status/save messages now show in the toolbar, replacing each page's own scattered
  status element(s) - the same consistent place across Settings, Lanes, and Notifications.**
- **The toolbar shows elapsed time for the run currently in progress ("Monitoring is ON: Running
  (Time Elapsed: 2 hrs, 5 min 10 sec)"), ticking up live instead of only updating once per poll.**
- **Checking for updates now happens immediately when Compressarr starts, instead of only relying
  on a browser cache that could keep showing "update available" for up to a day after you'd
  already upgraded.**
- Donate page crypto address cards are more compact and show a truncated address (full address on
  hover, copy, and in the QR modal) so all six currencies fit in a single row.

### Fixed
- **The sidebar's red History error/warning badges had no way to clear - they now go away once
  you've opened the History page and its Reports list has loaded, and stay cleared until a new run
  has an error or warning.**
- **A failed file move (offline network drive, permissions, etc.) no longer deletes the source
  file before the move is retried - the source is preserved until the move actually succeeds, and
  a failed move is retried automatically on the lane's next pass without re-encoding. A related
  bug this fix exposed - a rescan could mistake that pending retry for a fresh file and force a
  full re-encode instead of just retrying the move - is fixed alongside it.**
- **Sonarr/Radarr are no longer unmonitored for a file whose move to its destination failed - only
  once the move actually succeeds.**
- **The destination-collision setting (Rename/Skip) now actually applies - previously the staged
  output file was always given a fresh temporary name before the collision check ran, so Rename
  and Skip both behaved like Overwrite in practice.**
- **Companion files (subtitles, .nfo, artwork) now follow the same "On destination collision"
  setting as their video, and always take the video's own resulting filename (including any
  Rename-mode suffix), so a renamed video and its companions stay matched.**
- **The library scanner now skips reparse points (junctions/symlinks) and tracks visited folders,
  preventing runaway or duplicate scanning through a symlink loop.**
- **HandBrakeCLI's arguments are now passed individually instead of built into one manually-quoted
  string, removing a class of quoting problems from paths or preset names with spaces or special
  characters.**
- **Several cleanup steps (removing temp files, HandBrake detail logs, and trash-fallback
  warnings) that used to fail silently are now logged instead of swallowed.**
- **A source folder could be left behind, empty, after all its files successfully moved out.**
- **A monitor pass that keeps failing the same way (e.g. a lane with no usable preset) no longer
  writes a fresh log entry and report on every single pass.**
- **Installing or merging a new HandBrake preset didn't refresh the cached preset list, so it
  didn't show up in the Lanes page's preset dropdowns until a separate manual reload.**
- **The Lanes page's own save confirmation never turned green like it does on Settings and
  Notifications.**
- **Several other save/action confirmations across the app were missing their green success
  styling.**
- **A converted file's original source is no longer stripped of its title metadata before
  encoding - only the actual converted output ever gets its title tag cleared.**
- **Sonarr/Radarr's own library rescan (triggered right after unmonitoring) now waits for the
  scan to actually finish before moving on to the next file, instead of firing it and
  continuing immediately.**
- **The now-empty source folder is no longer removed until after Sonarr/Radarr's unmonitor and
  rescan have completed - removing it any earlier could make the rescan see a disconnected
  folder instead of a genuinely empty one, which could leave the episode/movie incorrectly
  still showing as present.**

## [2.1.3] - 2026-09-07

> [!TIP]
> **What's new in 2.1.3:** optional FileBot integration to rename/organize files before
> processing, daily/weekly digest notifications on top of per-run alerts, a live queue file count
> with a configurable Queue Completion display (date/time or countdown), a real trusted
> publisher signature in place of the old self-signed certificate, and the bundled installer
> option is back. Everything else below carries forward from 2.1.2 for context - new changes are
> in **bold**.

### Added
- **Compressarr is now signed with a real, trusted publisher certificate instead of the previous
  self-signed one, via Azure Trusted Signing.**
- **The bundled installer (`Compressarr-Setup-{version}-Full.exe`) is back as a second, permanent
  download option alongside the regular installer - it includes its own copy of the .NET runtime,
  so nothing else needs to be installed first, at the cost of a much larger download. It was
  dropped after 2.1.0 due to a Windows Defender reputation flag that a self-signed certificate
  contributed to; the real certificate above resolves that. Pick whichever fits: the regular,
  smaller installer if you already have (or don't mind installing) the .NET runtime it needs, or
  the Full one if you'd rather not deal with that at all.**
- **Optional FileBot pre-processing (Settings > File Name Processing): for users who don't run
  Sonarr/Radarr, Compressarr can shell out to the free [FileBot](https://www.filebot.net/) tool to
  rename and organize TV episodes and movies before scanning a lane's Input folder - separate
  enable toggles and Arguments for TV vs. Movies, a numbering-style picker (S01E01 vs. 1x01), a
  Fix Network button for FileBot's own common "Unable to establish loopback connection" issue, and
  an amber "Unmatched" badge on any queued file FileBot couldn't confidently rename.**
- **Daily and/or weekly digest notifications on every notification channel and the desktop toast,
  independent of the existing per-run trigger - a periodic summary ("Compressed N files, reducing
  original size from X GB to Y GB, saving Z% of original size") instead of, or alongside, a message
  after every single run. Each channel gets its own Daily/Weekly toggle, time, and day, plus
  Test/Save/Clear controls right on the row.**
- **Queue ETA on the Monitor page's state card ("Queue Completion"), estimated from real observed
  encode throughput - shows "Estimating" until enough data exists to calculate from. Configurable
  in Settings to display as an absolute date/time or a countdown duration (e.g. "2d 5h 36m").**
- **"Move to top" / "Move to bottom" in the queue item's 3-dot menu, for quickly repositioning a
  file in a long queue without a long drag.**
- **The Monitor page's "In Queue" heading now shows a live file count ("N Files In Queue").**
- **Configurable companion file extensions and unmatched-companion-file handling
  (Maintain/Delete/Recycle) in Settings, instead of a fixed built-in list.**
- **Clear Logs, Clear History, and Clear All buttons on Settings' Maintenance card.**
- Per-queue-item controls on the Monitor page: drag a file to reorder it within its lane, skip it
  (stays visible, dimmed, excluded from processing until un-skipped), remove it from the queue
  entirely, or override its preset for that one file only from a dropdown of installed presets - a
  "Use Lane Preset" option resets an override back to the lane default.
- Error queue entries are now shown on Monitor (red badge) with a Remove action, instead of being
  invisible until the next report.
- Failed file *moves* (encode succeeded, but couldn't be filed into the library - an offline
  network drive, etc.) are now retried automatically on the lane's next pass, without re-encoding.
- Configurable behavior when a destination file already exists - Overwrite (previous behavior,
  still the default), Skip, or Rename - instead of always silently overwriting.
- Automated backups (Settings > Backups): scheduled zip backups of your full setup (settings,
  lanes, resume state, run counter, history) to a local or network folder, plus a "Backup Now"
  button and a list of existing backups you can restore from with one click - including on a
  brand-new install, before you've configured anything else.
- Export/import your full configuration as a single file from Settings, for backing up or moving
  to a new machine.
- Test Connection button next to the Sonarr/Radarr integration settings, so a bad URL or API key
  shows up immediately instead of only at unmonitor-time during a real run.
- A warning before leaving Settings or Lanes with unsaved changes, plus a Clear Changes button to
  discard edits in place.
- Pause/Resume for the file currently being converted.
- KB/MB/GB unit dropdown next to Settings' Minimum size field (previously bytes only).
- Per-file conversion duration on the HTML report, and a running total time on the History page.
- The Monitor page's status now shows which preset the current file is using.
- A completely redesigned web UI: a left sidebar for navigation (in place of the old top tab bar),
  a persistent toolbar showing monitoring status and CPU usage on every page, and a consistent
  card-based layout across Settings, Lanes, History, and About.
- A Donate page with QR codes and one-click copy for several cryptocurrency addresses.
- A small indicator appears in the toolbar when a newer version of Compressarr is available.
- Notification channels (Notifications page): get a message when a run completes via Discord,
  Slack, Telegram, Pushover, ntfy, Gotify, Notifiarr, IFTTT, or a custom webhook (which also
  covers Zapier, Make, n8n, Node-RED, and Home Assistant) - configure as many channels as you
  want, each with its own trigger (always / only on error or warning / never) and a Test button.
  A separate toggle controls the existing Windows toast notification, now off by default. Every
  field has a help bubble explaining what it needs and where to find it.
- True cross-lane queue priority: dragging a file in the Monitor page's queue can now move it
  ahead of files in a *different* Lane, not just within its own Lane - the order shown is exactly
  the order files will be processed in, regardless of which Lane each one belongs to.
- A [detailed GitHub Wiki](https://github.com/Compressarr/Compressarr/wiki) with a full walkthrough
  of every page, written for people new to Compressarr.

### Changed
- **Settings saves now show a clear green confirmation instead of no feedback at all - both at the
  top of the page and directly on the row you just changed (e.g. a notification channel's own Save
  button).**
- **Page status messages now also mirror into the toolbar, so a confirmation like "Settings saved"
  stays visible even after scrolling down a long page.**
- Start/Stop Monitoring is now a single toggle button instead of two separate ones.
- Stop Monitoring now stops after the file currently converting finishes, rather than continuing
  to process every other file still queued behind it.
- Subtitle and other companion files now move to their destination immediately once their own
  video finishes converting, instead of waiting for every file in a shared folder to finish first.
- The queue's preset picker is a plain dropdown showing the preset actually in effect, instead of
  a custom popover that could show a stale or misleading placeholder.

### Fixed
- **FileBot's own console window no longer flashes on screen during an otherwise-background
  automated run.**
- **A file FileBot confirmed was already correctly named (it logs "already exists" for these) was
  incorrectly flagged "Unmatched" in the queue instead of being recognized as a real match.**
- **The Monitor page's "Renaming" state now appears as soon as FileBot actually starts, not after
  the fact - fixed a real blocking bug where starting monitoring could hang the page's very first
  status update for the full duration of FileBot's own work before anything appeared to happen.**
- **The post-execution command's own process launch could also flash a console window, the same
  root cause as FileBot's.**
- **A movie filename containing a raw resolution tag (e.g. an older DivX-era rip with "720x480" in
  the name) could be misdetected as a TV episode and routed into a nonsense "Season 720" folder.**
- **Log/report retention set to "0 days" deleted everything immediately instead of the intuitive
  "keep forever."**
- **The Monitor page's queue completion estimate could disappear once only a few files remained in
  the queue.**
- A queue edit (reorder, skip, or preset override) made while a file was actively converting could
  be silently discarded once that file finished, and the wrong file could be processed next.
- Removing a file from the queue didn't stick - it could reappear within seconds.
- The queue's preset dropdown or its right-click-style menu could be yanked shut mid-interaction
  by the page's own periodic refresh.
- A queue edit could reorder the whole queue as a side effect, or make untouched files incorrectly
  show as "Resumed" instead of "New."
- The In Queue list could freeze while its own lane's pass was actively running.
- A stale Error entry whose source file was already gone (deleted by hand, or handled elsewhere)
  never cleared itself the way a stale queued entry already did, and could permanently inflate the
  "resuming previous run" count on every single pass.
- Reordering, skipping, removing, or overriding the preset for a file that lives in a subfolder
  under a Lane's Input folder (rather than directly in it - e.g. one folder per movie) silently
  did nothing, with no error shown.
- Resolved a false-positive `Trojan:Win32/Wacatac.B!ml` flag from one vendor on v2.1.0's
  installer. Root-caused through systematic isolation testing against VirusTotal to the
  installer's LZMA2 compression of the embedded application payload, not to any notification
  code, service, or architecture - confirmed by an A/B test where an identical build scanned
  clean the moment compression was disabled. The installer now ships uncompressed
  (`Compression=none`) as a result; every notification channel, including Discord and Slack,
  remains fully intact.
- Along the way, the notification providers (Discord, Slack, Telegram, Pushover, ntfy, Gotify,
  Notifiarr, IFTTT) were also rewritten onto a narrower, intentionally boring HTTP client
  interface (fixed JSON/form/text POST shapes, never a fully generic method+headers+content-type
  sender) instead of sharing one universal webhook-sending routine - a deliberate architecture
  improvement independent of the VirusTotal finding above. Generic Webhook keeps its own
  fully-flexible sender, since it's the one channel that genuinely needs arbitrary
  method/header/URL configurability.
- Resolved a second, unrelated false positive (`Program:Win32/Contebrew.A!ml`) that Windows
  Defender's live cloud/SmartScreen reputation classifier flagged on a real download of the
  self-contained installer, despite VirusTotal - including a same-day re-scan with Microsoft's own
  engine - showing it completely clean. That classifier weighs signals VirusTotal's static engine
  never sees: publisher trust (this project's cert is self-signed, so it starts with none) and how
  new/large/rarely-downloaded a file is. Rather than chase a live reputation heuristic, Compressarr
  now ships a single, much smaller framework-dependent installer instead of the self-contained
  build - removing the exposure rather than working around it. See Installation below for the
  runtime it now requires.
- Upgrading from a self-contained install (v2.1.0, or the briefly-shipped self-contained 2.1.1
  build) left `coreclr.dll`/`hostfxr.dll`/`hostpolicy.dll` behind in the install folder, since an
  in-place upgrade only overwrites files the new package ships - it never removes files that
  belonged only to the old one. .NET's host then treated the install folder itself as a
  self-contained runtime root and failed to find the real machine-wide runtime, showing "You must
  install or update .NET" even on a machine with the correct runtime properly installed. The
  installer now runs the previous version's own uninstaller before installing, guaranteeing a
  clean upgrade every time.

### Security
- Donation addresses on the Donate page are no longer stored as single literal strings in the
  compiled binary - a reasonable hardening measure.

## [2.1.2] - 2026-09-06

> [!TIP]
> **What's new in 2.1.2:** movies no longer get silently misrouted into an unrelated movie's own
> folder. Everything else below carries forward from 2.1.1 for context - new changes are in
> **bold**.

### Added
- Per-queue-item controls on the Monitor page: drag a file to reorder it within its lane, skip it
  (stays visible, dimmed, excluded from processing until un-skipped), remove it from the queue
  entirely, or override its preset for that one file only from a dropdown of installed presets - a
  "Use Lane Preset" option resets an override back to the lane default.
- Error queue entries are now shown on Monitor (red badge) with a Remove action, instead of being
  invisible until the next report.
- Failed file *moves* (encode succeeded, but couldn't be filed into the library - an offline
  network drive, etc.) are now retried automatically on the lane's next pass, without re-encoding.
- Configurable behavior when a destination file already exists - Overwrite (previous behavior,
  still the default), Skip, or Rename - instead of always silently overwriting.
- Automated backups (Settings > Backups): scheduled zip backups of your full setup (settings,
  lanes, resume state, run counter, history) to a local or network folder, plus a "Backup Now"
  button and a list of existing backups you can restore from with one click - including on a
  brand-new install, before you've configured anything else.
- Export/import your full configuration as a single file from Settings, for backing up or moving
  to a new machine.
- Test Connection button next to the Sonarr/Radarr integration settings, so a bad URL or API key
  shows up immediately instead of only at unmonitor-time during a real run.
- A warning before leaving Settings or Lanes with unsaved changes, plus a Clear Changes button to
  discard edits in place.
- Pause/Resume for the file currently being converted.
- KB/MB/GB unit dropdown next to Settings' Minimum size field (previously bytes only).
- Per-file conversion duration on the HTML report, and a running total time on the History page.
- The Monitor page's status now shows which preset the current file is using.
- A completely redesigned web UI: a left sidebar for navigation (in place of the old top tab bar),
  a persistent toolbar showing monitoring status and CPU usage on every page, and a consistent
  card-based layout across Settings, Lanes, History, and About.
- A Donate page with QR codes and one-click copy for several cryptocurrency addresses.
- A small indicator appears in the toolbar when a newer version of Compressarr is available.
- Notification channels (Notifications page): get a message when a run completes via Discord,
  Slack, Telegram, Pushover, ntfy, Gotify, Notifiarr, IFTTT, or a custom webhook (which also
  covers Zapier, Make, n8n, Node-RED, and Home Assistant) - configure as many channels as you
  want, each with its own trigger (always / only on error or warning / never) and a Test button.
  A separate toggle controls the existing Windows toast notification, now off by default. Every
  field has a help bubble explaining what it needs and where to find it.
- True cross-lane queue priority: dragging a file in the Monitor page's queue can now move it
  ahead of files in a *different* Lane, not just within its own Lane - the order shown is exactly
  the order files will be processed in, regardless of which Lane each one belongs to.
- A [detailed GitHub Wiki](https://github.com/Compressarr/Compressarr/wiki) with a full walkthrough
  of every page, written for people new to Compressarr.

### Changed
- Start/Stop Monitoring is now a single toggle button instead of two separate ones.
- Stop Monitoring now stops after the file currently converting finishes, rather than continuing
  to process every other file still queued behind it.
- Subtitle and other companion files now move to their destination immediately once their own
  video finishes converting, instead of waiting for every file in a shared folder to finish first.
- The queue's preset picker is a plain dropdown showing the preset actually in effect, instead of
  a custom popover that could show a stale or misleading placeholder.

### Fixed
- **Movies could get silently misrouted into an unrelated movie's own destination folder instead
  of landing directly under the lane's Movie base path. `MoveMovieFile` tried to auto-detect a
  "bucket" folder (e.g. `01. Movies 1920-1979`, for libraries organized into year-range
  folders) by scanning the destination for any folder whose name merely contained the word
  "movie" - but an ordinary movie's own folder can just as easily match that (a title like
  `Scary Movie (2026)` contains "Movie"), and once that was the only such folder present, every
  subsequent movie got nested inside it instead of getting its own folder. Confirmed happening
  in production. Bucket/range folders were never actually used, so the auto-detection was
  removed entirely rather than made stricter - movies now always land directly under the
  configured Movie base path, each in its own folder, with no exceptions.**
- A queue edit (reorder, skip, or preset override) made while a file was actively converting could
  be silently discarded once that file finished, and the wrong file could be processed next.
- Removing a file from the queue didn't stick - it could reappear within seconds.
- The queue's preset dropdown or its right-click-style menu could be yanked shut mid-interaction
  by the page's own periodic refresh.
- A queue edit could reorder the whole queue as a side effect, or make untouched files incorrectly
  show as "Resumed" instead of "New."
- The In Queue list could freeze while its own lane's pass was actively running.
- A stale Error entry whose source file was already gone (deleted by hand, or handled elsewhere)
  never cleared itself the way a stale queued entry already did, and could permanently inflate the
  "resuming previous run" count on every single pass.
- Reordering, skipping, removing, or overriding the preset for a file that lives in a subfolder
  under a Lane's Input folder (rather than directly in it - e.g. one folder per movie) silently
  did nothing, with no error shown.
- Resolved a false-positive `Trojan:Win32/Wacatac.B!ml` flag from one vendor on v2.1.0's
  installer. Root-caused through systematic isolation testing against VirusTotal to the
  installer's LZMA2 compression of the embedded application payload, not to any notification
  code, service, or architecture - confirmed by an A/B test where an identical build scanned
  clean the moment compression was disabled. The installer now ships uncompressed
  (`Compression=none`) as a result; every notification channel, including Discord and Slack,
  remains fully intact.
- Along the way, the notification providers (Discord, Slack, Telegram, Pushover, ntfy, Gotify,
  Notifiarr, IFTTT) were also rewritten onto a narrower, intentionally boring HTTP client
  interface (fixed JSON/form/text POST shapes, never a fully generic method+headers+content-type
  sender) instead of sharing one universal webhook-sending routine - a deliberate architecture
  improvement independent of the VirusTotal finding above. Generic Webhook keeps its own
  fully-flexible sender, since it's the one channel that genuinely needs arbitrary
  method/header/URL configurability.
- Resolved a second, unrelated false positive (`Program:Win32/Contebrew.A!ml`) that Windows
  Defender's live cloud/SmartScreen reputation classifier flagged on a real download of the
  self-contained installer, despite VirusTotal - including a same-day re-scan with Microsoft's own
  engine - showing it completely clean. That classifier weighs signals VirusTotal's static engine
  never sees: publisher trust (this project's cert is self-signed, so it starts with none) and how
  new/large/rarely-downloaded a file is. Rather than chase a live reputation heuristic, Compressarr
  now ships a single, much smaller framework-dependent installer instead of the self-contained
  build - removing the exposure rather than working around it. See Installation below for the
  runtime it now requires.
- Upgrading from a self-contained install (v2.1.0, or the briefly-shipped self-contained 2.1.1
  build) left `coreclr.dll`/`hostfxr.dll`/`hostpolicy.dll` behind in the install folder, since an
  in-place upgrade only overwrites files the new package ships - it never removes files that
  belonged only to the old one. .NET's host then treated the install folder itself as a
  self-contained runtime root and failed to find the real machine-wide runtime, showing "You must
  install or update .NET" even on a machine with the correct runtime properly installed. The
  installer now runs the previous version's own uninstaller before installing, guaranteeing a
  clean upgrade every time.

### Security
- Donation addresses on the Donate page are no longer stored as single literal strings in the
  compiled binary - a reasonable hardening measure.

## [2.1.1] - 2026-09-05

> [!TIP]
> **What's new in 2.1.1:** two Windows Defender false positives resolved
> (`Trojan:Win32/Wacatac.B!ml`, `Program:Win32/Contebrew.A!ml`), a cleaner installer upgrade
> path, and hardened donation-address storage. Everything else below carries forward from 2.1.0
> for context - new changes are in **bold**.

### Added
- Per-queue-item controls on the Monitor page: drag a file to reorder it within its lane, skip it
  (stays visible, dimmed, excluded from processing until un-skipped), remove it from the queue
  entirely, or override its preset for that one file only from a dropdown of installed presets - a
  "Use Lane Preset" option resets an override back to the lane default.
- Error queue entries are now shown on Monitor (red badge) with a Remove action, instead of being
  invisible until the next report.
- Failed file *moves* (encode succeeded, but couldn't be filed into the library - an offline
  network drive, etc.) are now retried automatically on the lane's next pass, without re-encoding.
- Configurable behavior when a destination file already exists - Overwrite (previous behavior,
  still the default), Skip, or Rename - instead of always silently overwriting.
- Automated backups (Settings > Backups): scheduled zip backups of your full setup (settings,
  lanes, resume state, run counter, history) to a local or network folder, plus a "Backup Now"
  button and a list of existing backups you can restore from with one click - including on a
  brand-new install, before you've configured anything else.
- Export/import your full configuration as a single file from Settings, for backing up or moving
  to a new machine.
- Test Connection button next to the Sonarr/Radarr integration settings, so a bad URL or API key
  shows up immediately instead of only at unmonitor-time during a real run.
- A warning before leaving Settings or Lanes with unsaved changes, plus a Clear Changes button to
  discard edits in place.
- Pause/Resume for the file currently being converted.
- KB/MB/GB unit dropdown next to Settings' Minimum size field (previously bytes only).
- Per-file conversion duration on the HTML report, and a running total time on the History page.
- The Monitor page's status now shows which preset the current file is using.
- A completely redesigned web UI: a left sidebar for navigation (in place of the old top tab bar),
  a persistent toolbar showing monitoring status and CPU usage on every page, and a consistent
  card-based layout across Settings, Lanes, History, and About.
- A Donate page with QR codes and one-click copy for several cryptocurrency addresses.
- A small indicator appears in the toolbar when a newer version of Compressarr is available.
- Notification channels (Notifications page): get a message when a run completes via Discord,
  Slack, Telegram, Pushover, ntfy, Gotify, Notifiarr, IFTTT, or a custom webhook (which also
  covers Zapier, Make, n8n, Node-RED, and Home Assistant) - configure as many channels as you
  want, each with its own trigger (always / only on error or warning / never) and a Test button.
  A separate toggle controls the existing Windows toast notification, now off by default. Every
  field has a help bubble explaining what it needs and where to find it.
- True cross-lane queue priority: dragging a file in the Monitor page's queue can now move it
  ahead of files in a *different* Lane, not just within its own Lane - the order shown is exactly
  the order files will be processed in, regardless of which Lane each one belongs to.
- A [detailed GitHub Wiki](https://github.com/Compressarr/Compressarr/wiki) with a full walkthrough
  of every page, written for people new to Compressarr.

### Changed
- Start/Stop Monitoring is now a single toggle button instead of two separate ones.
- Stop Monitoring now stops after the file currently converting finishes, rather than continuing
  to process every other file still queued behind it.
- Subtitle and other companion files now move to their destination immediately once their own
  video finishes converting, instead of waiting for every file in a shared folder to finish first.
- The queue's preset picker is a plain dropdown showing the preset actually in effect, instead of
  a custom popover that could show a stale or misleading placeholder.

### Fixed
- A queue edit (reorder, skip, or preset override) made while a file was actively converting could
  be silently discarded once that file finished, and the wrong file could be processed next.
- Removing a file from the queue didn't stick - it could reappear within seconds.
- The queue's preset dropdown or its right-click-style menu could be yanked shut mid-interaction
  by the page's own periodic refresh.
- A queue edit could reorder the whole queue as a side effect, or make untouched files incorrectly
  show as "Resumed" instead of "New."
- The In Queue list could freeze while its own lane's pass was actively running.
- A stale Error entry whose source file was already gone (deleted by hand, or handled elsewhere)
  never cleared itself the way a stale queued entry already did, and could permanently inflate the
  "resuming previous run" count on every single pass.
- Reordering, skipping, removing, or overriding the preset for a file that lives in a subfolder
  under a Lane's Input folder (rather than directly in it - e.g. one folder per movie) silently
  did nothing, with no error shown.
- **Resolved a false-positive `Trojan:Win32/Wacatac.B!ml` flag from one vendor on v2.1.0's
  installer. Root-caused through systematic isolation testing against VirusTotal to the
  installer's LZMA2 compression of the embedded application payload, not to any notification
  code, service, or architecture - confirmed by an A/B test where an identical build scanned
  clean the moment compression was disabled. The installer now ships uncompressed
  (`Compression=none`) as a result; every notification channel, including Discord and Slack,
  remains fully intact.**
- **Along the way, the notification providers (Discord, Slack, Telegram, Pushover, ntfy, Gotify,
  Notifiarr, IFTTT) were also rewritten onto a narrower, intentionally boring HTTP client
  interface (fixed JSON/form/text POST shapes, never a fully generic method+headers+content-type
  sender) instead of sharing one universal webhook-sending routine - a deliberate architecture
  improvement independent of the VirusTotal finding above. Generic Webhook keeps its own
  fully-flexible sender, since it's the one channel that genuinely needs arbitrary
  method/header/URL configurability.**
- **Resolved a second, unrelated false positive (`Program:Win32/Contebrew.A!ml`) that Windows
  Defender's live cloud/SmartScreen reputation classifier flagged on a real download of the
  self-contained installer, despite VirusTotal - including a same-day re-scan with Microsoft's own
  engine - showing it completely clean. That classifier weighs signals VirusTotal's static engine
  never sees: publisher trust (this project's cert is self-signed, so it starts with none) and how
  new/large/rarely-downloaded a file is. Rather than chase a live reputation heuristic, Compressarr
  now ships a single, much smaller framework-dependent installer instead of the self-contained
  build - removing the exposure rather than working around it. See Installation below for the
  runtime it now requires.**
- **Upgrading from a self-contained install (v2.1.0, or the briefly-shipped self-contained 2.1.1
  build) left `coreclr.dll`/`hostfxr.dll`/`hostpolicy.dll` behind in the install folder, since an
  in-place upgrade only overwrites files the new package ships - it never removes files that
  belonged only to the old one. .NET's host then treated the install folder itself as a
  self-contained runtime root and failed to find the real machine-wide runtime, showing "You must
  install or update .NET" even on a machine with the correct runtime properly installed. The
  installer now runs the previous version's own uninstaller before installing, guaranteeing a
  clean upgrade every time.**

### Security
- **Donation addresses on the Donate page are no longer stored as single literal strings in the
  compiled binary - a reasonable hardening measure.**

## [2.1.0] - 2026-09-04

### Added
- Per-queue-item controls on the Monitor page: drag a file to reorder it within its lane, skip it
  (stays visible, dimmed, excluded from processing until un-skipped), remove it from the queue
  entirely, or override its preset for that one file only from a dropdown of installed presets - a
  "Use Lane Preset" option resets an override back to the lane default.
- Error queue entries are now shown on Monitor (red badge) with a Remove action, instead of being
  invisible until the next report.
- Failed file *moves* (encode succeeded, but couldn't be filed into the library - an offline
  network drive, etc.) are now retried automatically on the lane's next pass, without re-encoding.
- Configurable behavior when a destination file already exists - Overwrite (previous behavior,
  still the default), Skip, or Rename - instead of always silently overwriting.
- Automated backups (Settings > Backups): scheduled zip backups of your full setup (settings,
  lanes, resume state, run counter, history) to a local or network folder, plus a "Backup Now"
  button and a list of existing backups you can restore from with one click - including on a
  brand-new install, before you've configured anything else.
- Export/import your full configuration as a single file from Settings, for backing up or moving
  to a new machine.
- Test Connection button next to the Sonarr/Radarr integration settings, so a bad URL or API key
  shows up immediately instead of only at unmonitor-time during a real run.
- A warning before leaving Settings or Lanes with unsaved changes, plus a Clear Changes button to
  discard edits in place.
- Pause/Resume for the file currently being converted.
- KB/MB/GB unit dropdown next to Settings' Minimum size field (previously bytes only).
- Per-file conversion duration on the HTML report, and a running total time on the History page.
- The Monitor page's status now shows which preset the current file is using.
- A completely redesigned web UI: a left sidebar for navigation (in place of the old top tab bar),
  a persistent toolbar showing monitoring status and CPU usage on every page, and a consistent
  card-based layout across Settings, Lanes, History, and About.
- A Donate page with QR codes and one-click copy for several cryptocurrency addresses.
- A small indicator appears in the toolbar when a newer version of Compressarr is available.
- Notification channels (Notifications page): get a message when a run completes via Discord,
  Slack, Telegram, Pushover, ntfy, Gotify, Notifiarr, IFTTT, or a custom webhook (which also
  covers Zapier, Make, n8n, Node-RED, and Home Assistant) - configure as many channels as you
  want, each with its own trigger (always / only on error or warning / never) and a Test button.
  A separate toggle controls the existing Windows toast notification, now off by default. Every
  field has a help bubble explaining what it needs and where to find it.
- True cross-lane queue priority: dragging a file in the Monitor page's queue can now move it
  ahead of files in a *different* Lane, not just within its own Lane - the order shown is exactly
  the order files will be processed in, regardless of which Lane each one belongs to.
- A [detailed GitHub Wiki](https://github.com/Compressarr/Compressarr/wiki) with a full walkthrough
  of every page, written for people new to Compressarr.

### Changed
- Start/Stop Monitoring is now a single toggle button instead of two separate ones.
- Stop Monitoring now stops after the file currently converting finishes, rather than continuing
  to process every other file still queued behind it.
- Subtitle and other companion files now move to their destination immediately once their own
  video finishes converting, instead of waiting for every file in a shared folder to finish first.
- The queue's preset picker is a plain dropdown showing the preset actually in effect, instead of
  a custom popover that could show a stale or misleading placeholder.

### Fixed
- A queue edit (reorder, skip, or preset override) made while a file was actively converting could
  be silently discarded once that file finished, and the wrong file could be processed next.
- Removing a file from the queue didn't stick - it could reappear within seconds.
- The queue's preset dropdown or its right-click-style menu could be yanked shut mid-interaction
  by the page's own periodic refresh.
- A queue edit could reorder the whole queue as a side effect, or make untouched files incorrectly
  show as "Resumed" instead of "New."
- The In Queue list could freeze while its own lane's pass was actively running.
- A stale Error entry whose source file was already gone (deleted by hand, or handled elsewhere)
  never cleared itself the way a stale queued entry already did, and could permanently inflate the
  "resuming previous run" count on every single pass.
- Reordering, skipping, removing, or overriding the preset for a file that lives in a subfolder
  under a Lane's Input folder (rather than directly in it - e.g. one folder per movie) silently
  did nothing, with no error shown.

## [2.0.6] - 2026-08-31

### Fixed
- A lane whose only tracked resume jobs pointed at since-deleted source files (e.g. removed by
  hand between runs) would silently process nothing forever - it never fell back to scanning
  Input for genuinely new files, because a non-empty (but entirely dead) pending queue took
  priority over scanning. Dead pending entries are now dropped from the resume file as soon as
  they're detected, so the lane falls back to a fresh scan once nothing resumable is left.
- A file that reappeared in Input after already completing once (e.g. re-added for another test)
  got a second, duplicate resume entry instead of reusing its existing one - resume.json could
  accumulate multiple rows for the same path. Scanning now reuses an existing entry for a path
  it already knows about instead of always adding a new one.

## [2.0.5] - 2026-08-31

### Added
- In Queue section on the Monitor page - lists every file still waiting across enabled lanes, with
  its lane, size, and preset, styled to match the Current status cards.
- Reload button next to Install/Merge Presets on Settings - reloads presets.json without going
  through the merge-prompt flow, with a visible green confirmation message.

### Changed
- Stop Monitoring now reflects the click immediately on both the web page and the tray icon,
  regardless of which surface it was requested from - previously each surface only knew about its
  own click, so stopping from one left the other showing stale state until the in-flight file
  actually finished converting.
- Settings are now re-read after every file's HandBrakeCLI pass finishes, not just once at the
  start of a run or monitoring loop - a change made mid-run now takes effect on the very next
  file instead of requiring a restart.
- Lanes page's TV/Movie preset fields are now real dropdowns instead of a text field with
  autocomplete suggestions - the old control only showed suggestions matching whatever text was
  already typed, so a field already holding a valid preset name would only ever "suggest" itself.

### Fixed
- The preset list included HandBrake's own category headers ("General", "Web", "Devices",
  "Matroska", etc.) as if they were real, selectable presets, because the parser never checked
  HandBrake's own "Folder" flag - on a full HandBrake install this polluted the list with ~15-20
  bogus entries.
- Launching a second Compressarr instance no longer runs two processes against the same lanes -
  it now shows a small "Compressarr is already running" window (with the logo, styled like a
  native Windows dialog) and exits instead.
- A file that encoded successfully but couldn't be moved into the library (e.g. an
  offline/unreachable network drive as the TV or Movie base path) used to still report "OK" -
  now it's flagged as an error, since it isn't actually where it's supposed to be. The file was
  never lost either way - it stays exactly where HandBrake wrote it, in the lane's Output folder.
- Running out of disk space mid-encode was reported as a successful conversion - confirmed live
  against a genuinely full disk that HandBrakeCLI still writes its "Finished work at" completion
  banner even when the encode fails (exit code 4, "No space left on device"), and Compressarr
  wasn't checking the exit code. Left unfixed, this would have moved the truncated/corrupt file
  into place and, depending on Delete-after-convert, deleted or recycled the real source out from
  under it. Success now also requires the process to have exited 0.
- Monitoring now stops itself automatically when a disk-full failure is detected (encode or
  move), instead of retrying the same doomed encode again every poll interval - a clear log
  message explains why.
- The report's Status column now shows the specific reason a file failed ("Output drive full,
  monitoring stopped", "Base folder path unavailable, move skipped", or "No TV/Movie preset
  configured for this lane") instead of a generic "ERROR" for the failure conditions Compressarr
  can actually diagnose - other failures still show "ERROR", rather than guessing. A failed file
  with a detail log also gets a "Full Details" link straight to it.

## [2.0.4] - 2026-08-31

### Changed
- Rebranded to the new "Squeeze" logo/icon mark throughout the app - report and toast logo, both
  favicons, the exe/tray/installer icon, and the web UI's nav-bar and About-page logos.
- The web UI's nav-bar logo is now a clickable link to compressarr.tv (opens in a new tab).
- Refreshed every README screenshot (Settings, Lanes, Monitor, History, sample report) against a
  real run on the current setup - real lane names/paths, the new branding, the Clear title
  metadata toggle, and a genuine completed run (4 files, 27.79GB -> 4.66GB, 83.22% saved).

### Fixed
- The HTML report header's logo and title weren't vertically aligned - Segoe UI's font metrics
  meant `line-height: 1` alone wasn't enough. Dialed in against the real report render.

## [2.0.3] - 2026-08-30

### Fixed
- The installer could hang trying to close a running Compressarr instance before updating it,
  leaving the app running but unresponsive to its own tray Exit command and requiring a manual
  End Task. Caused by Windows Restart Manager's graceful close handshake, which is unreliable
  against a tray-only app that's never had a window shown or interacted with. The installer now
  force-closes any running instance directly before touching files, sidestepping that handshake
  entirely - safe since Compressarr saves settings to disk immediately rather than holding
  anything unsaved in memory.

## [2.0.2] - 2026-08-30

### Fixed
- **Clear title metadata** now actually works. The TagLib-Sharp-based title-stripping feature was
  fully wired up but had no setting driving it, so it silently never ran. It's now a real toggle
  on the Settings page (on by default).

## [2.0.1] - 2026-08-30

### Added
- Enable Monitoring at Startup and Start with Windows settings
- Live countdown to the next pass, Run Now (skips the wait), and Abort (kills the in-flight
  HandBrakeCLI process)
- Live per-file progress (percent, fps, ETA) parsed from HandBrakeCLI's own output
- About page: installed version, credits, GitHub link, and Check for Updates against this repo
  and against HandBrakeCLI's own releases
- Help tooltips on every Settings/Lanes field

### Changed
- HTML report rewritten to match v1.1's layout (light theme, per-lane summaries, rolling history)
- README rewritten to mirror v1.1's structure, with real screenshots and a Custom presets section

### Fixed
- Log panel jumping back to the bottom while scrolled up reading it
- App icon not showing on the desktop shortcut, Start Menu, or the Control Panel uninstall entry
- Lanes page layout bug where Browse buttons could overflow past the card
- "Original v1.1" link now correctly points at the `1.x` branch

## [2.0.0] - 2026-08-30

A complete rewrite of Compressarr as a web-first app. Instead of a PowerShell script, this ships
as a signed Windows installer with a system-tray-only background process - all configuration,
monitoring, and history live in the browser.

### Added
- Web UI for Settings, Lanes, Monitor, History, and About - reachable from any device on the LAN
- Continuous monitoring with a live countdown, Run Now, and Abort
- HandBrakeCLI detection, install, and update checks from inside the app
- Live per-file progress (%, fps, ETA) during conversion
- Self-contained HTML run reports and a rolling history view
- Optional "Start with Windows"

v1.1 (PowerShell) is preserved on the [`1.x`](https://github.com/Compressarr/Compressarr/tree/1.x) branch.

## [1.1.0] - 2026-08-29

### Added
- Per-lane **Enable Lane** checkbox (HD/SD and UHD) to suspend a lane entirely, in both a regular
  run and monitor mode, without clearing its configured paths or presets
- New `contentLanes.<lane>.enabled` config field, defaulting to `true` so existing config files
  keep processing both lanes exactly as before

## [1.0.0] - 2026-08-28

The first stable release. A complete, end-to-end Windows batch video conversion workflow, from
watching a folder for new downloads through to a completion notification - a from-scratch,
modular rewrite of [VidMonHB](https://github.com/mrpaulwasserman/VidMonHB).

### Added
- Two independent content lanes (HD/SD and UHD), each with its own input folder, HandBrake
  presets, and destination paths
- Auto-detection of TV episodes vs. Movies per file from the filename
- Conversion through HandBrakeCLI, followed by filing into an organized `Show Name\Season NN\` or
  `Movie Title\` library structure, moving companion files (subtitles, `.nfo`, artwork) alongside
- Source folder cleanup once empty, guarded against shared folders that still hold other
  unconverted videos
- Optional Sonarr/Radarr integration: unmonitor the matching episode/movie after a successful
  conversion and trigger a library rescan
- Continuous Monitor mode, watching for new files, enabled by default
- Standalone HTML report and a desktop toast notification at the end of each run
