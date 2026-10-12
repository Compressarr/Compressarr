# Design: announcements

Status: built for 2.2 (2026-10-11), with a port to 2.1.10. Decisions were confirmed on 2026-10-11 (section 11). The repo is
https://github.com/MrWizardCT/Compressarr-Announcements (empty at first).
Target: 2.2 and 2.1.10. It does not touch the encoding engine.

## 1. Goal

Give the project a way to talk to the people running Compressarr: upcoming changes, beta releases, catalog releases, things
worth knowing. Messages are posted to GitHub; the app fetches them and lights an icon when there is a message the user has not
seen from the last 30 days. It can be turned off in Settings and is on by default.

Non-goals:

- Not a replacement for the update check (a new release is already flagged on the About page) or for release notes.
- No tracking and no accounts: the app sends nothing about the user, and the project learns nothing back.
- No pushing of anything that changes the app's behaviour. A message is text and, at most, a link.
- No rich content: no HTML, images, scripts or forms.

## 2. Principles

1. **Informational only.** An announcement can never change settings, install anything or run anything.
2. **Quiet.** One icon with a dot. No pop-ups, toasts or banners, so it can never interrupt an encode or a novice.
3. **Optional and honest.** One Settings switch turns the whole thing off, and when it is off the app makes no request at all.
   The About page and the wiki say what it does and what GitHub can see.
4. **Fail silently.** Offline, rate-limited, the file gone or damaged: the icon simply does not light. Nothing is shown as an error.
5. **Safe against a bad file.** Text is shown as plain text; links are limited to an allowed list of hosts; sizes are capped. A
   mistake or a compromised repo cannot show a phishing link or break a page.
6. **Same posture as the update check.** That check already asks GitHub every 24 hours in the background, so this adds one small
   request to a routine the app already has.

## 3. Where messages live

Three places were considered:

| Option | For | Against |
|---|---|---|
| **A. A small dedicated public repo** (for example `MrWizardCT/Compressarr-Announcements`) holding one `announcements.json` | One file to edit; no code in the repo to confuse it with; any commit publishes; raw files have no API rate limit; history shows what was said when | One more repo |
| B. A file in the main `Compressarr` repo | No new repo | Mixes messages with source; a commit to the code repo is a publish; the file shows up in every clone |
| C. GitHub Discussions or Issues through the API | Rich text, comments | Unauthenticated API limit of 60 requests an hour per IP (a shared network could hit it); the format is not ours; harder to validate |

**Recommendation: A.** Read from the main branch, not a release tag. The catalog needs tags so a profile set never changes mid-read;
announcements are independent notes, and posting one should be a single commit.

Fetched from `https://raw.githubusercontent.com/<owner>/<repo>/main/announcements.json`, with `If-None-Match` so an unchanged file
costs almost nothing.

## 4. The file

```json
{
  "schema": 1,
  "updated": "2026-10-11",
  "announcements": [
    {
      "id": "2026-10-beta6",
      "date": "2026-10-14",
      "kind": "beta",
      "title": "Compressarr 2.2.0-beta.6 is out",
      "text": "Adds the preset catalog, ffmpeg download channel and HDR guards. Beta builds are optional.",
      "link": "https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0-beta.6",
      "important": false,
      "minAppVersion": null,
      "maxAppVersion": "2.2.0-beta.5",
      "expires": null
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | A unique, never reused string. Read state is kept by id |
| `date` | When it was posted (`yyyy-mm-dd`). Drives the 30-day window and the list order |
| `kind` | `release`, `beta`, `catalog`, `notice`. Shown as a small label |
| `title`, `text` | Plain text. Limits below |
| `link` | Optional `https` link, opened in the browser. Host must be on the allowed list |
| `important` | Optional. Sorted first in the list; does not change how the icon behaves |
| `minAppVersion`, `maxAppVersion` | Optional, inclusive. A message is shown only to versions in range, so "beta.6 is out" can be hidden from people already on it, and "2.2 is out" can be aimed at 2.1 |
| `expires` | Optional `yyyy-mm-dd`. Hidden after that date even inside the 30 days |

Limits, enforced by the reader: file 64 KB, 50 entries, `title` 120 characters, `text` 1,000, `id` 80. An entry that breaks a limit or
has no `id`, `date` or `title` is skipped on its own; a bad entry never hides the good ones. An unknown `schema` number means the
whole file is ignored and the app asks (in the list, once) for a newer Compressarr, as the catalog does.

Version bounds use the same version parsing as the update check, with prerelease handling (`2.2.0-beta.5` is below `2.2.0`).

## 5. In the app

### 5.1 The icon

A bell in the top bar on every page, next to the existing Advanced button. A small dot on it means "there is at least one unread
announcement from the last 30 days that applies to this version". No dot: the bell is plain. When announcements are off, or nothing
has ever been fetched, the bell is not shown.

### 5.2 The list

Clicking the bell opens a panel (same style as the existing modals) listing the announcements from the last 30 days that apply,
newest first with `important` ones first. Each shows its kind label, date, title and text, and its link as a button when it has
one. Unread ones are marked. **Opening the panel marks everything in it read** (recommended; see section 11), so the dot clears.
An empty panel says "No announcements in the last 30 days."

### 5.3 Settings

Settings gets one checkbox, **"Show announcements from the Compressarr project"**, on by default, with a short note: "Checks
GitHub once a day for short news items. Nothing about you is sent." It is a normal Settings item, not under Advanced, because it
is a privacy choice and people should find it. Turning it off takes effect immediately: the bell disappears and no request is made.

### 5.4 About page

The About page gets a line under the update status: "Announcements: on / off", with the date of the last successful check. It links
to the Settings checkbox.

## 6. How it works

- **Service.** An `IAnnouncementService` modelled on `UpdateCheckService`: a background loop that checks once at start-up and
  then every 24 hours, using the shared `IHttpClientFactory`. It stores the last good file in a small local cache so the bell state
  survives an offline start. A failed fetch keeps the cached copy and tries again at the next interval.
- **Request.** A plain `GET` with `If-None-Match`. The user agent is `Compressarr-Announcements` and **carries no version number**;
  version filtering happens in the app. No identifiers, cookies or settings are sent.
- **State.** `compressarr.announcements.json` in the app data folder holds the read ids and the baseline date (6.1).
  It is small and is included in backups; losing it only means old messages can show as unread again once.
- **Endpoints.** `GET /api/announcements` returns the applicable messages from the last 30 days with their read flag and whether
  announcements are enabled; `POST /api/announcements/read` marks ids read. The bell polls the first on page load and on a slow timer,
  not every second.
- **Config.** `Announcements.Enabled`, default `true`, in the settings file. Exported and imported with the rest of the settings.
- **Links.** Opened by the browser from the page; the allowed hosts are `github.com` and the project's own site (the exact list is
  one constant). Anything else renders as plain text with no link.

### 6.1 A new install does not start with a pile of news

On the first successful fetch the app records a baseline date. Messages dated **before** that day count as read, so a fresh install
shows no dot for history it never missed. The first message posted after install lights the bell as normal.

## 7. Security and privacy

- **What GitHub sees:** the machine's IP address and the request, exactly as with the update check. It learns that someone runs a
  Compressarr, and no more. The wiki and About page say this in one sentence.
- **Untrusted text.** The file is treated as untrusted: parsed with a strict reader, text inserted with `textContent` (never as
  HTML), lengths capped, links host-checked and `https` only. A message cannot execute anything.
- **No remote control.** There is no field that changes behaviour, a setting, or a version gate on features.
- **Integrity.** The repo is the project's own, over TLS. There is no signing in the first version; the allowed-host rule is what
  limits the damage of a compromised repo (text only, no off-site links). Signing can be added later without changing the file.

## 8. Publishing workflow

1. Add an entry to `announcements.json` in the announcements repo and commit to `main`. That is the publish.
2. A small checked-in script (`node validate.js`) validates the file against the same limits before the commit; a GitHub Action
   running it on every push is optional.
3. A correction is a new entry or an edit before it has been widely seen; a retraction is an `expires` date in the past. Ids are
   never reused, so a corrected message can be marked unread again by giving it a new id.

Nothing in this workflow needs a Compressarr release.

## 9. Compatibility

- **2.2:** the feature as designed.
- **2.1:** has no announcements. If the goal includes telling 2.1 users that 2.2 is out, the feature has to ship in a 2.1.x release
  (the pending 2.1.10 is the natural carrier), with `minAppVersion`/`maxAppVersion` aimed at 2.1. That puts a small feature in a
  hotfix line; it is a choice, not a requirement (section 11).
- **Settings roll-back:** a 2.1 or older build ignores the unknown `Announcements` settings block, as it does other new settings.
- **Offline machines:** unchanged behaviour (the known limitation M4): no bell, no error.

## 10. Testing

Unit tests for the file reader (good file, bad entry skipped, over-size, unknown schema, wrong types), the 30-day window and
`expires`, version-range filtering including prerelease versions, the baseline rule, and read-state persistence. Service tests with
a fake HTTP handler: success, 304, 404, timeout, invalid JSON (cache kept), disabled (no request made). Endpoint tests for the two
endpoints and the disabled state. A UI check of the bell, the panel, the dot clearing and the Settings checkbox. Manual plan
items: with the repo reachable, after a post, with the setting off (confirm no request in the network log), offline start, and a
fresh install with old messages in the file.

## 11. Decisions (confirmed 2026-10-11)

1. **Where the file lives:** a dedicated repo, Compressarr-Announcements, read from `main`.
2. **Marking read:** opening the panel marks everything read.
3. **Timing:** both lines - the 2.2 betas and 2.1.10.
4. **Reaching 2.1 users:** yes, it ships in 2.1.10 so they can be told about 2.2.
5. **Allowed link hosts:** the Compressarr GitHub page (`github.com/MrWizardCT/...`) and `compressarr.tv` (and its subdomains), https only.
6. **Default:** on.
7. **`important`:** only affects the order in the list.

## 12. Work breakdown

1. Model, strict reader and window/version/baseline rules, with tests.
2. `AnnouncementService` (loop, cache, `If-None-Match`, disabled mode), with fake-HTTP tests.
3. State file, two endpoints, `Announcements.Enabled` setting and its validation, with tests.
4. Top-bar bell, panel and unread dot on every page; Settings checkbox and About line.
5. Announcements repo with the validation script and a first message; README on how to post.
6. Wiki page and privacy sentence; test-plan items; CHANGELOG.

Rough size: one focused session, with the repo and docs a little extra. The pieces are independent, so it can be built and
released in two steps (service and endpoints first, UI second) if it needs to be split.
