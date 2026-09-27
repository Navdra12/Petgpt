# PetGPT

Windows desktop pet that opens the normal ChatGPT website in a compact WebView2
bubble. No OpenAI API key is used.

## Prerequisites
- Windows 10/11
- .NET 8 SDK
- Microsoft Edge WebView2 Runtime

## Build
```powershell
dotnet restore
dotnet build
dotnet run
```

## Local data
WebView2 session:
`%LOCALAPPDATA%\PetGPT\WebView2`

UI settings:
`%LOCALAPPDATA%\PetGPT\settings.v2.json`

The legacy `%LOCALAPPDATA%\PetGPT\settings.json` remains untouched as migration
input for rollback compatibility.

To clear the embedded ChatGPT session, close PetGPT and delete the WebView2
folder.

## Local PetGPT commands

Use the `/` button in the PetGPT chat title bar, the **PetGPT Commands** tray
entry, or `Ctrl+/` while the native bubble has keyboard focus. This opens a
separate native command window. Commands entered there stay local and are never
sent to ChatGPT.

Supported commands are `/pet`, `/pet list`, `/pet <id>`, `/pet sleep`,
`/pet wake`, `/theme`, `/history`, and `/new`. Shell syntax, chaining, paths,
multiline input, and unknown commands are rejected locally.

Slash-looking text typed in ChatGPT's own composer is an ordinary ChatGPT
message. PetGPT does not intercept, read, or replace the website composer.

## Settings and character packs

Open **Settings** from the tray or the gear button in the chat title bar.
Changes are edited in a detached working copy: **Cancel** or closing the window
does not apply them, while **Apply** validates the complete snapshot, updates
supported runtime behavior, and flushes it to settings v2.

Settings includes the PetChats project landing URL, manual Project Instructions
copy, exact character/version choice, compact/theme preferences, per-character
scale and reduced motion, roleplay/reaction preferences, control-marker
visibility, and FollowPet/Free placement. Hidden-browser suspension is shown as
unavailable because it is not safely supported yet.

Import a data-only character pack from the Character tab by choosing either a
pack folder or a `.petpack` archive. Imports use the same bounded T5 validation
and install below `%LOCALAPPDATA%\PetGPT\Pets\<id>\<version>`. A successful
import refreshes the chooser and tray but is not selected automatically.

## Compact mode

Compact mode can be toggled live from Settings. Its bundled implementation is
in `Web/compact-chatgpt.css` and `Web/compact-chatgpt.js`.

## Known limitations
- The T0 baseline restore, build, and process-launch checks passed on Windows 11;
  interactive GUI behavior still requires manual verification. See
  `docs/validation/windows-smoke.md` for the exact evidence and checklist.
- Per-monitor/DPI positioning has synthetic coverage; real mixed-DPI and
  monitor unplug/replug hardware checks remain outstanding.
- Bubble visuals are intentionally basic.
- ChatGPT frontend changes may require compact-mode updates.
