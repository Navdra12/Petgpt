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

    public PetWindow(SettingsService settingsService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _settings = _settingsService.Load();

        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        Closing += OnPetWindowClosing;

        PetImage.MouseLeftButtonDown += OnPetMouseDown;
        PetImage.MouseMove += OnPetMouseMove;
        PetImage.MouseLeftButtonUp += OnPetMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.PetLeft.HasValue && _settings.PetTop.HasValue)
        {
            Left = _settings.PetLeft.Value;
            Top = _settings.PetTop.Value;
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

    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        PetImage.ReleaseMouseCapture();

        if (!_dragging)
            ToggleBubble();

        _dragging = false;
    }

    private void OnToggleChatMenuItemClick(object sender, RoutedEventArgs e)
    {
        ToggleBubble();
    }

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void ToggleBubble()
    {
        if (_bubble is { IsVisible: true })
        {
            _bubble.Hide();
            return;
        }

        _bubble ??= new ChatBubbleWindow(_settings, this);
        WindowPositionService.PositionBubble(_bubble, this);
        _bubble.Show();
        _bubble.Activate();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        _settings.PetLeft = Left;
        _settings.PetTop = Top;
        _settingsService.Save(_settings);
        RepositionBubble();
    }

    private void RepositionBubble()
    {
        if (_bubble is { IsVisible: true })
            WindowPositionService.PositionBubble(_bubble, this);
    }

    private void ExitApplication()
    {
        if (_isExiting)
            return;

        _isExiting = true;

        _settings.PetLeft = Left;
        _settings.PetTop = Top;
        _settingsService.Save(_settings);

        _bubble?.CloseForAppShutdown();
        Close();
    }

    private void OnPetWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            _isExiting = true;
            _bubble?.CloseForAppShutdown();
        }
    }
}
