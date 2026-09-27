using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Media.Imaging;
using PetGPT.Animation;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Personas;
using PetGPT.Services;
using PetGPT.Windows;

namespace PetGPT.Shell;

public sealed class AppLifetime
{
    private static readonly TimeSpan SettingsFlushTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionEndingWaitTimeout = TimeSpan.FromSeconds(4);

    private readonly System.Windows.Application _application;
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private SingleInstanceGuard? _instanceGuard;
    private SettingsService? _settingsService;
    private AppSettings? _settings;
    private CharacterCatalog? _catalog;
    private PetWindow? _petWindow;
    private ChatBubbleWindow? _bubbleWindow;
    private TrayService? _trayService;
    private PetSelectionService? _petSelectionService;
    private PersonaSession? _personaSession;
    private LocalCommandRouter? _commandRouter;
    private SettingsApplyCoordinator? _settingsApplyCoordinator;
    private readonly SettingsMutationGate _settingsMutationGate = new();
    private CommandWindow? _commandWindow;
    private SettingsWindow? _settingsWindow;
    private AnimationStateEngine? _animationEngine;
    private PetAnimationPlayer? _animationPlayer;
    private BitmapSource? _preparedAnimationIdle;
    private string? _preparedAnimationPackKey;
    private SettingsPetPresentationPlan? _runtimeSelectionPlan;
    private AppShutdownCoordinator? _shutdown;

    public AppLifetime(System.Windows.Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public async Task<bool> StartAsync()
    {
        _shutdown = CreateShutdownCoordinator();

        try
        {
            _instanceGuard = SingleInstanceGuard.TryAcquireCurrentUser();
            if (_instanceGuard is null)
            {
                System.Windows.MessageBox.Show(
                    "PetGPT is already running.",
                    "PetGPT",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                RequestExit();
                return false;
            }

            _settingsService = new SettingsService();
            _settings = _settingsService.Load().Settings;
            _catalog = new CharacterCatalog();
            var catalogResult = _catalog.LoadInstalled();

            _petWindow = new PetWindow(
                _settingsService,
                _settings,
                ToggleChat,
                RequestExit,
                RepositionVisibleBubble,
                HandlePetEvent);
            _animationPlayer = new PetAnimationPlayer(
                _petWindow.AnimationSurface,
                () => _animationClock.Elapsed,
                OnAnimationDeadline,
                OnReactionDisplayed);
            _petSelectionService = new PetSelectionService(
                catalogResult.Packs,
                _settings,
                PetSelectionService.PrepareAsync,
                ApplyPreparedSelection,
                _settingsService.RequestSave);
            _petSelectionService.SelectionChanged += OnSelectionChanged;
            _personaSession = new PersonaSession(
                _settings.Roleplay.Enabled,
                _settings.Roleplay.ActivationMode);
            _bubbleWindow = new ChatBubbleWindow(
                _settings,
                _settingsService,
                _petWindow,
                _personaSession,
                HandlePetEvent,
                RequestExit,
                OpenCommandWindow,
                () => OpenSettings(SettingsSection.PetChats));
            _bubbleWindow.IsVisibleChanged += OnBubbleVisibilityChanged;
            _settingsApplyCoordinator = new SettingsApplyCoordinator(
                _settings,
                candidate => _settingsService.ValidateCandidate(candidate).DiagnosticCode,
                PrepareRuntimeSettingsCandidateAsync,
                ApplyRuntimeSettingsAsync,
                async (candidate, cancellationToken) =>
                    (await _settingsService.SaveImmediateAsync(candidate, cancellationToken)).Succeeded);
            _commandRouter = new LocalCommandRouter(new LocalCommandActions(
                OpenPetChooserAsync: () =>
                {
                    OpenSettings(SettingsSection.Character);
                    return Task.CompletedTask;
                },
                GetInstalledPacks: () => _petSelectionService.AvailablePacks,
                SelectPetByIdAsync: SelectPetByIdAsync,
                RaisePetEvent: HandlePetEvent,
                OpenThemeSettingsAsync: () =>
                {
                    OpenSettings(SettingsSection.Appearance);
                    return Task.CompletedTask;
                },
                NavigateAsync: (intent, _) => NavigateChatAsync(intent)));
            _trayService = new TrayService(
                ToggleChat,
                RequestExit,
                catalogResult.Packs,
                SelectPetAsync,
                NavigateChatAsync,
                _bubbleWindow.HasConfiguredPetChatsHome,
                OpenCommandWindow,
                () => OpenSettings(SettingsSection.PetChats));
            _trayService.SetChatVisible(false);
            await _petSelectionService.InitializeAsync(CancellationToken.None);
            HandlePetEvent(new PetEvent.ChatVisibilityChanged(false));

            _application.MainWindow = _petWindow;
            _petWindow.Show();
            _animationPlayer.SetVisible(true);
            return true;
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"PetGPT could not start.\n\n{exception.Message}",
                "PetGPT",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            RequestExit();
            return false;
        }
    }

    private Task<PetSelectionResult> SelectPetAsync(string id, string version) =>
        _settingsMutationGate.RunAsync(
            cancellationToken => _petSelectionService?.SelectAsync(id, version, cancellationToken) ??
                Task.FromResult(PetSelectionResult.Failed("selection_unavailable")),
            CancellationToken.None);

    private Task<PetSelectionResult> SelectPetByIdAsync(
        string id,
        CancellationToken cancellationToken) =>
        _settingsMutationGate.RunAsync(
            token => _petSelectionService?.SelectByIdAsync(id, token) ??
                Task.FromResult(PetSelectionResult.Failed("selection_unavailable")),
            cancellationToken);

    private void OpenCommandWindow()
    {
        if (_shutdown is not { AcceptsIntents: true } || _commandRouter is null)
            return;
        if (_commandWindow is null)
        {
            _commandWindow = new CommandWindow(_commandRouter)
            {
                Owner = _bubbleWindow?.IsVisible == true ? _bubbleWindow : _petWindow
            };
            _commandWindow.Closed += (_, _) => _commandWindow = null;
            _commandWindow.Show();
        }
        else
        {
            if (!_commandWindow.IsVisible)
                _commandWindow.Show();
            _commandWindow.Activate();
        }
    }

    private void OpenSettings(SettingsSection section)
    {
        if (_shutdown is not { AcceptsIntents: true } ||
            _settings is null ||
            _settingsApplyCoordinator is null ||
            _petSelectionService is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                _settings,
                _petSelectionService.AvailablePacks,
                GetDiagnosticCodes(),
                ApplySettingsFromWindowAsync,
                ImportPackAsync,
                () => _petSelectionService.AvailablePacks)
            {
                Owner = _bubbleWindow?.IsVisible == true ? _bubbleWindow : _petWindow
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.FocusSection(section);
    }

    private async Task<SettingsApplyResult> ApplySettingsFromWindowAsync(
        AppSettings candidate,
        CancellationToken cancellationToken)
    {
        if (_shutdown is not { AcceptsIntents: true } || _settingsApplyCoordinator is null)
            return SettingsApplyResult.Failure("shutdown_started");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await _settingsMutationGate.RunAsync(
            token => _settingsApplyCoordinator.ApplyAsync(candidate, token),
            linkedCancellation.Token);
    }

    private Task<bool> PrepareRuntimeSettingsCandidateAsync(
        AppSettings candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_petSelectionService is null || _petWindow is null || _bubbleWindow is null)
            return Task.FromResult(false);
        if (!candidate.SelectedPackVersions.TryGetValue(candidate.SelectedPetId, out var version))
            return Task.FromResult(false);
        var pack = _petSelectionService.AvailablePacks.SingleOrDefault(item =>
            item.Id.Equals(candidate.SelectedPetId, StringComparison.Ordinal) &&
            item.Version.Equals(version, StringComparison.Ordinal));
        if (pack is null)
            return Task.FromResult(false);

        var plan = SettingsPetPresentationPlan.Create(
            candidate,
            _petSelectionService.CurrentPetId,
            _petSelectionService.CurrentVersion);
        candidate.PetPlacement = _petWindow.ProjectPlacement(pack, plan.Scale);
        _bubbleWindow.PrepareRuntimeSettingsCandidate(candidate);
        return Task.FromResult(true);
    }

    private IReadOnlyList<string> GetDiagnosticCodes() =>
        (_settingsService?.DiagnosticCodes ?? [])
            .Concat(_petSelectionService?.DiagnosticCodes ?? [])
            .Concat(_catalog?.LoadInstalled().DiagnosticCodes ?? [])
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();

    private async Task<CharacterImportResult> ImportPackAsync(
        string selectedPath,
        CancellationToken cancellationToken)
    {
        if (_catalog is null || _petSelectionService is null)
            return CharacterImportResult.Failure("import_unavailable");
        var result = await _catalog.ImportAsync(selectedPath, cancellationToken);
        if (!result.Succeeded)
            return result;

        var catalog = _catalog.LoadInstalled();
        await _petSelectionService.RefreshAvailablePacksAsync(catalog.Packs, cancellationToken);
        _trayService?.RefreshPacks(
            catalog.Packs,
            _petSelectionService.CurrentPetId,
            _petSelectionService.CurrentVersion);
        return result;
    }

    private async Task<bool> ApplyRuntimeSettingsAsync(
        AppSettings candidate,
        AppSettings previous,
        CancellationToken cancellationToken)
    {
        if (_petSelectionService is null || _petWindow is null || _bubbleWindow is null)
            return false;
        SettingsPetPresentationPlan plan;
        try
        {
            plan = SettingsPetPresentationPlan.Create(
                candidate,
                _petSelectionService.CurrentPetId,
                _petSelectionService.CurrentVersion);
        }
        catch
        {
            return false;
        }

        if (!_bubbleWindow.ApplyRuntimePreferencesAndGeometry(candidate))
            return false;

        if (plan.RequiresSelection)
        {
            _runtimeSelectionPlan = plan;
            try
            {
                var selection = await _petSelectionService.SelectAsync(
                    plan.PetId,
                    plan.Version,
                    updateSettings: false,
                    cancellationToken);
                if (!selection.Succeeded)
                    return false;
            }
            finally
            {
                _runtimeSelectionPlan = null;
            }
        }

        var pack = _petSelectionService.CurrentPack;
        if (pack is null)
            return false;
        if (!plan.RequiresSelection)
        {
            _petWindow.ApplyScale(pack, plan.Scale);
            try
            {
                _animationPlayer?.SetReducedMotion(plan.ReducedMotion);
                HandlePetEvent(new PetEvent.ReducedMotionChanged(plan.ReducedMotion));
            }
            catch
            {
                // Animation rendering is cosmetic and does not invalidate settings commit.
            }
        }
        _bubbleWindow.CommitRuntimeIdentity(previous, candidate);
        _trayService?.SetPetChatsConfigured(candidate.ChatHomeUrl is not null);
        return true;
    }

    private void ApplyPreparedSelection(PreparedPetSelection prepared)
    {
        var scale = _runtimeSelectionPlan is { } plan && plan.Matches(prepared.Pack)
            ? plan.Scale
            : _settings?.PetOptions.GetValueOrDefault(prepared.Pack.Id)?.Scale ?? 1;
        _petWindow?.ApplySelection(prepared, scale);
        _trayService?.ApplySelection(prepared.Pack, prepared.TrayIcon);
        _preparedAnimationIdle = prepared.IdleImage;
        _preparedAnimationPackKey = PackKey(prepared.Pack);
    }

    private void OnSelectionChanged(object? sender, PetSelectionChangedEventArgs e)
    {
        _bubbleWindow?.ApplySelectedPack(e.Pack);
        if (_animationPlayer is null || _settings is null)
            return;

        var fallbackIdle = _preparedAnimationPackKey == PackKey(e.Pack)
            ? _preparedAnimationIdle
            : _petWindow?.CurrentFrame;
        if (fallbackIdle is null)
            return;

        var reducedMotion = _runtimeSelectionPlan is { } plan && plan.Matches(e.Pack)
            ? plan.ReducedMotion
            : _settings.PetOptions.TryGetValue(e.Pack.Id, out var options) && options.ReducedMotion;
        var now = _animationClock.Elapsed;
        PlaybackDecision decision;
        if (_animationEngine is null)
        {
            _animationEngine = new AnimationStateEngine(e.Pack, reducedMotion);
            decision = _animationEngine.Current(now);
        }
        else
        {
            decision = _animationEngine.Apply(new PetEvent.PetChanged(e.Pack, reducedMotion), now);
        }

        try
        {
            _animationPlayer.InstallPack(e.Pack, fallbackIdle, reducedMotion);
            _animationPlayer.Apply(decision);
        }
        catch
        {
            // The transaction already committed a usable frozen idle image.
        }
        finally
        {
            _preparedAnimationIdle = null;
            _preparedAnimationPackKey = null;
        }
    }

    private void HandlePetEvent(PetEvent petEvent)
    {
        if (_animationEngine is null || _animationPlayer is null)
            return;

        var decision = _animationEngine.Apply(petEvent, _animationClock.Elapsed);
        _animationPlayer.Apply(decision);
    }

    private void OnAnimationDeadline(TimeSpan monotonicNow)
    {
        if (_animationEngine is null || _animationPlayer is null)
            return;

        var decision = _animationEngine.Apply(new PetEvent.DeadlineElapsed(), monotonicNow);
        _animationPlayer.Apply(decision);
    }

    private void OnReactionDisplayed(string playbackKey, TimeSpan monotonicNow)
    {
        if (_animationEngine is null || _animationPlayer is null)
            return;

        var decision = _animationEngine.Apply(
            new PetEvent.ReactionDisplayed(playbackKey),
            monotonicNow);
        _animationPlayer.Apply(decision);
    }

    private static string PackKey(CharacterPack pack) => $"{pack.Id}@{pack.Version}";

    public void ToggleChat()
    {
        if (_shutdown is not { AcceptsIntents: true } ||
            _bubbleWindow is null ||
            _petWindow is null)
        {
            return;
        }

        if (_bubbleWindow.IsVisible)
        {
            _bubbleWindow.Hide();
        }
        else
        {
            ShowChat();
        }

        _trayService?.SetChatVisible(_bubbleWindow.IsVisible);
    }

    private async Task<NavigationResult> NavigateChatAsync(NavigationIntent intent)
    {
        if (_shutdown is not { AcceptsIntents: true } ||
            _bubbleWindow is null ||
            _petWindow is null)
        {
            return NavigationResult.Unavailable;
        }

        if (!_bubbleWindow.IsVisible)
            ShowChat();
        return await _bubbleWindow.NavigateAsync(intent);
    }

    private void ShowChat()
    {
        if (_bubbleWindow is null || _petWindow is null)
            return;
        var monitors = _petWindow.CurrentMonitors.Count > 0
            ? _petWindow.CurrentMonitors
            : WindowPositionService.GetMonitors();
        _bubbleWindow.PreparePlacement(monitors);
        _bubbleWindow.Show();
        _bubbleWindow.Activate();
    }

    public void RequestExit()
    {
        if (_shutdown is not null)
            _ = _shutdown.ShutdownAsync();
    }

    public void HandleSessionEnding()
    {
        if (_shutdown is null)
            return;

        var shutdownTask = _shutdown.ShutdownAsync(shutdownApplication: false);
        if (shutdownTask.IsCompleted)
            return;

        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send,
            _application.Dispatcher)
        {
            Interval = SessionEndingWaitTimeout
        };

        void StopWaiting()
        {
            if (frame.Continue)
                frame.Continue = false;
        }

        timer.Tick += (_, _) => StopWaiting();
        _ = shutdownTask.ContinueWith(
            _ =>
            {
                try
                {
                    _application.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Send,
                        new Action(StopWaiting));
                }
                catch
                {
                    // WPF may already be completing the Windows session shutdown.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        timer.Start();
        try
        {
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    private void RepositionVisibleBubble(IReadOnlyList<MonitorInfo> monitors)
    {
        if (_shutdown is { AcceptsIntents: true } && _bubbleWindow is { IsVisible: true })
            _bubbleWindow.RefreshDisplayGeometry(monitors);
    }

    private void OnBubbleVisibilityChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        _trayService?.SetChatVisible(_bubbleWindow?.IsVisible == true);
        HandlePetEvent(new PetEvent.ChatVisibilityChanged(_bubbleWindow?.IsVisible == true));
    }

    private AppShutdownCoordinator CreateShutdownCoordinator() =>
        new(
            new AppShutdownOperations(
                PrepareOwnedSurfacesForShutdown: () =>
                {
                    _lifetimeCancellation.Cancel();
                    HandlePetEvent(new PetEvent.Exit());
                    _animationPlayer?.Dispose();
                    _animationPlayer = null;
                    _personaSession?.Dispose();
                    _settingsWindow?.BeginAppShutdown();
                    _settingsWindow = null;
                    _commandWindow?.Close();
                    _commandWindow = null;
                    _bubbleWindow?.BeginAppShutdown();
                    _petWindow?.BeginAppShutdown();
                },
                FlushSettingsAsync: cancellationToken =>
                    _settingsService?.FlushAsync(cancellationToken) ?? Task.CompletedTask,
                DisposeBrowserAsync: () =>
                    _bubbleWindow?.DisposeBrowserAsync() ?? Task.CompletedTask,
                CloseBubble: () =>
                {
                    if (_bubbleWindow is not null)
                    {
                        _bubbleWindow.IsVisibleChanged -= OnBubbleVisibilityChanged;
                        _bubbleWindow.CloseForAppShutdown();
                    }
                },
                ClosePet: () => _petWindow?.CloseForAppShutdown(),
                DisposeTray: () =>
                {
                    _trayService?.Dispose();
                    _trayService = null;
                    if (_petSelectionService is not null)
                        _petSelectionService.SelectionChanged -= OnSelectionChanged;
                    _petSelectionService?.Dispose();
                    _petSelectionService = null;
                    _personaSession = null;
                    _commandRouter = null;
                    _settingsApplyCoordinator = null;
                },
                ReleaseInstanceGuard: () =>
                {
                    _instanceGuard?.Dispose();
                    _instanceGuard = null;
                    _lifetimeCancellation.Dispose();
                },
                ShutdownApplication: _application.Shutdown),
            SettingsFlushTimeout);
}

internal sealed record AppShutdownOperations(
    Action PrepareOwnedSurfacesForShutdown,
    Func<CancellationToken, Task> FlushSettingsAsync,
    Func<Task> DisposeBrowserAsync,
    Action CloseBubble,
    Action ClosePet,
    Action DisposeTray,
    Action ReleaseInstanceGuard,
    Action ShutdownApplication);

internal sealed class AppShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly AppShutdownOperations _operations;
    private readonly TimeSpan _flushTimeout;
    private Task? _shutdownTask;

    public AppShutdownCoordinator(AppShutdownOperations operations, TimeSpan flushTimeout)
    {
        _operations = operations;
        _flushTimeout = flushTimeout;
    }

    public bool AcceptsIntents
    {
        get
        {
            lock (_gate)
                return _shutdownTask is null;
        }
    }

    public Task ShutdownAsync(bool shutdownApplication = true)
    {
        lock (_gate)
            return _shutdownTask ??= RunShutdownAsync(shutdownApplication);
    }

    private async Task RunShutdownAsync(bool shutdownApplication)
    {
        // Defer the first callback so the shared task is published before any
        // window-close callback can re-enter the shutdown path.
        await Task.Yield();

        InvokeSafely(_operations.PrepareOwnedSurfacesForShutdown);

        using (var flushCancellation = new CancellationTokenSource(_flushTimeout))
        {
            Task? flushTask = null;
            try
            {
                flushTask = _operations.FlushSettingsAsync(flushCancellation.Token);
                await flushTask.WaitAsync(_flushTimeout);
            }
            catch
            {
                // Shutdown is best effort after a bounded persistence attempt.
                flushCancellation.Cancel();
                if (flushTask is not null)
                {
                    _ = flushTask.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        try
        {
            await _operations.DisposeBrowserAsync();
        }
        catch
        {
            // A browser teardown failure must not strand the process.
        }

        InvokeSafely(_operations.CloseBubble);
        InvokeSafely(_operations.ClosePet);
        InvokeSafely(_operations.DisposeTray);
        InvokeSafely(_operations.ReleaseInstanceGuard);
        if (shutdownApplication)
            InvokeSafely(_operations.ShutdownApplication);
    }

    private static void InvokeSafely(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Continue the deterministic teardown chain.
        }
    }
}

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceGuard? TryAcquireCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userScope = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userScope))
            userScope = $"{Environment.UserDomainName}\\{Environment.UserName}";

        return TryAcquire(userScope);
    }

    internal static SingleInstanceGuard? TryAcquire(string userScope)
    {
        var mutex = new Mutex(initiallyOwned: false, BuildMutexName(userScope));
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return acquired ? new SingleInstanceGuard(mutex) : null;
        }
        finally
        {
            if (!acquired)
                mutex.Dispose();
        }
    }

    internal static string BuildMutexName(string userScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userScope);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(userScope));
        return $"Global\\PetGPT-v2-{Convert.ToHexString(digest)}";
    }

    public void Dispose()
    {
        if (!_ownsMutex)
            return;

        _ownsMutex = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ownership may already have been abandoned during process teardown.
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}

internal sealed record SettingsPetPresentationPlan(
    string PetId,
    string Version,
    double Scale,
    bool ReducedMotion,
    bool RequiresSelection)
{
    public static SettingsPetPresentationPlan Create(
        AppSettings candidate,
        string? currentPetId,
        string? currentVersion)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.SelectedPackVersions.TryGetValue(candidate.SelectedPetId, out var version))
            throw new InvalidOperationException("The selected exact pack version is unavailable.");
        var options = candidate.PetOptions.GetValueOrDefault(candidate.SelectedPetId) ??
            new PetOptionSettings();
        return new SettingsPetPresentationPlan(
            candidate.SelectedPetId,
            version,
            options.Scale,
            options.ReducedMotion,
            !string.Equals(currentPetId, candidate.SelectedPetId, StringComparison.Ordinal) ||
            !string.Equals(currentVersion, version, StringComparison.Ordinal));
    }

    public bool Matches(CharacterPack pack) =>
        pack.Id.Equals(PetId, StringComparison.Ordinal) &&
        pack.Version.Equals(Version, StringComparison.Ordinal);
}

internal sealed record SettingsApplyResult(
    bool Succeeded,
    string? DiagnosticCode,
    AppSettings? AppliedSnapshot)
{
    public static SettingsApplyResult Success(AppSettings snapshot) => new(true, null, snapshot.Copy());
    public static SettingsApplyResult Failure(string code) => new(false, code, null);
}

internal sealed class SettingsApplyCoordinator
{
    private readonly AppSettings _live;
    private readonly Func<AppSettings, string?> _validate;
    private readonly Func<AppSettings, CancellationToken, Task<bool>> _prepareAsync;
    private readonly Func<AppSettings, AppSettings, CancellationToken, Task<bool>> _applyRuntimeAsync;
    private readonly Func<AppSettings, CancellationToken, Task<bool>> _persistAsync;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public SettingsApplyCoordinator(
        AppSettings live,
        Func<AppSettings, string?> validate,
        Func<AppSettings, CancellationToken, Task<bool>> prepareAsync,
        Func<AppSettings, AppSettings, CancellationToken, Task<bool>> applyRuntimeAsync,
        Func<AppSettings, CancellationToken, Task<bool>> persistAsync)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _prepareAsync = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
        _applyRuntimeAsync = applyRuntimeAsync ?? throw new ArgumentNullException(nameof(applyRuntimeAsync));
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
    }

    public async Task<SettingsApplyResult> ApplyAsync(
        AppSettings candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            var detached = candidate.Copy();
            var validationCode = _validate(detached);
            if (validationCode is not null)
                return SettingsApplyResult.Failure(validationCode);

            var previous = _live.Copy();
            if (!await PrepareSafelyAsync(detached, cancellationToken))
                return SettingsApplyResult.Failure("runtime_prepare_failed");

            // Ordinary window callbacks persist the shared live object. Stage the
            // complete candidate in memory for the entire write so any save queued
            // during the await carries candidate preferences plus current geometry.
            _live.CopyFrom(detached);
            bool persisted;
            try
            {
                persisted = await _persistAsync(detached, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _live.CopyFrom(previous);
                await ApplyRuntimeSafelyAsync(previous, previous, CancellationToken.None);
                throw;
            }
            if (!persisted)
            {
                _live.CopyFrom(previous);
                await ApplyRuntimeSafelyAsync(previous, previous, CancellationToken.None);
                return SettingsApplyResult.Failure("settings_write_failed");
            }

            MergeRuntimeGeometry(detached, _live);
            bool runtimeApplied;
            try
            {
                runtimeApplied = await ApplyRuntimeSafelyAsync(detached, previous, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _live.CopyFrom(previous);
                await RestoreAfterRuntimeFailureAsync(previous);
                throw;
            }
            if (!runtimeApplied)
            {
                _live.CopyFrom(previous);
                await RestoreAfterRuntimeFailureAsync(previous);
                return SettingsApplyResult.Failure("runtime_apply_failed");
            }

            return SettingsApplyResult.Success(_live);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private static void MergeRuntimeGeometry(AppSettings target, AppSettings live)
    {
        target.PetPlacement = live.PetPlacement.Copy();
        target.ChatWindow.WidthDip = live.ChatWindow.WidthDip;
        target.ChatWindow.HeightDip = live.ChatWindow.HeightDip;
        target.ChatWindow.MonitorId = live.ChatWindow.MonitorId;
        target.ChatWindow.XWithinWorkAreaDip = live.ChatWindow.XWithinWorkAreaDip;
        target.ChatWindow.YWithinWorkAreaDip = live.ChatWindow.YWithinWorkAreaDip;
    }

    private async Task<bool> PrepareSafelyAsync(
        AppSettings snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _prepareAsync(snapshot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task RestoreAfterRuntimeFailureAsync(AppSettings previous)
    {
        try
        {
            await _persistAsync(previous, CancellationToken.None);
        }
        catch
        {
            // The original failure remains authoritative; SettingsService records write diagnostics.
        }
        await ApplyRuntimeSafelyAsync(previous, previous, CancellationToken.None);
    }

    private async Task<bool> ApplyRuntimeSafelyAsync(
        AppSettings snapshot,
        AppSettings previous,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _applyRuntimeAsync(snapshot, previous, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class SettingsMutationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
    }
}
