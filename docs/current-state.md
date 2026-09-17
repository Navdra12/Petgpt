# Current state

## Implemented MVP

- .NET 8 WPF application with a transparent, always-on-top static pet
- drag and click-to-toggle interaction
- resizable WebView2 chat bubble loading the native `https://chatgpt.com/` UI
- persistent WebView2 profile at `%LOCALAPPDATA%\PetGPT\WebView2`
- optional cosmetic compact CSS/JS
- Open in browser, Reload, Close, and Escape-to-hide paths
- right-click Open/Hide ChatGPT and Exit PetGPT menu
- basic UI settings at `%LOCALAPPDATA%\PetGPT\settings.json`
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

Native GUI automation was unavailable. Pet visibility, drag, click/open/hide,
ChatGPT interaction and login, Reload, browser button, context-menu Exit, and
resizing all require manual verification for this checkpoint. The verification
process was stopped from the terminal, so that does not count as an Exit test.

## Historical user verification

Before T0, repository documentation recorded successful manual verification of
pet visibility, dragging, bubble opening, ChatGPT loading and login, and the
right-click Exit path. These are retained as historical user-reported results;
they were not repeated or promoted to current-run results during T0.

Detailed evidence and the manual checklist are in
[`docs/validation/windows-smoke.md`](validation/windows-smoke.md). Future web
compatibility questions are tracked in
[`docs/validation/web-compatibility.md`](validation/web-compatibility.md).

## Baseline observations

- The active SDK is a .NET 11 preview because the repository has no
  `global.json`. It emitted `NETSDK1057`, but the .NET 8 project built cleanly.
- A first build inside the restricted Codex sandbox was denied access to a
  Roslyn named pipe. Re-running the same commands outside that restriction
  succeeded; this is an environment limitation, not an application build
  blocker.
- No ChatGPT DOM inspection or web-compatibility probe was performed in T0.
