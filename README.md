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
`%LOCALAPPDATA%\PetGPT\settings.json`

To clear the embedded ChatGPT session, close PetGPT and delete the WebView2
folder.

## Replace the pet
Replace `Assets/pet-placeholder.png` or change the image source in
`Windows/PetWindow.xaml`.

## Compact mode
Edit:
- `Web/compact-chatgpt.css`
- `Web/compact-chatgpt.js`

## Known limitations
- Scaffold has not yet been built or run on Windows.
- Positioning is not yet per-monitor/DPI aware.
- Bubble visuals are intentionally basic.
- ChatGPT frontend changes may require compact-mode updates.
