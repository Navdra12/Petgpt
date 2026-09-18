# Current state

## Implemented MVP

- .NET 8 WPF application with a transparent, always-on-top static pet
- drag and click-to-toggle interaction
- resizable WebView2 chat bubble loading the native `https://chatgpt.com/` UI
- persistent WebView2 profile at `%LOCALAPPDATA%\PetGPT\WebView2`
- optional cosmetic compact CSS/JS
- Open in browser, Reload, Close, and Escape-to-hide paths
- right-click Open/Hide ChatGPT and Exit PetGPT menu
- validated settings v2 at `%LOCALAPPDATA%\PetGPT\settings.v2.json`, with the
  original `%LOCALAPPDATA%\PetGPT\settings.json` retained as read-only migration
  input
- clean shutdown path that allows a hidden chat bubble to close

## T0 verification — 2026-09-17

Verified in this run:

- `dotnet restore PetGPT.csproj`: succeeded
- `dotnet build PetGPT.csproj --no-restore`: succeeded with 0 warnings and
  0 errors
- `dotnet run --project PetGPT.csproj`: started `PetGPT.exe`, which remained
  alive for more than 15 seconds with no exception output or immediate crash
- Windows 11 Pro 64-bit, version `10.0.26200`, build `26200`
- active .NET SDK `11.0.100-preview.4.26230.115`; the project still targets
  `net8.0-windows`, with .NET 8 runtime `8.0.31` installed
- WebView2 NuGet package `1.0.4191.47`
- installed WebView2 Runtime `153.0.4234.32`
- system DPI reported as 96 (100% scale); per-monitor scales were not checked

Native GUI automation was unavailable during the automated T0 run. The
verification process was stopped from the terminal, so that run did not count
as an Exit test. The user-operated follow-up below supplies the manual evidence.

## User-reported Windows smoke follow-up

The user subsequently reported all supplied manual checks passing: the pet
appears; dragging has no visible jitter; click opens ChatGPT; second click or
Close hides it; reopening preserves the same WebView session; a message can be
sent and a response received; Reload and Open in browser work; context-menu
Exit fully terminates the process; and resizing does not break the chat window.

This evidence is user-operated. Multi-monitor/DPI behavior was not tested.
Escape-to-hide, right-click Open/Hide, and resized-dimension persistence across
a process restart were not separately reported, so this document makes no new
claim for those extra paths.

Detailed evidence and the manual checklist are in
[`docs/validation/windows-smoke.md`](validation/windows-smoke.md). Live web
compatibility findings are tracked in
[`docs/validation/web-compatibility.md`](validation/web-compatibility.md).

## Baseline observations

- The active SDK is a .NET 11 preview because the repository has no
  `global.json`. It emitted `NETSDK1057`, but the .NET 8 project built cleanly.
- A first build inside the restricted Codex sandbox was denied access to a
  Roslyn named pipe. Re-running the same commands outside that restriction
  succeeded; this is an environment limitation, not an application build
  blocker.
- No ChatGPT DOM inspection or web-compatibility probe was performed in T0.

## T1 live compatibility — 2026-09-17

User-operated validation in the PetGPT WebView2 surface on Edge/WebView2 153
established a **GO** for the narrow reaction-link transport:

- short, long/streaming, regenerated, and second-intensity marker responses
  preserved the exact reserved raw and absolute `href` as anchors;
- marker owners stabilized under
  `data-message-author-role="assistant"` with distinct `data-message-id` values;
- regenerated output received a different assistant message ID;
- existing assistant IDs and marker anchors can be baselined at attachment and
  excluded from live reaction dispatch;
- `stop-button` presence/disappearance supplied a tested generation signal;
- focus, input, Send-button, and Enter events were observed without reading
  prompt or response text.

A marker may appear before it acquires an assistant owner during DOM assembly,
so a production bridge must retain the planned stabilization/revalidation step
before dispatch. Reload destroys the page script context as expected; a future
bridge must create a fresh document session and invalidate the old one.

The verified project route transition is
`https://chatgpt.com/g/:opaque/project` to
`https://chatgpt.com/g/:opaque/c/:opaque` after the first native submission.
The ordinary global New chat control leaves PetChats and goes to
`https://chatgpt.com/`; New PetChat must instead navigate to the configured
project landing URL and wait for native user submission.

Safe structural composer-empty detection remains **UNKNOWN** for the observed
`div#prompt-textarea[role="textbox"]`, so persona staging remains capability
gated and must fall back to Copy context. IME and some Back/Forward edge cases
also remain follow-up items. These limits do not reverse the reaction transport
GO and do not imply that current ChatGPT selectors are supported public APIs.

## T2 settings persistence — 2026-09-17

T2 introduces schema-versioned, validated settings without changing browser
identity or WebView behavior. Loading now uses valid v2, one last-good backup,
legacy v0, then safe defaults. Corrupt v2 bytes receive bounded recovery names;
future schemas remain untouched in read-only/default recovery mode. Ordinary
geometry changes queue copied snapshots with a 500 ms debounce, while drag
completion and normal Exit flush serialized same-directory atomic writes.

The v2 defaults keep the legacy pet selected, compact styling and themes on,
and roleplay/reactions off. The legacy settings file is never written, and the
WebView2 profile path and package version remain unchanged. T3 monitor/DPI
placement, app-owned lifetime/tray work, character packs, roleplay, reactions,
and later v2 subsystems have not started.

Automated T2 evidence: 27 focused xUnit cases cover defaults, legacy migration,
v2/backup precedence, corrupt/future recovery, bounded validation, atomic-write
failure/retry, concurrency/debounce, copied snapshots, explicit flush, and
legacy-file preservation. The project and tests were built in Release during development
because a user-operated Debug PetGPT process was already running; final command
results are recorded with the T2 completion report.

The persistence implementation does not itself prove pet visibility, drag,
click/open/hide, ChatGPT interaction/login, Reload, browser opening,
context-menu Exit, resizing, multi-monitor placement, or DPI behavior. Those
native GUI paths still require manual user verification after this change.

Detailed persistence behavior is in
[`docs/contracts/settings-v2.md`](contracts/settings-v2.md).

## Post-T2 user verification

The user subsequently verified on the current single-monitor system that the
pet and ChatGPT open; pet position and bubble size survive Exit/restart;
immediate Exit after drag preserves position; open/hide/reopen, Reload, and
normal ChatGPT interaction still work; and compact mode remains visually
correct. This is user-operated evidence for the T2 baseline, not T3 or
multi-monitor verification.

## T3 monitor-aware geometry — 2026-09-17

T3 enables Per-Monitor V2 in the embedded application manifest and makes
geometry units explicit. Monitor bounds, work areas, cursor positions, drag
deltas, and native window rectangles are physical pixels. Persisted offsets and
WPF sizes are DIPs, converted once at the `WindowPositionService` boundary.
Dragging now moves the native window from its initial physical rectangle and no
longer adds physical-pixel deltas to WPF `Left`/`Top` values.

Pet and free-chat placement now persist monitor identity plus work-area-relative
DIP offsets. Restore supports negative coordinates, missing-monitor fallback,
taskbars on every edge, oversized-window clamping, and best-effort migration of
legacy/T2 global coordinates. `FollowPet` remains the effective default and
chooses above/below before clamping; `Free` restores its own position but has no
settings UI yet. Display, work-area, and DPI changes use Windows/WPF events with
no polling and do not double-apply WPF's suggested DPI bounds.

Automated evidence: 26 `WindowGeometryTests` cases cover synthetic 100%, 150%,
and 200% DPI monitors, mixed layouts, negative coordinates, all taskbar edges,
monitor removal fallback, clamping, FollowPet/Free behavior, conversion
round-trips, drag deltas, and legacy/T2 recovery. The full suite passes 53 tests.
An existing T2 concurrent-save race exposed by the full suite was fixed by
capturing the debounce cancellation token while holding the settings lock.

The current machine has one 100%-DPI monitor. The T3 process-start smoke found
no immediate crash, but native GUI automation was unavailable. Pet visibility,
drag/click distinction, bubble following, restart restore, resize persistence,
and on-screen clamping require user verification. Real two-monitor behavior,
mixed-DPI transitions, monitor unplug/replug, primary-monitor changes, and
taskbar movement remain explicitly unverified on hardware.

Detailed unit and placement behavior is in
[`docs/contracts/window-geometry.md`](contracts/window-geometry.md).

## T4 application lifetime and tray — 2026-09-17

T4 changes WPF to `OnExplicitShutdown` and makes `AppLifetime` the single owner
of `SettingsService`, the pet and persistent chat windows, `TrayService`, the
single-instance guard, and application Exit. `PetWindow` now emits narrow
toggle, geometry, and Exit intents; it no longer constructs or closes the chat
bubble. Pet click, the pet context menu, chat error UI, and the tray all route
through the same lifetime owner.

The app acquires a stable user-scoped named mutex before creating settings,
windows, tray, or WebView resources. The name uses the global Windows object
namespace plus a SHA-256 digest of the current Windows user SID, so another
session for the same user is excluded without blocking a different user.
Abandoned ownership is accepted normally, and the guard is released during
ordered shutdown. A second launch reports that PetGPT is already running and
does not construct a second application surface or profile/settings writer.

The native tray is one long-lived Windows Forms `NotifyIcon` hosted by the WPF
dispatcher; no Forms message loop was added. Its T4 commands are Show/Hide
ChatGPT and Exit PetGPT. New PetChat, History, Pet, and Settings are explicitly
disabled as unavailable. Show/Hide targets the same persistent
`ChatBubbleWindow` and WebView instance used by pet click. The embedded
multi-size `Assets/petgpt-fallback.ico` is used for the tray and its icon,
menu, and NotifyIcon resources are disposed explicitly.

`ChatWebViewService` now has one shared initialization task and explicit
`NotStarted`, `Initializing`, `Ready`, `Failed`, `Disposing`, and `Disposed`
states. Initialization can succeed at most once; a failure remains truthful and
requires restart rather than a fake retry. Reload is a no-op unless Ready.
Shutdown cancels new initialization/navigation/styling work, removes the
navigation subscription, and disposes WebView2 on the WPF UI thread. Late
initialization completion cannot return the service to Ready. Browser startup
errors are caught outside the WPF `Loaded` event and shown in a small native
surface with Hide and Exit actions.

Explicit Exit is idempotent and ordered: reject further intents; stop browser
and geometry work; queue final pet placement; perform a bounded settings flush;
dispose browser subscriptions/control; close bubble; close pet; dispose the
tray; release the instance guard; and call WPF shutdown once. Bubble-hidden,
bubble-visible, initialization-in-flight, repeated-Exit, and close-callback
paths converge on that sequence. The WebView2 profile remains exactly
`%LOCALAPPDATA%\PetGPT\WebView2`, and hiding never recreates it.
Windows session ending runs the same cleanup with a four-second dispatcher
bound, then leaves the final system-requested WPF shutdown to WPF.

Automated T4 evidence: 10 focused lifecycle tests cover shared initialization,
failure/disposed state rejection, dispose-before-initialize completion,
idempotent disposal/Exit, ordered cleanup after failed or non-cooperative flush,
session-ending cleanup ownership, stable user-scoped mutex naming, exclusive
ownership, and reacquisition. The full suite passes 63 tests. `dotnet build
PetGPT.csproj` succeeds with 0 warnings and 0 errors. A Release publish also
succeeds, contains `PetGPT.exe`, and exposes a readable nine-image embedded
fallback icon. A `dotnet run --project PetGPT.csproj --no-build`
smoke stayed alive and responsive for more than 10 seconds with no console
exception; the fallback icon was also confirmed as the embedded resource
`PetGPT.Assets.petgpt-fallback.ico`. That smoke process was stopped from the
terminal and is not evidence for an application Exit path.

Native Windows app automation was unavailable in this run. User-operated
verification remains required for tray and pet visibility; same-instance
Show/Hide and login preservation; ×/Escape hide; pet-menu and tray Exit;
repeated Exit; second-launch behavior; Exit during WebView initialization;
restart persistence; and Explorer restart/tray recovery. Real multi-monitor and
mixed-DPI hardware validation remains outstanding from T3. T5 and later work
has not started.

## Post-T4 user verification

After a clean Release rebuild, the user verified that the pet and tray icon
appear; pet click and tray Show/Hide control the same persistent bubble;
Close/Escape hide without destroying chat/login; and the disabled New PetChat,
History, Pet, and Settings tray entries are present. Both the pet context-menu
Exit and tray Exit terminate PetGPT and remove the tray icon. A second launch is
rejected without creating a second pet, tray icon, browser-profile writer, or
settings writer, and Exit during WebView initialization does not crash or hang.
Pet position, bubble size, and ChatGPT login survive restart. Reload, Open in
browser, resize, drag, and click-versus-drag behavior also remain working.

An initially observed black WebView came from an older/stale Release output and
did not reproduce after `dotnet clean PetGPT.csproj -c Release` followed by
`dotnet build PetGPT.csproj -c Release`; it is not an unresolved T4 regression.
Explorer restart recovery and real multi-monitor/mixed-DPI hardware checks
remain unverified.

## T5 validated character packs — 2026-09-18

T5 adds a closed, data-only character-pack schema, strict SemVer compatibility
at application version `2.0.0`, immutable validated snapshots, bounded persona
and theme-token parsing, structural plus decode-verified PNG preflight, and
explicit catalog resolution. Paths are canonicalized within the pack root.
Traversal, absolute/drive/UNC/URI/alternate-stream paths, reparse points,
case collisions, duplicate JSON properties, executable content, unsupported
schemas/app versions, and all documented file/image/persona/theme limits are
rejected with bounded diagnostic codes. Missing non-idle clip assets remain
unavailable with a bounded warning; a usable `idle` is mandatory.

`CharacterCatalog` distinguishes application-bundled packs from explicit user
versions under `%LOCALAPPDATA%\PetGPT\Pets`. Folder and `.petpack` imports are
fully path/size preflighted, copied into a local staging directory, validated,
and atomically published as a new `<id>\<version>` directory. A user pack cannot
shadow a bundled ID or replace an installed version. Failed imports remove only
their safe staging candidate and leave installed packs untouched. Settings v2
now stores `SelectedPackVersions` as strict SemVer strings rather than integer
counters.

The existing placeholder is also published as the bundled `legacy` pack with a
150-DIP presentation, one held idle PNG, and no persona or reactions. The
original `Assets/pet-placeholder.png` remains the emergency fallback and the
current pet window still uses its pre-T5 presentation path; runtime selection
belongs to T6.

Automated evidence: the focused `CharacterPackTests` run passes 120 cases,
including two independently named synthetic generated-image packs and the
46 required validation/import/catalog behaviors. Additional regression cases
cover script/executable extensions, metadata control characters, malformed
import paths, missing-theme fallback, and decoder exceptions from structurally
valid but unusable PNGs, ID-level junction escape attempts, terminal-newline
grammar violations, immutable SemVer prerelease identifiers, ZIP implicit-parent
case/type collisions, and ICO payload/header validation (including the bundled
multi-image fallback icon). ICO PNG and supported uncompressed DIB entries are
individually checked and decoded with budgets enforced before pixel allocation.
The full suite passes 189 tests. `dotnet build PetGPT.csproj
-c Release --no-restore` succeeds with 0 warnings and 0 errors. A clean Release
publish contains `PetGPT.exe`, the legacy manifest and idle asset, both Web
compact-mode assets, and the emergency placeholder, with no test
assemblies/fixtures. Published metadata reports file version `2.0.0.0` and
informational/product version `2.0.0+petgpt-v2.<commit provenance>`.

The exact published `PetGPT.exe` remained alive and responsive for more than 10
seconds with no console exception, then was stopped by the test harness. This
is no-immediate-crash evidence only; it does not claim interactive GUI behavior.
No T6 runtime selection, tray character menu, animation engine, persona
activation, reaction execution, theme application, WebView/navigation change,
or command system has been added. The complete data/install contract is in
[`docs/contracts/character-pack-v1.md`](contracts/character-pack-v1.md).
