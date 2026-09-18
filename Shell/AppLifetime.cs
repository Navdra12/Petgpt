using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Services;
using PetGPT.Windows;

namespace PetGPT.Shell;

public sealed class AppLifetime
{
    private static readonly TimeSpan SettingsFlushTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionEndingWaitTimeout = TimeSpan.FromSeconds(4);

    private readonly System.Windows.Application _application;
    private SingleInstanceGuard? _instanceGuard;
    private SettingsService? _settingsService;
    private AppSettings? _settings;
    private PetWindow? _petWindow;
    private ChatBubbleWindow? _bubbleWindow;
    private TrayService? _trayService;
    private PetSelectionService? _petSelectionService;
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
            var catalogResult = new CharacterCatalog().LoadInstalled();

            _petWindow = new PetWindow(
                _settingsService,
                _settings,
                ToggleChat,
                RequestExit,
                RepositionVisibleBubble);
            _petSelectionService = new PetSelectionService(
                catalogResult.Packs,
                _settings,
                PetSelectionService.PrepareAsync,
                ApplyPreparedSelection,
                _settingsService.RequestSave);
            _trayService = new TrayService(
                ToggleChat,
                RequestExit,
                catalogResult.Packs,
                SelectPetAsync);
            _trayService.SetChatVisible(false);
            await _petSelectionService.InitializeAsync(CancellationToken.None);

            _bubbleWindow = new ChatBubbleWindow(
                _settings,
                _settingsService,
                _petWindow,
                RequestExit);
            _bubbleWindow.IsVisibleChanged += OnBubbleVisibilityChanged;

            _application.MainWindow = _petWindow;
            _petWindow.Show();
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
        _petSelectionService?.SelectAsync(id, version, CancellationToken.None) ??
        Task.FromResult(PetSelectionResult.Failed("selection_unavailable"));

    private void ApplyPreparedSelection(PreparedPetSelection prepared)
    {
        _petWindow?.ApplySelection(prepared);
        _trayService?.ApplySelection(prepared.Pack, prepared.TrayIcon);
    }

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
            var monitors = _petWindow.CurrentMonitors.Count > 0
                ? _petWindow.CurrentMonitors
                : WindowPositionService.GetMonitors();
            _bubbleWindow.PreparePlacement(monitors);
            _bubbleWindow.Show();
            _bubbleWindow.Activate();
        }

        _trayService?.SetChatVisible(_bubbleWindow.IsVisible);
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
    }

    private AppShutdownCoordinator CreateShutdownCoordinator() =>
        new(
            new AppShutdownOperations(
                PrepareOwnedSurfacesForShutdown: () =>
                {
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
                    _petSelectionService?.Dispose();
                    _petSelectionService = null;
                },
                ReleaseInstanceGuard: () =>
                {
                    _instanceGuard?.Dispose();
                    _instanceGuard = null;
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
