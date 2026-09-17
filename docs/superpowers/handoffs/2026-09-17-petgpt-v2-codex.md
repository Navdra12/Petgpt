# PetGPT v2 — compact Codex handoff

This is an implementation handoff, not authorization to start coding in the architecture-review session.

Repository: [Navdra12/Petgpt](https://github.com/Navdra12/Petgpt). Assessed baseline: `c0c478877257118be849d751e2a8ee60c78e6ba4` on `main`. All 20 text files were read; one PNG was inventoried. No new Windows build, application execution, or authenticated ChatGPT DOM check was performed by the architect.

Read, in order:

1. Current repository `AGENTS.md` and `docs/current-state.md`.
2. [PetGPT v2 design](../specs/2026-09-17-petgpt-v2-design.md), especially §§7–10 and §13.
3. [PetGPT v2 implementation plan](../plans/2026-09-17-petgpt-v2.md): global constraints, shared contracts, then the current task.
4. Only source files relevant to that task. Check for user changes since the assessed commit.

The app is already a small working WPF/.NET 8 MVP: root `PetGPT.csproj`, two windows, three services, static PNG, compact CSS, persistent WebView2. Its WebView2 package is `1.0.4191.47`. Keep the root project and native ChatGPT UI. Preserve `%LOCALAPPDATA%\PetGPT\WebView2` exactly.

Key decisions:

- Incremental migration, one application assembly; no Electron, API client, conversation database, or new LLM engine.
- Data-only character packs; PNG plus uniform sprite sheets first. Wrap the current asset as `legacy` before adding real characters. Each pack supplies its own reaction meanings; no universal emotion conversion.
- Shared PetChats Project Instructions plus a visible per-chat context assembled from local persona files. Selection changes intended persona; native user submission makes the context available remotely. Never overwrite a draft, autosend, or claim role synchronization from a picture change.
- Proposed marker: `[·](https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/65/end)`. Read only reserved href and structural role/turn attributes. No response-text getters or network-body capture. Validate origin, document/route/turn, epoch, pack vocabulary, length, terminal `/end`, and integer intensity.
- Marker rendering/current-turn identity is an early live compatibility gate. If it fails, keep model reactions disabled and deliver independent features. Never substitute a broad scraper.
- Marker authority is bounded animation/status only. No commands, navigation, filesystem access, pack selection, or prompt sends from model output.
- One native command window handles `/pet`, `/theme`, `/history`, `/new`. ChatGPT's composer remains ChatGPT's input. History uses the native project page; no sidebar name discovery or project creation.
- Settings migrate from the five actual legacy fields into `settings.v2.json`; leave old settings untouched, debounce writes, and preserve the browser profile. Fix screen-pixel/WPF-DIP mixing.
- App-owned explicit lifetime and tray arrive together. Keep old Exit behavior. Dispose browser/tray/timers cleanly. No polling or static-idle animation loop.

Sequence: T0 baseline → T1 live compatibility → T2 settings → T3 DPI → T4 lifetime/tray → T5 packs → T6 selection → T7 animation → T8 navigation/themes → T9 persona → T10 reaction bridge → T11 commands/settings → T12 release checks. T1 failure gates T10, not the independent work.

Use the plan's precise test cases and Windows commands. Record tests actually run; distinguish source inspection, synthetic tests, Windows runtime checks, and live ChatGPT compatibility. Update `docs/current-state.md` after material changes. Schedule .NET 10 LTS in a separate compatibility slice before support extends beyond .NET 8's November 2026 end of support.

Do not treat the illustrative Trixie pack definition as existing artwork. Do not broaden the user's narrow output-inspection exception when updating repository instructions. If the implementation needs a different privacy or interaction boundary, surface that concrete design change instead of quietly introducing it.
