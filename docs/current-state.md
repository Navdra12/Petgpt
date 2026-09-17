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
