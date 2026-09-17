# Windows smoke validation

## T0 baseline

Date: 2026-09-17

Starting commit: `554a0f871bd1f868c89bfaf7ebdad88672ad3870`

Branch: `main`

T0 establishes a pre-refactor checkpoint. It does not change application code,
exercise ChatGPT's DOM, or inspect browser profile contents.

## Environment

| Item | Observed value |
|---|---|
| Windows | Windows 11 Pro 64-bit, version `10.0.26200`, build `26200` |
| Active .NET SDK | `11.0.100-preview.4.26230.115` |
| Project target | `net8.0-windows` |
| Installed .NET 8 runtime | `8.0.31` |
| WebView2 NuGet package | `1.0.4191.47` |
| Installed WebView2 Runtime | `153.0.4234.32` |
| System DPI | 96 (100% scale) |
| Per-monitor scale | Not collected; verify manually on each test display |

The active SDK is a preview because the repository has no `global.json`.
`NETSDK1057` was informational; the project built with no warnings or errors.

## Automated checks

| Check | Result | Evidence |
|---|---|---|
| `dotnet restore PetGPT.csproj` | Pass | All projects were up to date for restore. |
| `dotnet build PetGPT.csproj --no-restore` | Pass | Build succeeded; 0 warnings, 0 errors. |
| `dotnet run --project PetGPT.csproj` | Pass, process-level only | `dotnet run` and `PetGPT.exe` remained alive for more than 15 seconds with no exception output or immediate crash. |
| Process termination | Not a GUI result | The verification process was stopped from the terminal. Context-menu Exit was not exercised. |

The first sandboxed build attempt failed because Roslyn could not access a
local named pipe. The exact commands succeeded outside that restriction, so no
repository build blocker was identified.

## Local data boundary

- Settings path: `%LOCALAPPDATA%\PetGPT\settings.json`
- Persistent WebView2 profile: `%LOCALAPPDATA%\PetGPT\WebView2`
- T0 did not read, copy, enumerate, or log settings/profile contents.
- The chat bubble was not opened during the automated launch check, and no T0
  step intentionally initialized or modified the WebView2 profile.
- Never commit credentials, cookies, tokens, browser storage, or profile data.

## Manual GUI checklist

Native GUI automation was unavailable for T0. A user must complete and record
these checks on the intended Windows displays:

- [ ] Static pet is visible and rendered correctly.
- [ ] Drag moves the pet without triggering a click action.
- [ ] Click opens the chat bubble.
- [ ] A second click hides it, and a later click reopens it.
- [ ] ChatGPT loads and existing login/session behavior works normally.
- [ ] Close button hides the bubble without terminating PetGPT.
- [ ] Escape hides the bubble when focus permits it.
- [ ] Reload works without breaking the native ChatGPT UI.
- [ ] Browser button opens `https://chatgpt.com/` in the default browser.
- [ ] Bubble resizing works and the chosen size survives the expected save path.
- [ ] Right-click Open/Hide ChatGPT works.
- [ ] Context-menu Exit closes the bubble and terminates the process.
- [ ] Positioning remains usable at every connected monitor scale.

## Historical evidence

Before this checkpoint, `docs/current-state.md` recorded successful manual user
verification of pet visibility, dragging, bubble opening, ChatGPT loading and
login, and right-click Exit. That evidence is historical and was not rerun in
T0; it must not be presented as current-run automation.
