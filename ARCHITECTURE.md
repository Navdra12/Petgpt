# Architecture

`AppLifetime` is the explicit application owner. It acquires the user-scoped
single-instance guard before creating settings, windows, tray, or browser
resources; owns `SettingsService`, `PetWindow`, `ChatBubbleWindow`, and
`TrayService`; and performs the one ordered Exit path before calling WPF
`Application.Shutdown`. WPF uses `OnExplicitShutdown`.

`PetWindow` is a transparent, always-on-top WPF window. It reports toggle,
geometry, and Exit intents to `AppLifetime` and does not own the bubble or the
application lifetime.

`ChatBubbleWindow` hosts WebView2 and loads `https://chatgpt.com/`.

`ChatWebViewService` creates one persistent WebView2 profile under
`%LOCALAPPDATA%\PetGPT\WebView2` and applies optional cosmetic compact-mode
CSS/JS after navigation. A single shared initialization task moves through
`NotStarted`, `Initializing`, `Ready`, `Failed`, `Disposing`, and `Disposed`;
shutdown prevents late initialization or styling from reviving the browser.

`TrayService` owns one Windows Forms `NotifyIcon`, its menu, and its icon
resources while WPF remains the only application/message loop. T4 exposes only
Show/Hide and Exit; future feature entries are visibly disabled.

`SettingsService` stores validated non-sensitive UI settings under
`%LOCALAPPDATA%\PetGPT\settings.v2.json`, with the original `settings.json`
retained as read-only migration input.

`WindowPositionService` owns Per-Monitor V2 geometry. Native monitor, cursor,
drag, and window rectangles use physical pixels; persisted positions and WPF
sizes use DIPs at an explicit conversion boundary. See the
[window geometry contract](docs/contracts/window-geometry.md).

See the [PetGPT v2 design](docs/superpowers/specs/2026-09-17-petgpt-v2-design.md)
for the proposed full architecture.
