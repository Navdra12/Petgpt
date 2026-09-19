using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PetGPT.Characters;
using PetGPT.Models;

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

public sealed class ChatSourceChangedEventArgs(Uri? source) : EventArgs
{
    public Uri? Source { get; } = source;
}

public enum WebDocumentTransition
{
    TeardownAndDisable,
    AwaitNavigationCompletion,
    AdvanceRoute,
    AttachSameDocument
}

internal static class WebDocumentTransitionPolicy
{
    public static WebDocumentTransition Decide(
        bool eligible,
        bool isNewDocument,
        bool hasCurrentDocument)
    {
        if (!eligible)
            return WebDocumentTransition.TeardownAndDisable;
        if (isNewDocument)
            return WebDocumentTransition.AwaitNavigationCompletion;
        return hasCurrentDocument
            ? WebDocumentTransition.AdvanceRoute
            : WebDocumentTransition.AttachSameDocument;
    }
}

public sealed class ChatWebViewService
{
    private readonly WebView2 _webView;
    private readonly string _userDataFolder;
    private readonly ChatWebViewLifecycle _lifecycle = new();
    private readonly ChatWebViewNavigationState _navigationState;
    private readonly WebViewBridge _bridge = new();
    private readonly ThemeService _themeService = new();
    private bool _compactMode = true;
    private bool _themesEnabled = true;
    private bool _historyMode;
    private ThemeTokens? _selectedTheme;
    private bool _navigationSubscribed;
    private int _disposeStarted;
    private string? _adapterScript;
    private string? _compactCss;
    private string? _themeBaseCss;
    private string? _injectedDocumentSession;

    public ChatWebViewService(WebView2 webView, string? configuredHome)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _navigationState = new ChatWebViewNavigationState(configuredHome);
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PetGPT",
            "WebView2");
        _bridge.StatusChanged += OnBridgeStatusChanged;
    }

    public ChatWebViewLifecycleState State => _lifecycle.State;
    public Uri InitialNavigationUri => _navigationState.InitialNavigationUri;
    public Uri? CurrentUri => _navigationState.CurrentUri;
    public GenerationCapabilityState GenerationState => _bridge.GenerationState;
    public ComposerDraftState ComposerDraftState => _bridge.ComposerDraftState;

    public event EventHandler<ChatSourceChangedEventArgs>? SourceChanged;
    public event EventHandler? BridgeStatusChanged;

    public Task InitializeAsync(bool compactMode, bool themesEnabled)
    {
        _compactMode = compactMode;
        _themesEnabled = themesEnabled;
        return _lifecycle.InitializeAsync(InitializeCoreAsync);
    }

    public void SetSelectedTheme(ThemeTokens? theme)
    {
        _selectedTheme = theme;
        _ = ApplyAppearanceSafelyAsync();
    }

    public void SetHistoryMode(bool historyMode)
    {
        if (_historyMode == historyMode)
            return;
        _historyMode = historyMode;
        _ = ApplyAppearanceSafelyAsync();
    }

    public bool Navigate(Uri target)
    {
        if (!_lifecycle.CanUseBrowser ||
            !ChatNavigationUrlPolicy.TryValidateSafeUrl(target.OriginalString, out var safeTarget))
        {
            return false;
        }

        _webView.CoreWebView2.Navigate(safeTarget.AbsoluteUri);
        return true;
    }

    public void Reload()
    {
        if (!_lifecycle.CanUseBrowser)
            return;

        _webView.CoreWebView2?.Reload();
    }

    public void BeginShutdown()
    {
        DisableEnhancements();
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
            DisableEnhancements();
            UnsubscribeNavigation();
            _bridge.StatusChanged -= OnBridgeStatusChanged;
            _bridge.Dispose();
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
            await LoadBundledAssetsAsync(cancellationToken);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder);
            cancellationToken.ThrowIfCancellationRequested();

            await _webView.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();

            _webView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.SourceChanged += OnSourceChanged;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _navigationSubscribed = true;
            cancellationToken.ThrowIfCancellationRequested();

            // The lazy browser's first navigation is the validated project
            // home or root fallback. There is no root-first redirect.
            _navigationState.ObserveSource(_navigationState.InitialNavigationUri);
            SourceChanged?.Invoke(
                this,
                new ChatSourceChangedEventArgs(_navigationState.InitialNavigationUri));
            _webView.Source = _navigationState.InitialNavigationUri;
        }
        catch
        {
            UnsubscribeNavigation();
            throw;
        }
    }

    private async Task LoadBundledAssetsAsync(CancellationToken cancellationToken)
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        _adapterScript = await ReadOptionalAsync(Path.Combine(webRoot, "chatgpt-adapter.js"), cancellationToken);
        _compactCss = await ReadOptionalAsync(Path.Combine(webRoot, "compact-chatgpt.css"), cancellationToken);
        _themeBaseCss = await ReadOptionalAsync(Path.Combine(webRoot, "theme-base.css"), cancellationToken);
    }

    private static async Task<string?> ReadOptionalAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        DisableEnhancements();
    }

    private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var source = GetCurrentSource();
        _navigationState.ObserveSource(source);
        SourceChanged?.Invoke(this, new ChatSourceChangedEventArgs(source));

        var transition = WebDocumentTransitionPolicy.Decide(
            ChatNavigationUrlPolicy.IsEnhancementEligible(source),
            e.IsNewDocument,
            _bridge.CurrentDocument is not null);
        if (transition == WebDocumentTransition.TeardownAndDisable)
        {
            DisableEnhancements();
            return;
        }
        if (transition == WebDocumentTransition.AwaitNavigationCompletion)
            return;

        try
        {
            if (transition == WebDocumentTransition.AdvanceRoute)
            {
                _bridge.AdvanceRoute(source!);
            }
            else
            {
                _bridge.BeginDocument(source!);
                SetWebMessagesEnabled(true);
                await EnsureAdapterInjectedAsync();
            }
            await ApplyAppearanceAsync();
        }
        catch
        {
            DisableEnhancements();
        }
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !_lifecycle.CanUseBrowser)
            return;

        var source = GetCurrentSource();
        _navigationState.ObserveSource(source);
        SourceChanged?.Invoke(this, new ChatSourceChangedEventArgs(source));
        if (!ChatNavigationUrlPolicy.IsEnhancementEligible(source))
        {
            DisableEnhancements();
            return;
        }

        try
        {
            if (_bridge.CurrentDocument is null)
                _bridge.BeginDocument(source!);
            SetWebMessagesEnabled(true);
            await EnsureAdapterInjectedAsync();
            await ApplyAppearanceAsync();
        }
        catch
        {
            // Enhancements are optional. Keep native ChatGPT usable.
            DisableEnhancements();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_lifecycle.CanUseBrowser)
            return;

        try
        {
            _bridge.AcceptMessage(e.Source, GetCurrentSource(), e.WebMessageAsJson);
        }
        catch
        {
            // Page content is untrusted; malformed status is ignored.
        }
    }

    private void OnBridgeStatusChanged(object? sender, EventArgs e)
    {
        BridgeStatusChanged?.Invoke(this, EventArgs.Empty);
        _ = ApplyAppearanceSafelyAsync();
    }

    private async Task ApplyAppearanceSafelyAsync()
    {
        if (!_lifecycle.CanUseBrowser || _bridge.CurrentDocument is null)
            return;

        try
        {
            await ApplyAppearanceAsync();
        }
        catch
        {
            // Styling/capability refresh must never break native ChatGPT.
        }
    }

    private async Task EnsureAdapterInjectedAsync()
    {
        var document = _bridge.CurrentDocument;
        if (document is null ||
            _injectedDocumentSession == document.DocumentSession ||
            string.IsNullOrEmpty(_adapterScript) ||
            !IsCurrentSourceEligible())
        {
            return;
        }

        await _webView.CoreWebView2.ExecuteScriptAsync(_adapterScript);
        if (!IsCurrentSourceEligible() || _bridge.CurrentDocument != document)
            return;
        _injectedDocumentSession = document.DocumentSession;
    }

    private async Task ApplyAppearanceAsync()
    {
        var document = _bridge.CurrentDocument;
        var source = GetCurrentSource();
        if (document is null ||
            source is null ||
            !ChatNavigationUrlPolicy.IsEnhancementEligible(source) ||
            !IsCurrentSourceEligible())
        {
            return;
        }

        await EnsureAdapterInjectedAsync();
        if (_injectedDocumentSession != document.DocumentSession || !IsCurrentSourceEligible())
            return;

        var route = ChatNavigationUrlPolicy.Classify(source);
        var compact = CompactAppearancePolicy.Evaluate(
            _compactMode,
            route,
            _historyMode,
            _bridge.Capabilities.CompactNavigation);
        var themeCapabilities = new ThemeSelectorCapabilities(
            _bridge.Capabilities.PageSurface,
            _bridge.Capabilities.SecondarySurface,
            _bridge.Capabilities.ComposerSurface,
            _bridge.Capabilities.Scrollbar);
        var theme = _themeService.BuildLayer(_selectedTheme, _themesEnabled, themeCapabilities);
        var themeCss = theme is null
            ? null
            : (_themeBaseCss ?? string.Empty) + Environment.NewLine + theme.Css;

        var configuration = JsonSerializer.Serialize(new
        {
            v = 1,
            op = "configure",
            documentSession = document.DocumentSession,
            routeRevision = document.RouteRevision,
            routeKind = route.ToString(),
            historyMode = _historyMode || route == ChatRouteKind.ProjectLanding,
            compact = new
            {
                enabled = compact.Enabled,
                suppressNavigation = compact.SuppressNavigation,
                css = compact.Enabled ? _compactCss : null
            },
            theme = new
            {
                enabled = themeCss is not null,
                css = themeCss
            }
        });

        if (!IsCurrentSourceEligible() || _bridge.CurrentDocument != document)
            return;
        _webView.CoreWebView2.PostWebMessageAsJson(configuration);
    }

    private Uri? GetCurrentSource()
    {
        var value = _webView.CoreWebView2?.Source;
        if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var coreSource))
            return coreSource;
        return _webView.Source;
    }

    private bool IsCurrentSourceEligible() =>
        _lifecycle.CanUseBrowser &&
        ChatNavigationUrlPolicy.IsEnhancementEligible(GetCurrentSource());

    private void SetWebMessagesEnabled(bool enabled)
    {
        if (_webView.CoreWebView2 is not null)
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = enabled;
    }

    private void DisableEnhancements()
    {
        SendAdapterTeardown();
        _bridge.InvalidateDocument();
        _injectedDocumentSession = null;
        SetWebMessagesEnabled(false);
    }

    private void SendAdapterTeardown()
    {
        if (_injectedDocumentSession is null ||
            _webView.CoreWebView2 is null ||
            !ChatNavigationUrlPolicy.IsExactChatGptOrigin(GetCurrentSource()))
        {
            return;
        }

        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson("{\"v\":1,\"op\":\"teardown\"}");
        }
        catch
        {
            // The document may already be unloading. Native ChatGPT remains usable.
        }
    }

    private void UnsubscribeNavigation()
    {
        if (!_navigationSubscribed)
            return;

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            _webView.CoreWebView2.SourceChanged -= OnSourceChanged;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
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
