# Settings v2 contract

PetGPT stores non-sensitive application preferences in
`%LOCALAPPDATA%\PetGPT\settings.v2.json`. The legacy
`%LOCALAPPDATA%\PetGPT\settings.json` is migration input only and is never
modified. The WebView2 profile remains independent at
`%LOCALAPPDATA%\PetGPT\WebView2` and is never read or changed by settings
persistence.

## Load contract

`SettingsService.Load()` returns a `SettingsLoadResult` containing the validated
snapshot, its source/version, a read-only recovery flag, and bounded diagnostic
codes. Precedence is:

1. valid `settings.v2.json`;
2. valid `settings.v2.last-good.json`;
3. valid legacy `settings.json` migrated in memory;
4. safe defaults.

A future schema is left byte-for-byte untouched and puts persistence into
read-only/default recovery mode. Invalid current-schema bytes are moved to a
bounded `settings.v2.corrupt.<UTC timestamp>.<suffix>.json` name before fallback;
at most three such recovery files are retained. Diagnostics contain short codes
only, never settings content, URLs, prompts, conversations, or browser-profile
data.

## Schema and defaults

Schema version `2` contains:

- `SelectedPetId`: `legacy`;
- `SelectedPackVersions`: empty ID-to-strict-SemVer string object;
- `ChatHomeUrl`: `null`;
- `PetPlacement`: nullable monitor ID and nullable work-area DIP coordinates;
- `ChatWindow`: `FollowPet`, width `500` DIP, height `650` DIP, and nullable
  monitor/coordinates;
- `CompactMode`: `true`;
- `ThemesEnabled`: `true`;
- `Roleplay`: disabled with `ReviewThenSend` activation;
- `ReactionsEnabled`: `false`;
- `ShowControlMarkers`: `false`;
- `PetOptions`: empty object; documented per-pet fields are scale `0.5`–`2.0`
  and reduced motion;
- `SuspendHiddenBrowser`: `false`.

Legacy `PetLeft`, `PetTop`, `BubbleWidth`, `BubbleHeight`, and `CompactMode` are
migrated exactly where valid. T3 converts a restored window into monitor ID plus
work-area-relative DIP offsets. Historical global coordinates without a monitor
ID are a best-effort input because mixed-DPI intent cannot be reconstructed;
nearest/primary fallback and work-area clamping keep the windows usable.

## Validation and writes

Inputs over 256 KiB, malformed UTF-8/JSON, duplicate or unknown properties,
unsupported schema versions, nonfinite/implausible geometry, overlong values,
invalid selected pack version strings, and invalid per-pet options are rejected.
Selected pack versions use the same strict SemVer 2.0.0 parser as character
pack manifests; the schema stores strings such as `"1.2.3"`, not integer
version counters. A future schema is not treated as corrupt and is never
overwritten automatically.

`RequestSave(AppSettings)` copies and validates the supplied snapshot, then
debounces ordinary writes for 500 ms. `FlushAsync(CancellationToken)` cancels the
delay and serializes the latest pending write. Writes use a complete temporary
file in the settings directory, flush it to disk, and replace/move it atomically;
one last-good backup is kept. A write or cleanup failure is reduced to a bounded
diagnostic and does not terminate the UI. Pet drag completion and normal Exit
flush pending changes; location and chat-size events only queue snapshots.
