using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PetGPT.Models;
using PetGPT.Services;

namespace PetGPT.Windows;

public partial class ChatBubbleWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Window _pet;
    private readonly ChatWebViewService _chatService;
    private bool _allowClose;

    public ChatBubbleWindow(AppSettings settings, Window pet)
    {
        InitializeComponent();

        _settings = settings;
        _pet = pet;

        Width = settings.BubbleWidth;
        Height = settings.BubbleHeight;

        _chatService = new ChatWebViewService(ChatWebView);

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowPositionService.PositionBubble(this, _pet);
        await _chatService.InitializeAsync(_settings.CompactMode);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _settings.BubbleWidth = Width;
        _settings.BubbleHeight = Height;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Hide();
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/",
            UseShellExecute = true
        });
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        _chatService.Reload();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Hide();
    }

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
