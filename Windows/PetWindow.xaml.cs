using System.Windows;
using System.Windows.Input;
using PetGPT.Models;
using PetGPT.Services;

namespace PetGPT.Windows;

public partial class PetWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private ChatBubbleWindow? _bubble;
    private Point _mouseDownScreen;
    private bool _dragging;
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

        PetImage.MouseLeftButtonDown += OnPetMouseDown;
        PetImage.MouseMove += OnPetMouseMove;
        PetImage.MouseLeftButtonUp += OnPetMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.PetPlacement.XWithinWorkAreaDip.HasValue &&
            _settings.PetPlacement.YWithinWorkAreaDip.HasValue)
        {
            Left = _settings.PetPlacement.XWithinWorkAreaDip.Value;
            Top = _settings.PetPlacement.YWithinWorkAreaDip.Value;
        }
        else
        {
            WindowPositionService.PutPetAtDefault(this);
        }
    }

    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _mouseDownScreen = PointToScreen(e.GetPosition(this));
        PetImage.CaptureMouse();
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !PetImage.IsMouseCaptured)
            return;

        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _mouseDownScreen.X;
        var dy = current.Y - _mouseDownScreen.Y;

        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 5)
            return;

        _dragging = true;
        Left += dx;
        Top += dy;
        _mouseDownScreen = current;
        RepositionBubble();
    }

    private async void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        PetImage.ReleaseMouseCapture();

        var completedDrag = _dragging;
        if (!completedDrag)
            ToggleBubble();

        _dragging = false;

        if (completedDrag)
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
        WindowPositionService.PositionBubble(_bubble, this);
        _bubble.Show();
        _bubble.Activate();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        _settings.PetPlacement.XWithinWorkAreaDip = Left;
        _settings.PetPlacement.YWithinWorkAreaDip = Top;
        _settingsService.RequestSave(_settings);
        RepositionBubble();
    }

    private void RepositionBubble()
    {
        if (_bubble is { IsVisible: true })
            WindowPositionService.PositionBubble(_bubble, this);
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
            return;

        _isExiting = true;

        _settings.PetPlacement.XWithinWorkAreaDip = Left;
        _settings.PetPlacement.YWithinWorkAreaDip = Top;
        _settingsService.RequestSave(_settings);
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
}
