# Architecture

`PetWindow` is a transparent, always-on-top WPF window and owns the lifetime of
`ChatBubbleWindow`.

`ChatBubbleWindow` hosts WebView2 and loads `https://chatgpt.com/`.

`ChatWebViewService` creates a persistent WebView2 profile under
`%LOCALAPPDATA%\PetGPT\WebView2` and applies optional cosmetic compact-mode
CSS/JS after navigation.

`SettingsService` stores validated non-sensitive UI settings under
`%LOCALAPPDATA%\PetGPT\settings.v2.json`, with the original `settings.json`
retained as read-only migration input.

`WindowPositionService` owns Per-Monitor V2 geometry. Native monitor, cursor,
drag, and window rectangles use physical pixels; persisted positions and WPF
sizes use DIPs at an explicit conversion boundary. See the
[window geometry contract](docs/contracts/window-geometry.md).

See the [PetGPT v2 design](docs/superpowers/specs/2026-09-17-petgpt-v2-design.md)
for the proposed full architecture.
