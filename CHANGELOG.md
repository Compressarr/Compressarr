# Changelog

All notable changes to Compressarr are documented in this file. Pre-release/RC builds leading up
to v1.0.0 are omitted here - see [GitHub Releases](https://github.com/MrWizardCT/Compressarr/releases)
for that full history.

> [!NOTE]
> Compressarr contains no AI or machine learning at runtime. Movie/TV detection, file matching,
> and renaming all run on plain, inspectable regex pattern matching - the same deterministic
> logic every time, nothing generative involved.

## [2.2.0-beta.6] - Unreleased

> [!NOTE]
> Changes since [2.2.0-beta.5](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0-beta.5)
> only - everything else is in the entries below.

### Added
- **A preset catalog: more profiles without a new Compressarr release.** Profiles > Import > **From Catalog** (between "From installed HandBrake" and "From a presets file") reads the official catalog, https://github.com/MrWizardCT/Compressarr-Presets, which is shown on screen as "Importing from:". You tick the profiles you want and Import: nothing is added automatically and nothing is checked on a timer (the catalog is read when you open the tab, or press Check for Updates on the new **Preset catalog** card on the About page). Catalog profiles are tagged with a lock and "catalog" and are locked like the built-ins (View and Duplicate), but can be deleted - refused, naming the lane, while a lane uses one. Every download is checked against the SHA-256 the catalog lists and the same validators as any other profile before it is stored, and a stored profile that no longer matches what was installed is not offered to lanes (the Profiles page says so). Published versions never change: a profile is named `<name> vN`, a newer version is added next to the old one (marked "newer version", never an update), and a version can be marked deprecated with a reason. If a catalog name is already taken (a built-in, one of yours, another catalog profile) the row offers **Import as:** a new name (default `<name> (copy)`), and it is saved as one of your own profiles. Built-in profiles are not in the catalog. The profiles you add are included in the automatic backups and in Export/Import configuration, and a restore brings them back with no network. The Profiles page's storage panel has a **Catalog (downloaded)** box. The first catalog has four profiles: **Compressarr UHD HEVC (HDR) v1** (x265 10-bit RF 22, keeps Dolby Vision, HDR10+ and HDR10), **Compressarr HD Quality v1** (x265 10-bit RF 20), **Compressarr Compatibility (H.264) v1** (your original VeryFastDDtoAAC: x264 8-bit, MP4, AAC, 30 fps cap; SDR only) and **Compressarr AV1 (NVENC) v1** (cq 34, needs an RTX 40-series or newer).
- **AV1 on an NVIDIA GPU is checked on its own.** The Encoder page's capability check opened a one-frame GPU session for H.265 only, so an ffmpeg profile using `av1_nvenc` could look fine on a GPU that can't encode AV1 (it needs an RTX 40-series or newer) and then fail mid-run. `av1_nvenc` now has its own test and its own chip on the Encoder page (ticked only when a session really opens), and an AV1 NVENC profile on a GPU without AV1 is flagged on Profiles and Lanes with the real reason.
- **HandBrake lanes no longer re-encode an HDR file with a profile that would lose the HDR.** Before an encode, a lane running a HandBrake profile whose video encoder can't be relied on for HDR reads the file (with ffprobe). An H.264 profile can only produce SDR, so an HDR10, HLG, Dolby Vision or HDR10+ file would come out with washed-out colours; a hardware encoder (NVEnc, QSV, AMF) can't keep Dolby Vision or HDR10+. Such a file is left alone and reported as ERROR 114 (it stays in the queue with its ERROR badge, like other refusals) instead of being re-encoded and replacing the original. HandBrake profiles that keep everything (x265 and SVT-AV1 with Dolby Vision / HDR10+ passthrough on) are never probed, so they pay nothing, and a profile that has switched dynamic-metadata passthrough off has already said it doesn't want it. A lane that really wants it can tick **Allow HDR files to lose their HDR** (under Advanced on the Lanes page); that never covers a Dolby Vision profile 5 file, which would show wrong colours. If ffprobe isn't installed the check is skipped with one note in the log (install ffmpeg on the Encoder page to turn it on). It also applies to a file an ffmpeg lane hands to a HandBrake fallback profile.
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
- **The built-in ffmpeg profiles no longer hand Dolby Vision or HDR10+ files to HandBrake.** Their HandBrake fallback is now None: such a file is left alone and reported as ERROR 112 (and the report's help-bubble says how to fix it); it stays in the queue with its ERROR badge instead of being refused and reported again on every pass, until you change the profile and use Remove on its row instead of being re-encoded with a profile you didn't choose, which would replace the original. To have those files encoded, duplicate the profile and pick a HandBrake fallback on the copy, or use a HandBrake lane (HandBrake keeps Dolby Vision and HDR10+). Duplicate as ffmpeg also starts with the fallback set to None. Your own ffmpeg profiles keep whatever fallback they have.
- **Failure messages in the top bar are red.** Many errors ("Could not delete the profile", "Failed to save...", "Import failed", a failed notification test and so on) showed as plain text; they now use the same red chip as other errors. Progress messages ("Saving...") stay plain and successes stay green.
- **Clearer messages when a file can't be read or is HDR-blocked.** An ffmpeg lane given a damaged file or a non-video file now says the file may be damaged or not a video, instead of asking whether ffprobe is installed (it still asks that when ffprobe really is missing). The ERROR 114 text no longer reads "a HDR file".
- **Cleaning up a deleted lane's old entries is no longer shown as an error.** "Resume entry ... belongs to a lane that no longer exists - removing it" is routine housekeeping, so it is now an ordinary log line instead of a red one.
- **A lane's TV and Movie Destination paths are checked like Input.** On an enabled lane, a destination folder that does not exist (with Move converted files on) turns red with a message, and the red clears within half a second of fixing it, no Save needed. Compressarr still never creates the folder, and a run is not stopped by this.
- **E-AC3 audio is honest about 5.1.** ffmpeg's E-AC3 encoder (and HandBrake's, which is the same encoder) cannot write 7.1 - a 7.1 or 8-channel source always came out as 5.1 while the profiles said "up to 7.1". The ffmpeg built-ins now say 5.1, and an ffmpeg profile whose codec cannot hold the channels (E-AC3 and AC3: 5.1, MP3: stereo) mixes down explicitly and the Preview says why. The HandBrake built-ins are unchanged (HandBrake has always capped them itself, and editing them would make your 2.1 copies look edited). Want 7.1 kept? Use a profile that copies E-AC3/TrueHD/DTS-HD through, or AAC/FLAC/Opus, which carry 7.1.
- **Catalog: one name can be both a HandBrake and an ffmpeg profile.** Installing now says which engine, so a catalog family published for both (like the built-ins) installs each one on its own. The first such family is Compressarr SD-HD Opus 7.1 (SD-HD with Opus audio, which keeps 7.1 surround) once the catalog is updated.
- **HDR10+ files are described as HDR10+.** The report tag for an ffmpeg lane that hands an HDR10+ file to HandBrake now reads "HandBrake (HDR10+ fallback)" instead of "Dolby Vision fallback", and the ffmpeg Preview note says what this profile will actually do with the file (hand it to HandBrake, drop the metadata, or leave it alone as an error) instead of a general rule.
- **Importing or restoring the wrong or a damaged file is refused in plain words.** A truncated export, an empty file, text, or a damaged backup zip now says the file looks damaged or cut short (no more temp-file paths or "Central Directory corrupt"), and nothing is changed. A valid JSON file that has none of Compressarr's settings - an empty `{}`, or another program's file such as HandBrake's presets.json - used to import "successfully" and reset every lane and setting to defaults; it is now refused.
- **Your own profiles are tagged "user" on the Profiles page**, the way the shipped ones are tagged "built-in", so the two are easy to tell apart.
- **The destructive Maintenance buttons on Settings are under Advanced.** Clear Logs, Clear History, Purge Logs & Reports,
  Clear All, Reset Lanes and Clear Configuration are hidden until Advanced is on. Reset Resume File and Clean Up Now stay
  visible. Each of the six keeps its confirmation prompt.

## [2.2.0-beta.5] - 2026-10-10

> [!NOTE]
> Changes since [2.2.0-beta.4](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0-beta.4)
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
> Changes since [2.2.0-beta.3](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0-beta.3)
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
> Changes since [2.2.0-beta.2](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0-beta.2)
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
> **Pre-release (beta).** The stable release is still [2.1.8](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.1.8).
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
> release is still [2.1.8](https://github.com/MrWizardCT/Compressarr/releases/tag/v2.1.8).

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
- A [detailed GitHub Wiki](https://github.com/MrWizardCT/Compressarr/wiki) with a full walkthrough
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
- A [detailed GitHub Wiki](https://github.com/MrWizardCT/Compressarr/wiki) with a full walkthrough
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
- A [detailed GitHub Wiki](https://github.com/MrWizardCT/Compressarr/wiki) with a full walkthrough
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
- A [detailed GitHub Wiki](https://github.com/MrWizardCT/Compressarr/wiki) with a full walkthrough
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

v1.1 (PowerShell) is preserved on the [`1.x`](https://github.com/MrWizardCT/Compressarr/tree/1.x) branch.

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
