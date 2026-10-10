# Design: the Compressarr Presets catalog

Status: draft for review. Nothing here is built. Target: after 2.2.0 (2.3), or as the last 2.2 feature if wanted.

## 1. Goal

Let users add new encoding profiles without waiting for a Compressarr release, from a separate GitHub repo
(`MrWizardCT/Compressarr-Presets`), while never changing a profile a user already has.

Non-goals:

- Replacing the built-in profiles. They stay embedded in the app and are **not** in the catalog.
- Updating, editing or removing installed profiles remotely. The catalog is **append-only**.
- Automatic installs. Everything is a deliberate user pick.
- Sharing user profiles back to the catalog (a possible later feature).

## 2. Principles

1. **Nothing changes under a user.** A lane picks a profile by name (`TvPreset`/`MoviePreset`, and an ffmpeg profile's
   `FallbackHandBrakePreset`). A name must therefore always mean the same settings.
2. **Published versions are immutable.** Any change, even a typo, is a new version with a new name.
3. **The app works without the catalog.** Offline, rate-limited or the repo gone: installed profiles keep working and the
   embedded built-ins are unaffected.
4. **A profile is configuration that controls what runs**, so it is treated as untrusted input: checksummed, schema-checked
   and run through the same validators as an import or a user-created profile.

## 3. Profile tiers

| Tier | Source | Tag on Profiles page | Edit | Delete |
|---|---|---|---|---|
| Built-in | Embedded in the installer | lock + `built-in` | View only | No |
| Catalog | Downloaded and accepted by the user | lock + `catalog` | View only (Duplicate allowed) | Yes (refused, naming the lane, if a lane uses it) |
| User | Created, imported or duplicated by the user | `user` | Yes | Yes |

"User" is already shipped (beta.6). Catalog profiles behave like built-ins except they can be deleted. A catalog profile
may not use the name of a built-in or an existing user profile; the install step refuses it with the reason shown.

## 4. Naming and versions

- Each catalog entry belongs to a **family** (`Compressarr HEVC AV1 (NVENC)`) and has an integer **version** (1, 2, 3...).
- The installed profile's name is `"{family} v{version}"`, for example `Compressarr UHD HEVC (HDR) v2`. That is what lanes
  and fallbacks reference, so installing v2 next to v1 never affects anything using v1.
- Integers, not dates or semver: an immutable profile has no minor/major distinction, two revisions on one day would
  collide, and a date implies "newer is better". The publish date is metadata, shown as a tooltip.
- A new version is a new entry, never an edit. The repo's checks refuse a changed file under an existing version.
- A version can be marked **deprecated** in the catalog (for example a setting found to damage output) with a reason.
  Installed copies then show a warning. They are never changed or removed.

## 5. The repository

```
Compressarr-Presets/
  catalog.json                      # the index (see below)
  profiles/
    handbrake/
      compressarr-uhd-hevc-hdr-v1.json
    ffmpeg/
      compressarr-hevc-av1-nvenc-v1.json
  CHANGELOG.md                      # human-readable, one line per version
  README.md                         # how to contribute / what the schema is
```

Profile files use the formats the app already imports and stores: a HandBrake preset file (the same JSON HandBrake exports)
and an `ffmpeg-presets.json`-style profile object. No new profile format is invented.

### catalog.json

```json
{
  "schema": 1,
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
      "deprecated": null,
      "requires": ["libx265"]
    }
  ]
}
```

- `schema` is the catalog format version. An app that does not know the schema shows "Update Compressarr to browse the
  catalog" and does nothing else.
- `minAppVersion` is per profile: an app older than that lists the profile greyed out ("needs Compressarr 2.3").
- `requires` lists encoders/capabilities (`libsvtav1`, `av1_nvenc`, an NVENC GPU session...) so the dialog can warn before
  install, as the Profiles page already does for ffmpeg builds.
- `sha256` is of the profile file. The app verifies it on download.

Hosting: the raw files on the default branch (`raw.githubusercontent.com`), fetched over HTTPS with the same
`Compressarr/<version>` user agent the other GitHub calls use. Each published change is a tagged release of the repo so
there is always a known-good snapshot. (Alternative: fetch the repo's latest release assets, as the ffmpeg installer does;
decide in review.)

## 6. In the app

### 6.1 Browse and install

On the Profiles page, next to Import: **Browse catalog...**. It opens the existing import dialog with a different source.

- The app fetches `catalog.json` (one small request). Offline or unreachable: a clear message; nothing else changes.
- Rows are grouped by family and show the newest version, with earlier versions under "show earlier versions". Each row shows
  name and version, engine chip, description, the version's changelog line, and a status chip:
  - **New** (nothing from this family installed)
  - **Installed** (this exact version is installed)
  - **Newer version available** (an older version is installed; information only, never an update)
  - **Deprecated** (with the reason)
  - **Needs Compressarr x.y** (greyed out)
  - plus the existing warnings, for example "needs libsvtav1".
- Nothing is ticked by default. A Select all convenience exists.
- **Add selected** downloads each chosen file and verifies its sha256. A failure skips that profile with the reason; nothing is
  half-installed.
- Verified files go through the same validators as an import (HandBrake preset schema, `FFmpegProfileValidator`), and must
  not collide with an existing profile name. They are then saved into the catalog store (6.2).

### 6.2 Storage

- Catalog profiles live in a separate read-only store under AppData, for example `Profiles\catalog\handbrake\*.json` and
  `Profiles\catalog\ffmpeg\*.json`, beside (never inside) the user's own profile files. User profile files are never touched.
- A small `catalog-installed.json` records for each installed profile: family, version, sha256 at install, source URL, install
  date. On load the app re-checks the stored file against the recorded hash. A mismatch is shown as "damaged - delete and
  add again" and the profile is not offered to lanes.
- `IHandBrakeProfileStore` and `IFFmpegProfileStore` gain a third tier when listing. Resolution by name is unchanged, and
  names are unique across tiers by rule.
- Backups include the catalog store and its record, so a restore on a new machine keeps lanes working without network.

### 6.3 Deleting

- Catalog profiles can be deleted. If an enabled lane (or an ffmpeg profile's fallback) uses it, the delete is refused and
  names the lane, the same rule user profiles follow.
- Deleting does not "re-offer" it: nothing installs automatically, so a deleted profile only reappears in Browse as **New**.

### 6.4 Passive hint (optional)

A once-a-day catalog check may show a quiet "N new presets in the catalog" note on the Profiles page. It never installs,
never nags beyond that note, and can be turned off. Skip it in the first version if in doubt.

## 7. Security

- HTTPS only; the catalog URL is fixed in the app (not user-supplied).
- Checksum on every file; refuse on mismatch.
- Strict parsing: accept only fields the profile model knows; refuse oversize files; refuse anything the validators refuse.
- Profile arguments are passed to HandBrakeCLI and ffmpeg individually (not through a shell), as for every other profile, so a
  profile cannot run commands. It can still change encoding behaviour, which is why installs are explicit and visible.
- A compromised repo can offer a bad profile but can only affect users who then choose to install it. It cannot modify or
  remove anything already installed. The deprecation flag is the remedy for a bad published version.
- Optionally, later: sign the catalog (a detached signature with a key pinned in the app).

## 8. Compatibility

- Catalog `schema` plus per-profile `minAppVersion` make older apps skip what they cannot read.
- 2.1 does not have this feature (and retires after 2.2); the catalog targets 2.2 and later only.
- If a profile's engine capability is missing on the machine (an ffmpeg without libsvtav1, no NVENC session), it can still be
  installed but is flagged exactly as ffmpeg profiles are today.

## 9. Work breakdown

1. Catalog client: fetch and parse `catalog.json`, schema/min-version checks, timeouts, offline handling.
2. Download + verify + validate + install pipeline, reusing the import validators.
3. Catalog store tier for both engines, `catalog-installed.json`, hash re-check on load, backup inclusion.
4. Endpoints: `GET /api/catalog` (list with statuses), `POST /api/catalog/install` (selected family+version pairs),
   `DELETE` reusing the profile delete endpoints with the in-use rule.
5. UI: Browse catalog dialog (reusing the import dialog), `catalog` tag, deprecation and version chips.
6. Repo scaffolding in `Compressarr-Presets`, including a CI check that a published file's hash and version never change.
7. Tests: checksum mismatch, validator rejection, name collisions (built-in, user, catalog), offline, unknown schema,
   `minAppVersion`, deprecated, delete-in-use, restore from backup, damaged file.
8. Wiki page and test-plan items.

Estimate: one to two days of app work, plus the repo and the first profiles.

## 10. First catalog contents (proposal)

HandBrake: Compressarr UHD HEVC (HDR) v1 (x265 10-bit, RF ~22, preset medium, DV and HDR10+ passthrough on), Compressarr HD
Quality v1 (x265 10-bit, RF ~20, medium or slow), Compressarr HD AV1 (fast) v1 (SVT-AV1 preset ~6, CRF ~32), optionally a
Compressarr Compatibility (H.264) v1.
ffmpeg: Compressarr AV1 (NVENC) v1 (`av1_nvenc`, 10-bit, p5, cq ~30, falling back to the UHD HEVC (HDR) HandBrake profile
for Dolby Vision / HDR10+), HEVC NVENC (quality) v1.
All values are starting points to be tested on real DV and SDR samples before publishing. The exact HandBrake settings that
switch Dolby Vision / HDR10+ passthrough on must be confirmed against a real DV file before that profile is published.

## 11. Open questions

1. Fetch raw files from the default branch, or from the repo's tagged releases (atomic snapshots, like ffmpeg)?
2. Show the daily "N new presets" note, or only Browse on demand?
3. Allow a user to install two versions of a family at once (proposed: yes, since names differ)?
4. Sign the catalog from day one, or after the first version ships?
5. Does the existing `MrWizardCT/Handbrake-Custom-Presets` repo get archived, or linked from the new repo's README?
