# Changelog

All notable changes to Compressarr are documented in this file. Pre-release/RC builds leading up
to v1.0.0 are omitted here - see [GitHub Releases](https://github.com/Compressarr/Compressarr/releases)
for that full history.

> [!NOTE]
> Compressarr contains no AI or machine learning at runtime. Movie/TV detection, file matching,
> and renaming all run on plain, inspectable regex pattern matching - the same deterministic
> logic every time, nothing generative involved.

## [Unreleased]

- **Compressarr has a new home on GitHub: github.com/Compressarr/Compressarr** (the organization), with the preset catalog and the announcements next to it. Update checks, announcements and every link in the app use the new address. The old github.com/MrWizardCT/... addresses keep redirecting, so earlier versions keep working.

- **Update channel: Stable, or Stable and beta.** A new Settings > Updates choice sets which releases the update check offers. Stable (the default) is real releases only, as before. Stable and beta offers the highest version of any release, so a 2.2 beta counts as newer than 2.1.x. The check now compares versions including their beta tag, so someone on a beta is told when the final release is out.

- **Announcements.** A bell in the top bar lights up when the Compressarr project has posted news (releases, betas, the 2.2 release) in the last 30 days that you have not seen; click it to read them. The messages come from a public GitHub repo (Compressarr/Compressarr-Announcements), are fetched once at start-up and then about daily, and are plain text with at most a link to the Compressarr GitHub page or compressarr.tv. Nothing about you or your computer is sent. It is on by default and can be turned off in Settings > Announcements, which stops the requests completely.

- **With "Maintain" a converted file is no longer converted again on every pass.** Maintain leaves the source in the Input folder, but the finished file was forgotten at the end of the pass and then found again as new, so the same file was re-encoded over and over. Compressarr now remembers a converted file whose source was kept, by its size and date: it stays out of the queue and is not encoded again while it is unchanged. Put a different file at the same path (or edit it) and it is converted as new; delete the kept source yourself and it is forgotten.

## [2.1.9] - 2026-10-10

> [!TIP]
> **What's new in 2.1.9:** a failed encode now waits in the queue instead of being retried every
> pass, a renamed library folder is no longer recreated (so a second library can't start by
> accident), report "Full Details" links work over http, and History totals are right (the
> Today / Last 7 Days clock and failed encodes). Everything else below carries forward from 2.1.8
> for context - new changes are in **bold**.

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
- **A file whose encode failed is no longer retried on every pass. Before, an encode failure
  (ERROR 101) flipped the file back to pending on the next rescan, so a file that failed the same
  way each time produced a new error report on every poll. It now stays in the queue with its ERROR
  badge, untouched, until you remove the row.**
- **A renamed or missing library base folder (TV Shows or Movies) is no longer recreated. Routing a
  file used to create the missing base path, which quietly started a second library next to the
  renamed one. It is now ERROR 102 (destination unavailable), the file stays put, and it is retried
  on the next pass once the folder is back or the setting is corrected.**
- **The HTML report's "Full Details" links now open when the report is viewed over http (the web
  UI, or from a phone). They used file:// paths, which a browser blocks from a page served over
  http; they now go through the app's own /api/logs/ address (the report itself is unchanged).**
- **History totals and the Today / Last 7 Days rollups were wrong in two ways. The rollup
  calculator read the clock once at startup, so Today and Last 7 Days stayed anchored to the day
  the app started; it now reads the clock on every request. And a failed encode was recorded with
  its original size and no output size, counting as 100% saved and inflating the totals; failed
  encodes are now left out of the totals, and old runs in which every file failed are skipped by
  the rollups and the daily/weekly digest.**
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
