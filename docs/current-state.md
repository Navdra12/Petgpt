# Current state

Implemented in the scaffold:
- right-click pet menu with Open/Hide ChatGPT and Exit PetGPT
- clean app shutdown path that allows the hidden chat bubble to close

- .NET 8 WPF project
- transparent always-on-top pet
- placeholder PNG
- drag + click-to-toggle bubble
- WebView2 loading ChatGPT
- persistent WebView2 profile
- optional compact CSS/JS
- Open in browser / Reload / Close
- basic settings persistence
- agent-oriented repo docs

Windows verification:
- dotnet restore: success
- dotnet build: success, 0 warnings, 0 errors
- application launched and remained responsive
- WebView2 Runtime 152.0.4191.66 detected
- manual UI verification completed successfully:
  pet visible, draggable, bubble opens, ChatGPT loads, login works
- right-click menu and Exit PetGPT verified manually