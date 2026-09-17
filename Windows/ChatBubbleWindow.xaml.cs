using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PetGPT.Models;
using PetGPT.Services;

namespace PetGPT.Windows;

public partial class ChatBubbleWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly Window _pet;
    private readonly ChatWebViewService _chatService;
    private readonly Action _exitRequested;
    private readonly double _normalMinWidthDip;
    private readonly double _normalMinHeightDip;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _geometryReady;
    private bool _applyingGeometry;
    private bool _allowClose;
    private bool _appShuttingDown;

    public ChatBubbleWindow(
        AppSettings settings,
        SettingsService settingsService,
        Window pet,
        Action exitRequested)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _pet = pet;
        _exitRequested = exitRequested;
        _normalMinWidthDip = MinWidth;
        _normalMinHeightDip = MinHeight;

        Width = settings.ChatWindow.WidthDip;
        Height = settings.ChatWindow.HeightDip;

        _chatService = new ChatWebViewService(ChatWebView);

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        LocationChanged += OnLocationChanged;
        DpiChanged += OnDpiChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitors = WindowPositionService.GetMonitors();
        ApplyConfiguredPlacement();
        _geometryReady = true;
        if (_settings.ChatWindow.PlacementMode != "FollowPet")
            PersistFreePlacement();

        _ = InitializeBrowserWithErrorHandlingAsync();
    }

    private async Task InitializeBrowserWithErrorHandlingAsync()
    {
        try
        {
            await _chatService.InitializeAsync(_settings.CompactMode);
        }
        catch (OperationCanceledException) when (_appShuttingDown)
        {
        }
        catch
        {
            if (!_appShuttingDown)
                ShowBrowserInitializationError();
        }
    }

    private void ShowBrowserInitializationError()
    {
        ChatWebView.Visibility = Visibility.Collapsed;
        BrowserStatusText.Text =
            "ChatGPT could not start in PetGPT. Hide this window or exit PetGPT, then restart to try again.";
        BrowserStatusPanel.Visibility = Visibility.Visible;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_geometryReady || _applyingGeometry)
            return;

        _settings.ChatWindow.WidthDip = e.NewSize.Width;
        _settings.ChatWindow.HeightDip = e.NewSize.Height;
        _settingsService.RequestSave(_settings);

        if (_settings.ChatWindow.PlacementMode == "FollowPet")
            ApplyConfiguredPlacement();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_geometryReady || _applyingGeometry || _settings.ChatWindow.PlacementMode == "FollowPet")
            return;

        PersistFreePlacement();
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (!_geometryReady || Dispatcher.HasShutdownStarted)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                RefreshDisplayGeometry(WindowPositionService.GetMonitors());
            }
            catch
            {
                // WPF already applied its DPI suggestion; keep that usable result.
            }
        }));
    }

    public void RefreshDisplayGeometry(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!_geometryReady)
            return;

        _monitors = monitors;
        ApplyConfiguredPlacement();

        if (_settings.ChatWindow.PlacementMode != "FollowPet")
            PersistFreePlacement();
    }

    public void PreparePlacement(IReadOnlyList<MonitorInfo> monitors)
    {
        _monitors = monitors;
        ApplyConfiguredPlacement();
    }

    private void ApplyConfiguredPlacement()
    {
        ApplyGeometry(() =>
            WindowPositionService.PositionBubble(this, _pet, _settings.ChatWindow, _monitors));
    }

    private void ApplyGeometry(Action apply)
    {
        _applyingGeometry = true;
        MinWidth = 0;
        MinHeight = 0;
        try
        {
            apply();
        }
        finally
        {
            // A monitor can be smaller than the normal resize minimum. Keep the
            // entire bubble reachable there without permanently weakening the
            // normal 360x420-DIP user-resize constraint.
            MinWidth = Math.Min(_normalMinWidthDip, Math.Max(0, ActualWidth));
            MinHeight = Math.Min(_normalMinHeightDip, Math.Max(0, ActualHeight));
            _applyingGeometry = false;
        }
    }

    private void PersistFreePlacement()
    {
        var placement = WindowPositionService.CapturePlacement(this, _monitors);
        _settings.ChatWindow.MonitorId = placement.MonitorId;
        _settings.ChatWindow.XWithinWorkAreaDip = placement.XWithinWorkAreaDip;
        _settings.ChatWindow.YWithinWorkAreaDip = placement.YWithinWorkAreaDip;
        _settingsService.RequestSave(_settings);
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_appShuttingDown && e.Key == Key.Escape)
            Hide();
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_appShuttingDown)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/",
            UseShellExecute = true
        });
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (!_appShuttingDown)
            _chatService.Reload();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_appShuttingDown)
            Hide();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _exitRequested();
    }

    public void BeginAppShutdown()
    {
        _appShuttingDown = true;
        _geometryReady = false;
        _chatService.BeginShutdown();
    }

    public Task DisposeBrowserAsync() => _chatService.DisposeAsync();

    public void CloseForAppShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
