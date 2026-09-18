# Character pack v1 contract

Character packs are inert local data and image assets. PetGPT never loads pack
assemblies, scripts, XAML, HTML, fonts, CSS, or remote resources, and it never
interprets persona or reaction text as local commands. T5 validates, catalogs,
and installs packs only; it does **not** switch the displayed pet, activate a
persona, execute reactions, apply themes, or change ChatGPT navigation.

## Roots, identity, and compatibility

Bundled packs are read from `<application>/Pets`. User packs are installed at
`%LOCALAPPDATA%\PetGPT\Pets\<id>\<version>`. A catalog lookup always requires an
explicit ID and version; there is no implicit highest/latest version policy.
User packs may not use any bundled ID.

The manifest compatibility version is strict SemVer 2.0.0. Numeric core parts
must not have leading zeroes, prerelease numeric identifiers must not have
leading zeroes, trailing newlines are rejected, and build metadata does not affect ordering. Prerelease identifiers are read-only. PetGPT v2 reports
pack compatibility version `2.0.0`; a pack whose `minAppVersion` is greater is
rejected before it can enter the usable catalog. Settings schema 2 stores
`SelectedPackVersions` as ID-to-SemVer strings under the same parser policy.

Pack IDs and all clip/reaction IDs use `[a-z][a-z0-9_]{0,31}`. The IDs `list`,
`sleep`, and `wake` are reserved. `legacy` is reserved solely for the bundled
compatibility pack.

## Closed manifest

`pet.json` is UTF-8 JSON with maximum depth 32. Property names are
case-sensitive, duplicate names are rejected, and unknown properties are
rejected except for bounded string entries inside `metadata`.

Required/recognized top-level properties are:

- `schemaVersion`: integer `1`.
- `id`: stable pack ID.
- `displayName`: nonempty plain text, at most 80 characters.
- `version` and `minAppVersion`: strict SemVer strings.
- `persona`: `{ "profile": <relative path>, "voice": <relative path> }` for
  normal packs; `null` only for bundled `legacy`.
- `presentation`: `widthDip` and `heightDip` from 64 through 512, plus an
  `anchor` object whose numeric `x` and `y` are from 0 through 1.
- `clips`: 1–64 ID-keyed clip objects. `idle` must be present and usable.
- `systemAnimations`: optional closed mappings for `idle`, `hover`,
  `dragging`, `chatOpen`, `userTyping`, `generating`, and `sleep`; values are
  ordered arrays of 1–8 declared clip IDs.
- `reactions`: 1–64 ID-keyed reaction objects. Only bundled `legacy` may use an
  empty object.
- `theme`: `null`, omitted, or a relative token-file path.
- `trayIcon`: `null`, omitted, or a relative `.ico` path.
- `metadata`: optional string map (maximum 32 entries, 64-character keys and
  2,000-character values). Metadata is informational and is never fetched or
  executed.

### Clips

The only formats are `png` and `pngSheet`. Every clip has a relative `path`.
A `png` clip requires `playback: "hold"`. A `pngSheet` requires positive
integer `frameWidth`, `frameHeight`, and `columns`, `frameCount` from 1 through
256, `fps` from 1 through 24, and `playback` equal to `once` or `loop`. Frames
are row-major from zero. The PNG dimensions must equal the declared column
width and the number of required frame rows, so no frame is partial.

The required `idle` image must exist and decode. A missing or undecodable
non-idle clip remains in the immutable snapshot as unavailable and emits the
bounded warning `clip_unavailable`. References to undeclared clip IDs are an
authoring error. Candidate order is preserved; later playback code may fall
back through that order and finally to `idle`.

### Reactions

Each reaction contains:

- `meaning`: 1–500 characters of inert descriptive text;
- `animationCandidates`: 1–8 ordered declared clip IDs;
- `defaultIntensity`: integer 0–100;
- `visibleMs`: integer 500–6000;
- optional `intensityBands`: strictly ascending, unique integer `min` values
  from 0 through 100, each with 1–8 ordered declared clip IDs.

Reaction IDs and meanings are pack-defined. There is no universal emotion
enumeration and T5 does not execute a reaction.

## Persona and theme data

Normal packs provide `persona/profile.json` and `persona/voice.md` (the paths
are declared by the manifest). The profile is a closed schema with
`schemaVersion: 1`, a `characterId` equal to the pack ID, nonempty strings
`identity`, `worldview`, `relationshipToUser`, and `factualAnswerStyle`, plus
required arrays `values`, `likes`, `dislikes`, `fears`, `taboos`, `humor`, and
`appraisalPrinciples`. Arrays may be empty, contain at most 20 strings, and
each string is at most 1,000 characters. Voice Markdown is bounded inert text.

A supplied theme is a closed JSON object with `schemaVersion: 1`, `colors`,
and `metrics`, plus optional `decoration`. Required colors are `background`,
`surface`, `text`, `mutedText`, `accent`, `border`, `composerBackground`,
`composerText`, and `scrollbarThumb`; values are literal `#RRGGBB` or
`#RRGGBBAA`. Required integer metrics are `surfaceRadiusPx` and
`composerRadiusPx` (0–24), `borderWidthPx` (0–4), and `scrollbarWidthPx`
(6–20). Decoration contains only a relative PNG `path`, `opacityPercent`
(0–30), and `placement` (`background` or `corner`). Packs never supply CSS or
selectors. If the declared theme-token file is missing, the pack remains usable
with native appearance and the bounded warning `theme_unavailable`; an existing
but malformed or unsafe theme remains an authoring error.

## Paths and trust limits

Manifest and archive paths use forward slashes and must be relative to the
canonical pack root. Empty segments, `.`, `..`, absolute/drive-qualified/UNC
paths, URI-looking paths, alternate-data-stream colons, trailing dots/spaces,
canonical-root escapes, case-colliding names, symlinks, and reparse points are
rejected. ZIP names, including every implicit parent directory, are fully
preflighted before any extraction. Case aliases, duplicate entries, and
file/directory conflicts are rejected regardless of entry order. Known
executable/script/XAML/HTML/CSS/SVG/font extensions are rejected.

Application-owned limits (packs cannot override them) are:

| Resource | Limit |
|---|---:|
| `.petpack` archive | 50 MiB |
| expanded candidate | 100 MiB |
| files | 1,000 |
| encoded image | 8 MiB |
| image dimensions | 4096 × 4096 |
| active decoded images | 64 MiB |
| `pet.json` | 64 KiB |
| combined persona profile and voice | 32 KiB |
| theme tokens | 8 KiB |
| decoration PNG | 2 MiB |

PNG headers, dimensions, structure, and decoding are checked without retaining
WPF image objects. Decoded-size arithmetic is checked 64-bit arithmetic before
pixel allocation. Every ICO payload is checked against its directory dimensions;
empty payloads and offsets overlapping the directory are rejected. Embedded PNGs
use the same structure, dimension, budget, and decode checks as standalone PNGs.
DIB support is limited to uncompressed BITMAPINFOHEADER-family headers (40, 52,
56, 108, or 124 bytes) with 1-, 4-, 8-, 16-, 24-, or 32-bit pixels, valid palette
and bitmap/mask lengths, and the ICO doubled-height convention. Compressed DIBs,
V5 linked/embedded color profiles, and unsupported or undecodable payloads are
rejected. Each entry is decoded transiently; the largest entry contributes to
the pack's active decoded-image budget. Validated paths
are canonical absolute paths inside the immutable validated pack snapshot.

## Import and diagnostics

`CharacterCatalog.ImportAsync` accepts a selected folder or `.petpack` ZIP. It
creates a unique staging directory below the local PetGPT pack root, preflights
all paths/counts/sizes, copies or extracts only after path safety succeeds,
validates the complete staged candidate, rejects bundled-ID shadowing and an
already-installed ID/version, then publishes with one same-volume directory
move. Existing versions are never modified or removed. Failed imports clean
their own safe staging directory and leave every installed version untouched;
old abandoned staging directories are removed only when they are safely below
the staging root and contain no reparse points.

Validation/import results expose bounded diagnostic codes and warning codes.
They never include persona text, arbitrary file contents, credentials, or
browser-profile data. Catalog results expose only complete immutable pack
snapshots; unsupported or invalid packs remain rejected diagnostics rather
than half-valid selectable entries.

## Bundled legacy compatibility pack

`Pets/legacy` wraps the original placeholder as a 150-DIP static idle pack.
It has no persona, reactions, theme, or tray icon. The original
`Assets/pet-placeholder.png` remains separately published as the emergency
fallback. T5 does not change which asset the current pet window displays.
