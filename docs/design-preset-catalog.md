# Design: the Compressarr Presets catalog

Status: draft v2 for review (decisions from the first review are folded in; see section 12). Nothing here is built.
Target: after 2.2.0 (2.3), or as the last 2.2 feature if wanted.

## 1. Goal

Let users add new encoding profiles without waiting for a Compressarr release, from a separate GitHub repo
(`MrWizardCT/Compressarr-Presets`), while never changing a profile a user already has. Build the import so that the
source it reads from is a visible, replaceable thing, so importing from someone else's repo (and later publishing back)
is an extension, not a rewrite.

Non-goals (for the first version):

- Replacing the built-in profiles. They stay embedded in the app and are **not** in the catalog.
- Updating, editing or removing installed profiles remotely. The catalog is **append-only**.
- Automatic installs or background polling. Everything is a deliberate user action.
- Importing from other people's repos, and uploading profiles to a catalog (section 8 lays the groundwork).

## 2. Principles

1. **Nothing changes under a user.** A lane picks a profile by name (`TvPreset`/`MoviePreset`, and an ffmpeg profile's
   `FallbackHandBrakePreset`). A name must therefore always mean the same settings.
2. **Published versions are immutable.** Any change, even a typo, is a new version with a new name.
3. **The app works without the catalog.** Offline, rate-limited or the repo gone: installed profiles keep working and the
   embedded built-ins are unaffected.
4. **A profile is configuration that controls what runs**, so it is treated as untrusted input: checksummed, schema-checked
   and run through the same validators as an import or a user-created profile.
5. **Always say where it came from.** The dialog states which source it is importing from, and every installed profile
   records its source.

## 3. Profile tiers

| Tier | Source | Tag on Profiles page | Edit | Delete |
|---|---|---|---|---|
| Built-in | Embedded in the installer | lock + `built-in` | View only | No |
| Catalog | The official Compressarr catalog, accepted by the user | lock + `catalog` | View only (Duplicate allowed) | Yes |
| Community (future) | Another user's repo, added by the user | lock + `community` | View only (Duplicate allowed) | Yes |
| User | Created, imported from a file, duplicated, or renamed out of a catalog import | `user` | Yes | Yes |

"User" is already shipped (beta.6). Catalog and community profiles behave like built-ins except they can be deleted. A delete
is refused, naming the lane, if an enabled lane (or an ffmpeg profile's fallback) uses the profile - the rule user profiles
already follow.

**Name collisions.** A catalog profile cannot overwrite a built-in, a user profile or another installed profile of the same
name. Instead of refusing, the dialog lets the user **rename and import it as a user profile**:

- A row whose name is taken shows the chip `name already used` and an inline field, `Import as:`, prefilled with the name plus
  ` (copy)`. The user can edit it; it must be unique and valid.
- Such a profile is saved as a normal **user** profile: editable, deletable, no catalog lock or tag, and no link back to the
  catalog. Its description gets a line recording where it came from ("Copied from the Compressarr catalog: <family> v<N>").
- Leaving the row unticked, or clearing the field, skips it. There is no "replace" option for catalog imports (the file import
  has one; a catalog profile never replaces anything).
- A catalog profile that is already installed shows `Installed`, not a collision.

## 4. Naming and versions

- Each catalog entry belongs to a **family** (`Compressarr HEVC AV1 (NVENC)`) and has an integer **version** (1, 2, 3...).
- The installed profile's name is `"{family} v{version}"`, for example `Compressarr UHD HEVC (HDR) v2`. That is what lanes
  and fallbacks reference, so installing v2 next to v1 never affects anything using v1.
- Integers, not dates or semver: an immutable profile has no minor/major distinction, two revisions on one day would
  collide, and a date implies "newer is better". The publish date is metadata, shown as a tooltip.
- A new version is a new entry, never an edit. The repo's checks refuse a changed file under an existing version.
- A version can be marked **deprecated** in the catalog (for example a setting found to damage output) with a reason.
  Installed copies then show a warning. They are never changed or removed.
- Installing two versions of a family side by side is allowed.

## 5. The repository

```
Compressarr-Presets/
  catalog.json                      # the index (see below); also published as a release asset
  profiles/
    handbrake/
      compressarr-uhd-hevc-hdr-v1.json
    ffmpeg/
      compressarr-hevc-av1-nvenc-v1.json
  CHANGELOG.md                      # human-readable, one line per version
  README.md                         # how to contribute / what the schema is; links the archived predecessor repo
```

Profile files use the formats the app already imports and stores: a HandBrake preset file (the same JSON HandBrake exports)
and an `ffmpeg-presets.json`-style profile object. No new profile format is invented.

### catalog.json

```json
{
  "schema": 1,
  "catalog": {
    "name": "Compressarr Presets",
    "maintainer": "MrWizardCT",
    "url": "https://github.com/MrWizardCT/Compressarr-Presets"
  },
  "updated": "2026-10-12",
  "profiles": [
    {
      "family": "Compressarr UHD HEVC (HDR)",
      "version": 1,
      "engine": "handbrake",
      "file": "profiles/handbrake/compressarr-uhd-hevc-hdr-v1.json",
      "sha256": "<hex>",
      "minAppVersion": "2.2.0",
      "description": "x265 10-bit for UHD HDR, keeping HDR10 metadata; Dolby Vision and HDR10+ passed through.",
      "changelog": "First release.",
      "published": "2026-10-12",
      "author": "MrWizardCT",
      "sourceUrl": null,
      "deprecated": null,
      "requires": ["libx265"]
    }
  ]
}
```

- `schema` is the catalog format version. An app that does not know the schema shows "Update Compressarr to browse this
  catalog" and does nothing else.
- `catalog` describes the repo as a whole (display name, maintainer, home URL), shown in the dialog.
- Per profile, `author` is who made the profile and `sourceUrl` (optional) credits where it originated, for example a forum
  post or another repo the settings were adapted from. Both are shown in the dialog and kept in the installed record. They are
  text for the user: **the app never decides trust from them** (see section 7).
- `minAppVersion` is per profile: an app older than that lists the profile greyed out ("needs Compressarr 2.3").
- `requires` lists encoders/capabilities (`libsvtav1`, `av1_nvenc`, an NVENC GPU session...) so the dialog can warn before
  install, as the Profiles page already does for ffmpeg builds.
- `sha256` is of the profile file. The app verifies it on download.
- Possible later: `license`, `tags`, `previewImage`. Not needed now.

### Hosting and fetching (best practice)

- Each publish is a **tagged release** of the repo (`catalog-2026.10.12` or similar) whose assets are `catalog.json` and the
  profile files. Assets of a release are fixed, GitHub records a digest for each, and the tag gives a permanent snapshot - the
  same pattern the ffmpeg installer already uses. Turn on GitHub's immutable-releases protection for the repo if available.
- The app asks the GitHub API for the repo's latest release, reads `catalog.json` from its assets, and downloads profile
  files from the same release. It records the release tag in the installed record.
- Requests use HTTPS and the `Compressarr/<version>` user agent, send `If-None-Match` so an unchanged catalog costs nothing,
  and have short timeouts. GitHub's unauthenticated API limit (60 requests per hour per address) is ample for manual checks.
- Pre-releases and drafts are ignored.

## 6. In the app

### 6.1 Importing from the catalog

The Profiles page's **Import** dialog already has a source tab per origin. The catalog becomes a third tab, placed in the
middle:

`From installed HandBrake` | **`From Catalog`** | `From a presets file`

- The tab shows **`Importing from: Compressarr Presets - https://github.com/MrWizardCT/Compressarr-Presets`** at the top, built from
  the source's `catalog` block and URL (see section 8). It is always shown, so it is always clear where profiles come from.
- A **Load catalog** step (or automatic on opening the tab) fetches `catalog.json`. Offline or unreachable: a clear message;
  nothing else changes.
- Rows are grouped by family and show the newest version, with earlier versions under "show earlier versions". Each row shows
  name and version, engine chip, description, author, the version's changelog line, and a status chip:
  - `New` (nothing from this family installed)
  - `Installed` (this exact version is installed)
  - `Newer version available` (an older version is installed; information only, never an update)
  - `Deprecated` (with the reason)
  - `Needs Compressarr x.y` (greyed out)
  - `name already used` with the `Import as:` field (section 3)
  - plus the existing warnings, for example "needs libsvtav1".
- Nothing is ticked by default. The existing search, `Select all shown` and `Clear` controls work as in the other tabs.
- **Import** downloads each chosen file, verifies its sha256, runs the validators, and saves it. A failure skips that profile with the
  reason; nothing is half-installed.
- The "keep both / replace / skip" conflict options of the file tab do not apply here (collisions are handled per row).

### 6.2 Storage and backups

- Catalog profiles live in a separate read-only store under AppData, for example `Profiles\catalog\handbrake\*.json` and
  `Profiles\catalog\ffmpeg\*.json`, beside (never inside) the user's own profile files. User profile files are never touched.
- A small `catalog-installed.json` records for each installed profile: family, version, sha256 at install, the source
  (catalog name and URL), release tag, author, install date. On load the app re-checks each stored file against the recorded hash. A
  mismatch is shown as "damaged - delete and add again" and the profile is not offered to lanes.
- `IHandBrakeProfileStore` and `IFFmpegProfileStore` list a third tier. Resolution by name is unchanged, and names are unique
  across tiers by rule.
- **Backups.** The catalog store and `catalog-installed.json` are part of the regular backups, the same as the user profile files:
  the automatic backups, the Backups page download, and Export/Import configuration. On restore or import, catalog profiles
  the machine does not already have are added back with their records (files re-verified against their recorded hashes), so
  lanes that use them keep working on a new machine with no network.

### 6.3 Deleting

- Catalog profiles can be deleted, subject to the in-use rule above.
- Deleting does not "re-offer" it: nothing installs automatically, so a deleted profile only reappears in the dialog as `New`.

### 6.4 Checking on demand, and the About page

No background polling and no automatic downloads. The check happens when the user asks:

- The **About** page gets a **Preset catalog** card, like the HandBrakeCLI and ffmpeg cards: it shows the catalog's name and URL,
  when it was last updated, how many profiles are published, and how many of them are not installed. **Check for Updates**
  fetches `catalog.json` and reports "N profiles you haven't added" (and names the ones published since the last check).
  A link opens the Import dialog on the catalog tab.
- After a check, the Profiles page shows a quiet note, "N new presets in the catalog", with the same link. It disappears once the
  user has seen the list, and it is only ever produced by a check the user ran (or by opening the catalog tab). Nothing runs on
  a timer. A setting is not needed.
- The last result is kept in memory/UI state only, not in the configuration.

## 7. Security

- HTTPS only. The app ships with the official catalog URL fixed; there is no free-text URL field in the first version. The URL
  is displayed (`Importing from:`) so the user always sees it.
- Checksum on every file; refuse on mismatch. The release tag is recorded.
- Strict parsing: accept only fields the profile model knows; refuse oversize files; refuse anything the validators refuse.
- Profile arguments are passed to HandBrakeCLI and ffmpeg individually (not through a shell), as for every other profile, so a
  profile cannot run commands. It can still change encoding behaviour, which is why installs are explicit and visible.
- A compromised repo can offer a bad profile but can only affect users who then choose to install it. It cannot modify or
  remove anything already installed. The deprecation flag is the remedy for a bad published version.
- `author`, `sourceUrl` and `catalog` text come from the repo and are displayed as plain text only (escaped). They carry no
  trust: whether a source is the official catalog is decided by the app comparing its URL with the one it ships, never by a
  field in the file.

## 8. Sources, community imports and publishing (groundwork, not built now)

The first version has exactly one source, but the code treats it as one entry in a list so more can be added without rework:

- **Source model.** A source has an id, a display name, a catalog URL and a kind: `official` or `community`. The first version
  hard-codes one `official` source. The `Importing from:` line, the installed record and the tag all read from it.
- **Tags.** Profiles from the `official` source are tagged `catalog`; profiles from any other source are tagged `community`.
  They behave like catalog profiles (view-only, duplicate allowed, deletable). Because `official` is decided by the shipped URL,
  a community repo cannot present itself as official.
- **Adding a source (later).** An "Add a source" action takes a repo URL, shows a clear warning ("Presets from other people
  are not reviewed by Compressarr"), shows the author and URL, and requires an explicit confirmation. HTTPS only, GitHub
  hosts only to begin with, the same schema/checksum/validator pipeline as the official catalog. A source can be removed;
  profiles already installed from it stay installed (and deletable).
- **Name collisions across sources.** Two sources may publish the same family and version with different content. They are
  just a name collision and take the rename-as-user path (section 3). If this proves clumsy, names could carry a short source
  prefix for community profiles; not needed yet.
- **Publishing back (later).** Exporting a user profile in the catalog's profile format, with `author` filled in, so it can be
  submitted to a repo by pull request. This needs no catalog support in the app beyond the export. Uploading directly from the
  app is deliberately not planned (it would need credentials).
- The `catalog`, `author` and `sourceUrl` fields in `catalog.json` exist now for this reason.

## 9. Compatibility

- Catalog `schema` plus per-profile `minAppVersion` make older apps skip what they cannot read.
- 2.1 does not have this feature (and retires after 2.2); the catalog targets 2.2 and later only.
- If a profile's engine capability is missing on the machine (an ffmpeg without libsvtav1, no NVENC session), it can still be
  installed but is flagged exactly as ffmpeg profiles are today.

## 10. First catalog contents (settled one profile at a time)

All values are starting points to be checked on real DV and SDR samples (size, quality, time) before publishing. Every profile
keeps the shipped profiles' common choices unless noted: MKV, crop auto, deinterlace only when interlaced, variable frame rate,
English subtitles, chapters, and HDR dynamic metadata  (the same  setting the shipped
HandBrake profiles already use).

### 10.1 Compressarr UHD HEVC (HDR) v1 - HandBrake - SETTLED

The HEVC sibling of the shipped UHD AV1 profile: the two differ by codec.

- Video: x265 10-bit, profile main10, **RF 22**, **preset fast**; source resolution, no scaling.
- HDR: static HDR10 metadata kept; dynamic metadata  (Dolby Vision and HDR10+ where x265 can carry them).
- Audio: **pass through** every English track HandBrake can copy (AAC, AC3, E-AC3, DTS, DTS-HD, TrueHD, FLAC, MP3), E-AC3 640 kbps
  for anything else.
- To verify before publishing: that a real Dolby Vision file keeps its DV on the output, including a dual-layer profile 7 rip
  (not sure how HandBrake treats those), and the size and time on a UHD sample.

### 10.2 Compressarr HD Quality v1 - HandBrake - IN DISCUSSION

### 10.3 Compressarr HD AV1 (fast) v1 - HandBrake - NOT YET DISCUSSED

### 10.4 Compressarr Compatibility (H.264) v1 - HandBrake, optional - NOT YET DISCUSSED

### 10.5 Compressarr AV1 (NVENC) v1 - ffmpeg - NOT YET DISCUSSED

, 10-bit; needs an RTX 40-series GPU and the AV1-specific GPU check. Neither engine keeps Dolby Vision / HDR10+
with it, so its fallback should be the UHD HEVC (HDR) HandBrake profile (10.1).

### 10.6 Compressarr HEVC NVENC (quality) v1 - ffmpeg - NOT YET DISCUSSED

## 11. Work breakdown

1. Source model (one official source) and catalog client: latest release lookup, `catalog.json` fetch with `If-None-Match`,
   schema/min-version checks, timeouts, offline handling.
2. Download + verify + validate + install pipeline, reusing the import validators; the rename-as-user path.
3. Catalog store tier for both engines, `catalog-installed.json`, hash re-check on load.
4. Backup integration: automatic backups, backup download, Export/Import configuration, restore.
5. Endpoints: `GET /api/catalog` (list with statuses), `POST /api/catalog/install` (selected family+version, optional
   rename-as-user name per row), delete reusing the profile delete endpoints with the in-use rule.
6. UI: `From Catalog` tab between the other two, `Importing from:` line, status chips, the `Import as:` field, `catalog` tag,
   deprecation and version chips; the About page card and the Profiles-page note (6.4).
7. Repo scaffolding in `Compressarr-Presets` (release workflow, CI check that a published file's hash and version never
   change); archive `Handbrake-Custom-Presets` and link it from the new README.
8. Tests: checksum mismatch, validator rejection, name collisions (built-in, user, catalog) and rename-as-user, offline,
   unknown schema, `minAppVersion`, deprecated, delete-in-use, backup and restore (including a damaged file), source line,
   About-page check.
9. Wiki page and test-plan items.

Estimate: two to three days of app work, plus the repo and the first profiles.

## 12. Decisions and open items

Decided:

1. **Hosting:** tagged releases of the repo with release assets and recorded digests (section 5).
2. **Checking:** never automatic. An About-page card and an on-demand note (6.4).
3. **Multiple versions of a family** may be installed side by side.
4. **Catalog signing:** not in version 1. HTTPS, the release digest and the per-file sha256 already cover tampering in transit
   and corruption, and a signature only helps if a key is pinned in the app, which is the official catalog only. Revisit it when
   community sources arrive, where a signature could identify an author. `catalog.json` can gain an optional `signature`
   field later without a schema break.
5. **The old `Handbrake-Custom-Presets` repo is archived** and linked from the new repo's README.
6. **Name collisions** offer rename and import as a user profile (section 3).
7. **Backups** include catalog choices (6.2).
8. **Source line:** `Importing from:` is shown, built on a source model (sections 6.1 and 8).
9. **Tab order:** `From installed HandBrake` | `From Catalog` | `From a presets file`.

Please confirm or correct:

- I read "not automatic" together with "6.4 is fine to include" as: the passive "N new presets" note is included, but only
  after the user has run a check (About page or opening the catalog tab) and never from a timer. If you meant something else,
  say so.
- Community profiles behave like catalog ones (lock, `community` tag, deletable). Should they be visually distinct in other
  ways, for example a different chip colour?
- The default `Import as:` name is `<name> (copy)`. Preferred wording?
