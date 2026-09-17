using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PetGPT.Services;

public enum ChatWebViewLifecycleState
{
    NotStarted,
    Initializing,
    Ready,
    Failed,
    Disposing,
    Disposed
}

public sealed class ChatWebViewService
{
    private readonly WebView2 _webView;
    private readonly string _userDataFolder;
    private readonly ChatWebViewLifecycle _lifecycle = new();
    private bool _compactMode = true;
    private bool _navigationSubscribed;
    private int _disposeStarted;

    public ChatWebViewService(WebView2 webView)
    {
        _webView = webView;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PetGPT",
            "WebView2");
    }

    public ChatWebViewLifecycleState State => _lifecycle.State;

    public Task InitializeAsync(bool compactMode)
    {
        _compactMode = compactMode;
        return _lifecycle.InitializeAsync(InitializeCoreAsync);
    }

    public void Reload()
    {
        if (!_lifecycle.CanUseBrowser)
            return;

        _webView.CoreWebView2?.Reload();
    }

    public void BeginShutdown()
    {
        _lifecycle.BeginDisposal();
    }

    public Task DisposeAsync()
    {
        _lifecycle.BeginDisposal();
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return Task.CompletedTask;

        _webView.Dispatcher.VerifyAccess();
        try
        {
            UnsubscribeNavigation();
        }
        finally
        {
            try
            {
                _webView.Dispose();
            }
            finally
            {
                _lifecycle.CompleteDisposal();
            }
        }

        return Task.CompletedTask;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder);
            cancellationToken.ThrowIfCancellationRequested();

            await _webView.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();

            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _navigationSubscribed = true;
            cancellationToken.ThrowIfCancellationRequested();

            _webView.Source = new Uri("https://chatgpt.com/");
        }
        catch
        {
            UnsubscribeNavigation();
            throw;
        }
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !_compactMode || !_lifecycle.CanUseBrowser)
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
            if (!_lifecycle.CanUseBrowser)
                return;

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
            if (!_lifecycle.CanUseBrowser)
                return;

            await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }
    }

    private void UnsubscribeNavigation()
    {
        if (!_navigationSubscribed)
            return;

        if (_webView.CoreWebView2 is not null)
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        _navigationSubscribed = false;
    }
}

internal sealed class ChatWebViewLifecycle
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private ChatWebViewLifecycleState _state = ChatWebViewLifecycleState.NotStarted;
    private Task? _initializationTask;

    public ChatWebViewLifecycleState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public bool CanUseBrowser => State == ChatWebViewLifecycleState.Ready;

    public Task InitializeAsync(Func<CancellationToken, Task> initializeAsync)
    {
        ArgumentNullException.ThrowIfNull(initializeAsync);

        lock (_gate)
        {
            if (_state is ChatWebViewLifecycleState.Disposing or ChatWebViewLifecycleState.Disposed)
                return Task.FromException(new ObjectDisposedException(nameof(ChatWebViewService)));

            if (_initializationTask is not null)
                return _initializationTask;

            _state = ChatWebViewLifecycleState.Initializing;
            _initializationTask = RunInitializationAsync(initializeAsync);
            return _initializationTask;
        }
    }

    public bool BeginDisposal()
    {
        var shouldCancel = false;
        lock (_gate)
        {
            if (_state is ChatWebViewLifecycleState.Disposing or ChatWebViewLifecycleState.Disposed)
                return false;

            _state = ChatWebViewLifecycleState.Disposing;
            shouldCancel = true;
        }

        if (shouldCancel)
            _shutdownCancellation.Cancel();
        return true;
    }

    public void CompleteDisposal()
    {
        lock (_gate)
            _state = ChatWebViewLifecycleState.Disposed;
    }

    private async Task RunInitializationAsync(Func<CancellationToken, Task> initializeAsync)
    {
        try
        {
            await initializeAsync(_shutdownCancellation.Token);
            lock (_gate)
            {
                if (_state != ChatWebViewLifecycleState.Initializing)
                    throw new OperationCanceledException(_shutdownCancellation.Token);

                _state = ChatWebViewLifecycleState.Ready;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_state is ChatWebViewLifecycleState.Disposing or ChatWebViewLifecycleState.Disposed)
                {
                    throw new OperationCanceledException(
                        "WebView shutdown began during initialization.",
                        exception,
                        _shutdownCancellation.Token);
                }

                _state = ChatWebViewLifecycleState.Failed;
            }

            throw;
        }
    }
}
