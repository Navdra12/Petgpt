using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PetGPT.Models;
using PetGPT.Services;

namespace PetGPT.Windows;

public partial class PetWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private ChatBubbleWindow? _bubble;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private ScreenPointPx _dragPointerStartPx;
    private ScreenRectPx _dragWindowStartPx;
    private bool _dragging;
    private bool _geometryReady;
    private bool _isExiting;
    private bool _allowClose;

    public PetWindow(SettingsService settingsService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _settings = _settingsService.Load().Settings;

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Closing += OnPetWindowClosing;
        Closed += OnPetWindowClosed;
        DpiChanged += OnDpiChanged;

        PetImage.MouseLeftButtonDown += OnPetMouseDown;
        PetImage.MouseMove += OnPetMouseMove;
        PetImage.MouseLeftButtonUp += OnPetMouseUp;
        PetImage.LostMouseCapture += OnPetLostMouseCapture;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitors = WindowPositionService.GetMonitors();
        if (_settings.PetPlacement.XWithinWorkAreaDip.HasValue &&
            _settings.PetPlacement.YWithinWorkAreaDip.HasValue)
        {
            WindowPositionService.RestorePet(this, _settings.PetPlacement, _monitors);
        }
        else
        {
            WindowPositionService.PutPetAtDefault(this, _monitors);
        }

        _geometryReady = true;
        PersistPetPlacement();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragPointerStartPx = WindowPositionService.GetCursorPositionPx();
        _dragWindowStartPx = WindowPositionService.GetWindowRectPx(this);
        PetImage.CaptureMouse();
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !PetImage.IsMouseCaptured)
            return;

        var current = WindowPositionService.GetCursorPositionPx();
        var dx = current.X - _dragPointerStartPx.X;
        var dy = current.Y - _dragPointerStartPx.Y;

        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 5)
            return;

        _dragging = true;
        var moved = WindowPositionService.MoveByScreenDelta(
            _dragWindowStartPx,
            _dragPointerStartPx,
            current);
        WindowPositionService.MoveWindowToScreenRect(this, moved);
        RepositionBubble();
    }

    private async void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        var completedDrag = _dragging;
        _dragging = false;
        PetImage.ReleaseMouseCapture();

        if (!completedDrag)
            ToggleBubble();

        if (completedDrag)
        {
            _monitors = WindowPositionService.GetMonitors();
            WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
            PersistPetPlacement();
            await _settingsService.FlushAsync(CancellationToken.None);
        }
    }

    private async void OnPetLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _monitors = WindowPositionService.GetMonitors();
        WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
        PersistPetPlacement();
        await _settingsService.FlushAsync(CancellationToken.None);
    }

    private void OnToggleChatMenuItemClick(object sender, RoutedEventArgs e)
    {
        ToggleBubble();
    }

    private async void OnExitMenuItemClick(object sender, RoutedEventArgs e)
    {
        await ExitApplicationAsync();
    }

    private void ToggleBubble()
    {
        if (_bubble is { IsVisible: true })
        {
            _bubble.Hide();
            return;
        }

        _bubble ??= new ChatBubbleWindow(_settings, _settingsService, this);
        _bubble.PreparePlacement(_monitors);
        _bubble.Show();
        _bubble.Activate();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_geometryReady)
            return;

        PersistPetPlacement();
        RepositionBubble();
    }

    private void RepositionBubble()
    {
        if (_bubble is { IsVisible: true } && _settings.ChatWindow.PlacementMode == "FollowPet")
            _bubble.PreparePlacement(_monitors);
    }

    private void PersistPetPlacement()
    {
        if (!_geometryReady)
            return;

        var placement = WindowPositionService.CapturePlacement(this, _monitors);
        _settings.PetPlacement.MonitorId = placement.MonitorId;
        _settings.PetPlacement.XWithinWorkAreaDip = placement.XWithinWorkAreaDip;
        _settings.PetPlacement.YWithinWorkAreaDip = placement.YWithinWorkAreaDip;
        _settingsService.RequestSave(_settings);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => QueueDisplayGeometryRefresh();

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            QueueDisplayGeometryRefresh();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => QueueDisplayGeometryRefresh();

    private void QueueDisplayGeometryRefresh()
    {
        if (!_geometryReady || Dispatcher.HasShutdownStarted)
            return;

        _ = Dispatcher.BeginInvoke(new Action(RefreshDisplayGeometry));
    }

    private void RefreshDisplayGeometry()
    {
        if (!_geometryReady)
            return;

        try
        {
            _monitors = WindowPositionService.GetMonitors();
            if (_dragging)
                return;

            WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
            PersistPetPlacement();

            if (_bubble is { IsVisible: true })
                _bubble.RefreshDisplayGeometry(_monitors);
        }
        catch
        {
            // Display transitions are best effort; preserve the current usable window.
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
            return;

        _isExiting = true;

        PersistPetPlacement();
        await _settingsService.FlushAsync(CancellationToken.None);

        _allowClose = true;
        _bubble?.CloseForAppShutdown();
        Close();
    }

    private async void OnPetWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (!_isExiting)
            await ExitApplicationAsync();
    }

    private void OnPetWindowClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }
}
