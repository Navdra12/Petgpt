using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Personas;
using PetGPT.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfGrid = System.Windows.Controls.Grid;
using WpfRowDefinition = System.Windows.Controls.RowDefinition;
using WpfScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace PetGPT.Windows;

public partial class ChatBubbleWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly Window _pet;
    private readonly ChatWebViewService _chatService;
    private readonly ChatNavigationService _navigationService;
    private readonly PersonaSession _personaSession;
    private readonly Action _exitRequested;
    private readonly double _normalMinWidthDip;
    private readonly double _normalMinHeightDip;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _geometryReady;
    private bool _applyingGeometry;
    private bool _allowClose;
    private bool _appShuttingDown;
    private Task? _browserInitializationTask;

    public ChatBubbleWindow(
        AppSettings settings,
        SettingsService settingsService,
        Window pet,
        PersonaSession personaSession,
        Action exitRequested)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _pet = pet;
        _personaSession = personaSession ?? throw new ArgumentNullException(nameof(personaSession));
        _exitRequested = exitRequested;
        _normalMinWidthDip = MinWidth;
        _normalMinHeightDip = MinHeight;

        Width = settings.ChatWindow.WidthDip;
        Height = settings.ChatWindow.HeightDip;

        _chatService = new ChatWebViewService(ChatWebView, settings.ChatHomeUrl);
        _navigationService = new ChatNavigationService(
            settings.ChatHomeUrl,
            () => _chatService.CurrentUri,
            uri => _chatService.Navigate(uri),
            ConfirmLeaveAsync);
        _chatService.SourceChanged += OnChatSourceChanged;
        _chatService.BridgeStatusChanged += OnBridgeStatusChanged;
        _chatService.PersonaContextChanged += OnPersonaContextChanged;
        _chatService.SubmissionObserved += OnSubmissionObserved;
        _personaSession.StatusChanged += OnPersonaStatusChanged;

        var configured = _navigationService.HasConfiguredHome;
        NewPetChatButton.IsEnabled = configured;
        HistoryButton.IsEnabled = configured;
        PetChatsStatusText.Text = configured ? string.Empty : "PetChats not configured";
        RefreshPersonaUi();

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

        _ = EnsureBrowserInitializedAsync();
    }

    public bool HasConfiguredPetChatsHome => _navigationService.HasConfiguredHome;

    public void ApplySelectedPack(CharacterPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _chatService.SetSelectedTheme(_settings.ThemesEnabled ? pack.Theme : null);
        _personaSession.SelectPack(pack);
    }

    public async Task<NavigationResult> NavigateAsync(NavigationIntent intent)
    {
        if (_appShuttingDown)
            return NavigationResult.Unavailable;
        if ((intent is NavigationIntent.NewPetChat or NavigationIntent.History) &&
            !_navigationService.HasConfiguredHome)
        {
            PetChatsStatusText.Text = "PetChats not configured";
            return NavigationResult.InvalidHome;
        }

        await EnsureBrowserInitializedAsync();
        if (_chatService.State != ChatWebViewLifecycleState.Ready)
            return NavigationResult.Unavailable;

        var result = await _navigationService.NavigateAsync(intent, CancellationToken.None);
        _chatService.SetHistoryMode(_navigationService.HistoryMode);
        return result;
    }

    private Task EnsureBrowserInitializedAsync() =>
        _browserInitializationTask ??= InitializeBrowserWithErrorHandlingAsync();

    private async Task InitializeBrowserWithErrorHandlingAsync()
    {
        try
        {
            await _chatService.InitializeAsync(_settings.CompactMode, _settings.ThemesEnabled);
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
            FileName = _navigationService.ResolveExternalBrowserUri().AbsoluteUri,
            UseShellExecute = true
        });
    }

    private async void OnNewPetChat(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(NavigationIntent.NewPetChat);
    }

    private async void OnHistory(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(NavigationIntent.History);
    }

    private Task<bool> ConfirmLeaveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = System.Windows.MessageBox.Show(
            "ChatGPT may be generating a response or contain an unsent draft. Choose Yes to Leave, or No to Stay.",
            "Leave this chat?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    private void OnChatSourceChanged(object? sender, ChatSourceChangedEventArgs e)
    {
        _navigationService.ObserveSource(e.Source);
        _chatService.SetHistoryMode(_navigationService.HistoryMode);
    }

    private void OnBridgeStatusChanged(object? sender, EventArgs e)
    {
        _navigationService.UpdateGuardState(
            _chatService.GenerationState,
            _chatService.ComposerDraftState);
        _personaSession.ObserveGeneration(_chatService.GenerationState);
        if (_chatService.CurrentDocument is not null && _chatService.IsAdapterReady)
            _personaSession.ObserveSubmissionCapability(_chatService.BridgeCapabilities.Submission);
    }

    private void OnPersonaContextChanged(object? sender, PersonaChatContextChangedEventArgs e)
    {
        if (e.Invalidated)
            _personaSession.InvalidateDocument();
        else if (e.Context is not null)
            _personaSession.ObserveContext(e.Context);
    }

    private void OnSubmissionObserved(object? sender, BridgeSubmissionObservation observation) =>
        _personaSession.ObserveSubmission(observation);

    private void OnPersonaStatusChanged(object? sender, EventArgs e) => RefreshPersonaUi();

    private void RefreshPersonaUi()
    {
        PersonaStatusText.Text = _personaSession.StatusText;
        ApplyPersonaButton.IsEnabled = _personaSession.CanApply && !_appShuttingDown;
        ReviewPersonaButton.IsEnabled = _personaSession.CanReview && !_appShuttingDown;
        CopyPersonaButton.IsEnabled = _personaSession.CanCopy && !_appShuttingDown;
    }

    private async void OnApplyPersona(object sender, RoutedEventArgs e)
    {
        if (_appShuttingDown)
            return;
        var snapshot = _chatService.GetPersonaStageSnapshot();
        if (snapshot is null)
            return;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _personaSession.ApplyAsync(
                snapshot,
                _chatService.StagePersonaAsync,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (!_appShuttingDown)
        {
            System.Windows.MessageBox.Show(
                "The composer did not confirm staging. Use Copy context, then paste and send it manually.",
                "Persona staging unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnCopyPersona(object sender, RoutedEventArgs e)
    {
        if (_appShuttingDown)
            return;
        _personaSession.CopyContext(text =>
        {
            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            return true;
        });
    }

    private void OnReviewPersona(object sender, RoutedEventArgs e)
    {
        if (_appShuttingDown || _personaSession.Context is not { } context)
            return;

        var close = new WpfButton
        {
            Content = "Close",
            Padding = new Thickness(18, 6, 18, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true
        };
        var layout = new WpfGrid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new WpfRowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new WpfRowDefinition { Height = GridLength.Auto });
        var text = new WpfTextBox
        {
            Text = context.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = WpfScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WpfScrollBarVisibility.Auto,
            FontFamily = new WpfFontFamily("Consolas")
        };
        WpfGrid.SetRow(text, 0);
        WpfGrid.SetRow(close, 1);
        close.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(text);
        layout.Children.Add(close);
        var review = new Window
        {
            Title = $"Review {context.CharacterId} context",
            Owner = this,
            Width = 660,
            Height = 620,
            MinWidth = 420,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
            ShowInTaskbar = false
        };
        close.Click += (_, _) => review.Close();
        review.ShowDialog();
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
        RefreshPersonaUi();
    }

    public async Task DisposeBrowserAsync()
    {
        _chatService.SourceChanged -= OnChatSourceChanged;
        _chatService.BridgeStatusChanged -= OnBridgeStatusChanged;
        _chatService.PersonaContextChanged -= OnPersonaContextChanged;
        _chatService.SubmissionObserved -= OnSubmissionObserved;
        _personaSession.StatusChanged -= OnPersonaStatusChanged;
        await _chatService.DisposeAsync();
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
