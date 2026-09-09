# Architecture

`PetWindow` is a transparent, always-on-top WPF window and owns the lifetime of
`ChatBubbleWindow`.

`ChatBubbleWindow` hosts WebView2 and loads `https://chatgpt.com/`.

`ChatWebViewService` creates a persistent WebView2 profile under
`%LOCALAPPDATA%\PetGPT\WebView2` and applies optional cosmetic compact-mode
CSS/JS after navigation.

`SettingsService` stores non-sensitive UI settings under
`%LOCALAPPDATA%\PetGPT\settings.json`.

The first scaffold uses the primary WPF work area. Per-monitor DPI-aware
positioning is intentionally deferred until the app is running on Windows.
