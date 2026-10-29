# Before cutting 3.0.0 final

3.0 is feature-frozen as of rc.3 (2026-10-29). Anything new goes to 3.1. The plan: run rc.3 (or a later rc, if a fix is needed) on the production
machine for about a week, then release 3.0.0 to `main` and publish everything at once. This replaces the old 2.2.0 checklist.

## 1. Code and version

1. **Update channel default -> Stable.** In `src/Compressarr.Core/Config/CompressarrConfig.cs` change `UpdateCheckSettings.DefaultChannel` (line ~320)
   from `UpdateChannel.Beta` to `UpdateChannel.Stable`. `UpdateChannelDefaultTests` fails on any version without a pre-release tag until this is done,
   so the version bump cannot ship without it. Update the changelog line ("During the betas the default is...") and the wiki's *Updating Compressarr*
   page to say Stable is the default. (Installs that already saved a choice keep it.) **Also reword the Settings > Updates card text in `src/Compressarr.Web/wwwroot/index.html` (~line 495): it still says
   "a 2.2 beta counts as newer than 2.1.x. While 2.2 is in beta, new installs start on Stable and beta"**; a few User-Agent strings say `Compressarr/2.2` (CatalogClient, FFmpegInstaller) and can say 3.0.
2. **Version 3.0.0:** `Directory.Build.props`, `installer/Compressarr.iss`, the splash comment. Commit as "Version 3.0.0 (not released)".
3. **CHANGELOG `[3.0.0] - <date>`:** one consolidated entry of everything changed or added since **2.1.10** (the last 2.x release), grouped by theme and
   written for a 2.1 user. The pre-release entries (3.0.0-rc.3, 2.2.0-rc.*, 2.2.0-beta.*) stay below it as the history. The GitHub release notes are this section.
4. Full test run (Core, Web), then a clean, signed build of both installers from the release commit (`build-installers.ps1`, no `-AllowDirty`).

## 2. Documents (prepare locally before the release; publish together)

- **README.md (DONE locally, uncommitted):** rewritten as a landing page (what is new in 3.0, how it works, features with screenshots, install, quick start, docs table, upgrading). The
  reference sections moved to new wiki pages (The-Scheduler-Page, Encoders-and-Profiles, Configuration-File-Reference; "Landing a file in a different lane" is in Understanding-Lanes). It refers to
  screenshots in Assets/Screenshots: monitor-page, lanes-page, **profiles-page (new)**, history-page, sample-report, notifications-page - all to be recaptured from production with no real file names.
- **Wiki** (local, uncommitted; ~20 commits already waiting): the 11 pages that still say "2.2"; the stale HandBrake section in *Settings Reference*;
  the new *API and Automation*, *Home Assistant*, *Understanding Lanes* (home videos), *Troubleshooting* (Smart App Control, forgotten password) pages;
  screenshots (below). A GitHub wiki goes live the moment it is pushed, so publish it with the release, not before.
- **Website (`website/`, untracked):** still says 2.1.7 and links to `MrWizardCT/Compressarr`; fallback version (`data-version`), copy for 3.0 features, API
  URL in `README-deploy.txt` (repo owner is now `Compressarr`), real statistics, new screenshots. Do not upload `README-deploy.txt`.
- **Screenshots** come from the production machine (http://192.168.10.91:1212): never show a real file name (replace names in the page before the
  capture, or choose pages with none). Pages: Monitor (idle and live encode), History overview, Lanes, Settings, Encoder, Profiles, Notifications, About, a report.

### Done locally (2026-10-29/30, uncommitted) and what is left of it

- Screenshots recaptured: README (Assets/Screenshots), the website (assets/shots) and the wiki (images/) from the production machine, with host names, user name, IP addresses and **API keys** masked
  (the Security card shows the API keys in full, so never photograph it unmasked) and with made-up file names (the Monitor shots use the real 3.0 page against a made-up status feed; the report
  shot has its names replaced). Tools: scratchpad shot.js (headless Edge over the DevTools protocol), san.js, san-report.js, serve-monitor2.js.
- Website refreshed (3.0 copy, "New in 3.0" section, Profiles tab, stats band 1,268 files / 1.5 TB / 312 runs from production, owner links, fallback version 3.0.0). **Refresh the three numbers and the History caption on release day** (README-deploy.txt explains).
- **Recapture `about-page.png` once production runs 3.0.0** (it shows the version), and check the About page's "Modern V2.1" label in the app (it still says 2.1).

## 3. Release steps (only when the user says go)

1. Release commit on `3.0-dev` (dated CHANGELOG, version 3.0.0, channel Stable), clean signed installers built from it, hashes recorded.
2. **`main` cannot fast-forward:** the 2.1.10 commit (`f4fa87b`) is not an ancestor of `3.0-dev` (the lines diverged at `5b97118`). Without rewriting history:
   on `3.0-dev` run `git merge -s ours origin/main` (records 2.1.10 as merged and keeps the 3.0 tree), then fast-forward `main` to `3.0-dev` and push. The
   tag `v2.1.10` and the `2.1-dev` branch keep the 2.x code.
3. Tag `v3.0.0`, GitHub release as **Latest** (not a pre-release) with both installers, notes = the `[3.0.0]` changelog section; the VirusTotal action appends its links.
4. Publish the wiki (push the wiki repository), deploy the website, push the README with `main`.
5. **Announcements:** post "3.0 is out" to `Compressarr-Announcements` (the 2.1 line reads it; `maxAppVersion` aims it at 2.1 installs).
6. **2.1 users** are on the Stable channel, so they are offered 3.0 only now that it is a stable release.

## 4. After the release (each needs the user's go-ahead)

- A text-only "2.x is retired" note on the v2.1.10 GitHub release (points to 3.0; mentions the two known problems that stay in 2.1.10: Purge Logs & Reports
  deletes the history, and the installer signs only the exe and setup).
- Consider archiving the `2.1-dev`, `2.0` and `1.x` branches.
- Start `3.1-dev` for the roadmap items (Videos lane, per-lane processing, Track in History, scrubbing History rows, Share/Save on reports, ...).

## 5. Tests that must be done or checked

- J7: upgrade from 2.1.10 to 3.0 (history CSV imported, settings kept) on a machine that has run 2.1.10.
- K15 / K16: new lane ready to use, novice walkthrough on a clean machine.
- The production-week results (anything found goes into an rc.4, not into new features).
