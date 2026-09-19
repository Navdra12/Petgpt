using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PetGPT.Models;
using PetGPT.Services;
using PetGPT.Shell;

namespace PetGPT.Windows;

public partial class PetWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly Action _toggleChatRequested;
    private readonly Action _exitRequested;
    private readonly Action<IReadOnlyList<MonitorInfo>> _bubbleRepositionRequested;
    private readonly Action<PetEvent> _petEventRaised;
    private readonly PetInteractionEventGate _interactionGate = new();
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private ScreenPointPx _dragPointerStartPx;
    private ScreenRectPx _dragWindowStartPx;
    private bool _geometryReady;
    private bool _allowClose;
    private bool _applyingPresentation;
    private double _presentationAnchorX = 0.5;
    private double _presentationAnchorY = 1;

    public PetWindow(
        SettingsService settingsService,
        AppSettings settings,
        Action toggleChatRequested,
        Action exitRequested,
        Action<IReadOnlyList<MonitorInfo>> bubbleRepositionRequested,
        Action<PetEvent> petEventRaised)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _settings = settings;
        _toggleChatRequested = toggleChatRequested;
        _exitRequested = exitRequested;
        _bubbleRepositionRequested = bubbleRepositionRequested;
        _petEventRaised = petEventRaised ?? throw new ArgumentNullException(nameof(petEventRaised));

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Closing += OnPetWindowClosing;
        Closed += OnPetWindowClosed;
        DpiChanged += OnDpiChanged;

        PetImage.MouseLeftButtonDown += OnPetMouseDown;
        PetImage.MouseMove += OnPetMouseMove;
        PetImage.MouseLeftButtonUp += OnPetMouseUp;
        PetImage.LostMouseCapture += OnPetLostMouseCapture;
        PetImage.MouseEnter += OnPetMouseEnter;
        PetImage.MouseLeave += OnPetMouseLeave;
    }

    public IReadOnlyList<MonitorInfo> CurrentMonitors => _monitors;
    internal System.Windows.Controls.Image AnimationSurface => PetImage;
    internal BitmapSource? CurrentFrame => PetImage.Source as BitmapSource;

    internal void ApplySelection(PreparedPetSelection prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var presentation = prepared.Pack.Presentation;
        if (!_geometryReady)
        {
            Width = presentation.WidthDip;
            Height = presentation.HeightDip;
            PetImage.Source = prepared.IdleImage;
            _presentationAnchorX = presentation.AnchorX;
            _presentationAnchorY = presentation.AnchorY;
            return;
        }

        var previousRect = WindowPositionService.GetWindowRectPx(this);
        var previousWidth = Width;
        var previousHeight = Height;
        var previousImage = PetImage.Source;
        var previousAnchorX = _presentationAnchorX;
        var previousAnchorY = _presentationAnchorY;
        var previousPlacement = _settings.PetPlacement.Copy();
        var targetRect = WindowPositionService.ResizeAroundAnchor(
            previousRect,
            previousAnchorX,
            previousAnchorY,
            new SizeDip(presentation.WidthDip, presentation.HeightDip),
            presentation.AnchorX,
            presentation.AnchorY,
            _monitors);

        try
        {
            _applyingPresentation = true;
            WindowPositionService.ResizeWindowToScreenRect(this, targetRect);
            Width = presentation.WidthDip;
            Height = presentation.HeightDip;
            PetImage.Source = prepared.IdleImage;
            _presentationAnchorX = presentation.AnchorX;
            _presentationAnchorY = presentation.AnchorY;
            UpdatePersistedPlacementWithoutSave();
        }
        catch
        {
            Width = previousWidth;
            Height = previousHeight;
            PetImage.Source = previousImage;
            _presentationAnchorX = previousAnchorX;
            _presentationAnchorY = previousAnchorY;
            _settings.PetPlacement = previousPlacement;
            try
            {
                WindowPositionService.ResizeWindowToScreenRect(this, previousRect);
            }
            catch
            {
                // Preserve the original exception; the prior presentation data is restored.
            }
            throw;
        }
        finally
        {
            _applyingPresentation = false;
        }

        try
        {
            _bubbleRepositionRequested(_monitors);
        }
        catch
        {
            // Bubble follow placement is best effort and never invalidates pet selection.
        }
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
        if (!_geometryReady)
            return;

        _interactionGate.Complete();
        _dragPointerStartPx = WindowPositionService.GetCursorPositionPx();
        _dragWindowStartPx = WindowPositionService.GetWindowRectPx(this);
        PetImage.CaptureMouse();
    }

    private void OnPetMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_geometryReady || e.LeftButton != MouseButtonState.Pressed || !PetImage.IsMouseCaptured)
            return;

        var current = WindowPositionService.GetCursorPositionPx();
        var dx = current.X - _dragPointerStartPx.X;
        var dy = current.Y - _dragPointerStartPx.Y;

        var transition = _interactionGate.ObserveMove(dx, dy);
        if (transition is not null)
            RaisePetEvent(transition);
        if (!_interactionGate.IsDragging)
            return;

        var moved = WindowPositionService.MoveByScreenDelta(
            _dragWindowStartPx,
            _dragPointerStartPx,
            current);
        WindowPositionService.MoveWindowToScreenRect(this, moved);
        _bubbleRepositionRequested(_monitors);
    }

    private async void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        var completedDrag = _interactionGate.IsDragging;
        var transition = _interactionGate.Complete();
        if (transition is not null)
            RaisePetEvent(transition);
        PetImage.ReleaseMouseCapture();

        if (!_geometryReady)
            return;

        if (!completedDrag)
            _toggleChatRequested();

        if (completedDrag)
        {
            _monitors = WindowPositionService.GetMonitors();
            WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
            PersistPetPlacement();
            await _settingsService.FlushAsync(CancellationToken.None);
        }
    }

    private async void OnPetLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_geometryReady || !_interactionGate.IsDragging)
            return;

        var transition = _interactionGate.Complete();
        if (transition is not null)
            RaisePetEvent(transition);
        _monitors = WindowPositionService.GetMonitors();
        WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
        PersistPetPlacement();
        await _settingsService.FlushAsync(CancellationToken.None);
    }

    private void OnToggleChatMenuItemClick(object sender, RoutedEventArgs e)
    {
        _toggleChatRequested();
    }

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e)
    {
        _exitRequested();
    }

    private void OnPetMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        RaisePetEvent(new PetEvent.HoverEntered());

    private void OnPetMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        RaisePetEvent(new PetEvent.HoverLeft());

    private void RaisePetEvent(PetEvent petEvent)
    {
        try
        {
            _petEventRaised(petEvent);
        }
        catch
        {
            // Animation is cosmetic; native window interaction must remain usable.
        }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_geometryReady || _applyingPresentation)
            return;

        PersistPetPlacement();
        if (_settings.ChatWindow.PlacementMode == "FollowPet")
            _bubbleRepositionRequested(_monitors);
    }

    private void PersistPetPlacement()
    {
        if (!_geometryReady)
            return;

        UpdatePersistedPlacementWithoutSave();
        _settingsService.RequestSave(_settings);
    }

    private void UpdatePersistedPlacementWithoutSave()
    {
        var placement = WindowPositionService.CapturePlacement(this, _monitors);
        _settings.PetPlacement.MonitorId = placement.MonitorId;
        _settings.PetPlacement.XWithinWorkAreaDip = placement.XWithinWorkAreaDip;
        _settings.PetPlacement.YWithinWorkAreaDip = placement.YWithinWorkAreaDip;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => QueueDisplayGeometryRefresh();

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            QueueDisplayGeometryRefresh();
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e) =>
        QueueDisplayGeometryRefresh();

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
            if (_interactionGate.IsDragging)
                return;

            WindowPositionService.ClampWindowToAvailableWorkArea(this, _monitors);
            PersistPetPlacement();

            _bubbleRepositionRequested(_monitors);
        }
        catch
        {
            // Display transitions are best effort; preserve the current usable window.
        }
    }

    public void CloseForAppShutdown()
    {
        _allowClose = true;
        Close();
    }

    public void BeginAppShutdown()
    {
        PersistPetPlacement();
        _geometryReady = false;
    }

    private void OnPetWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        _exitRequested();
    }

    private void OnPetWindowClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }
}

internal sealed class PetInteractionEventGate
{
    public bool IsDragging { get; private set; }

    public PetEvent? ObserveMove(double deltaX, double deltaY)
    {
        if (IsDragging || Math.Abs(deltaX) + Math.Abs(deltaY) < 5)
            return null;

        IsDragging = true;
        return new PetEvent.DragStarted();
    }

    public PetEvent? Complete()
    {
        if (!IsDragging)
            return null;

        IsDragging = false;
        return new PetEvent.DragEnded();
    }
}
