using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PetGPT.Services;

public sealed class ChatWebViewService
{
    private readonly WebView2 _webView;
    private readonly string _userDataFolder;
    private bool _initialized;
    private bool _compactMode = true;

    public ChatWebViewService(WebView2 webView)
    {
        _webView = webView;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PetGPT",
            "WebView2");
    }

    public async Task InitializeAsync(bool compactMode)
    {
        _compactMode = compactMode;

        if (_initialized)
            return;

        Directory.CreateDirectory(_userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _userDataFolder);

        await _webView.EnsureCoreWebView2Async(environment);

        _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _webView.Source = new Uri("https://chatgpt.com/");
        _initialized = true;
    }

    public void Reload()
    {
        _webView.CoreWebView2?.Reload();
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !_compactMode)
            return;

        try
        {
            await InjectCompactModeAsync();
        }
        catch
        {
            // Compact styling is optional. Never break normal ChatGPT.
        }
    }

    private async Task InjectCompactModeAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        var cssPath = Path.Combine(baseDir, "Web", "compact-chatgpt.css");
        var jsPath = Path.Combine(baseDir, "Web", "compact-chatgpt.js");

        if (File.Exists(cssPath))
        {
            var css = await File.ReadAllTextAsync(cssPath);
            var cssJson = JsonSerializer.Serialize(css);
            var script =
                "(() => {" +
                "const id='petgpt-compact-style';" +
                "let style=document.getElementById(id);" +
                "if(!style){style=document.createElement('style');style.id=id;document.head.appendChild(style);}" +
                "style.textContent=" + cssJson + ";" +
                "})();";

            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        if (File.Exists(jsPath))
        {
            var js = await File.ReadAllTextAsync(jsPath);
            await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }
    }
}
