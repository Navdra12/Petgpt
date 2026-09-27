using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using PetGPT.Characters;
using PetGPT.Models;
using DrawingIcon = System.Drawing.Icon;

namespace PetGPT.Shell;

public sealed record PetSelectionResult(bool Succeeded, bool Changed, string? DiagnosticCode)
{
    internal static PetSelectionResult Selected() => new(true, true, null);
    internal static PetSelectionResult Unchanged() => new(true, false, null);
    internal static PetSelectionResult Failed(string code) => new(false, false, code);
}

public sealed class PetSelectionChangedEventArgs(CharacterPack pack) : EventArgs
{
    public CharacterPack Pack { get; } = pack ?? throw new ArgumentNullException(nameof(pack));
}

internal sealed class PreparedPetSelection : IDisposable
{
    private IDisposable? _ownedTrayIcon;

    internal PreparedPetSelection(
        CharacterPack pack,
        BitmapSource idleImage,
        DrawingIcon trayIcon,
        IDisposable ownedTrayIcon,
        bool usesFallbackTrayIcon)
    {
        Pack = pack ?? throw new ArgumentNullException(nameof(pack));
        IdleImage = idleImage ?? throw new ArgumentNullException(nameof(idleImage));
        TrayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _ownedTrayIcon = ownedTrayIcon ?? throw new ArgumentNullException(nameof(ownedTrayIcon));
        UsesFallbackTrayIcon = usesFallbackTrayIcon;
    }

    public CharacterPack Pack { get; }
    public BitmapSource IdleImage { get; }
    public DrawingIcon TrayIcon { get; }
    public bool UsesFallbackTrayIcon { get; }

    public void Dispose() => Interlocked.Exchange(ref _ownedTrayIcon, null)?.Dispose();
}

public sealed class PetSelectionService : IDisposable
{
    private const int MaximumDiagnostics = 32;

    private readonly Dictionary<(string Id, string Version), CharacterPack> _packs;
    private readonly AppSettings _settings;
    private readonly Func<CharacterPack, CancellationToken, Task<PreparedPetSelection?>> _prepareAsync;
    private readonly Action<PreparedPetSelection> _applySelection;
    private readonly Action<AppSettings> _requestSave;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly List<string> _diagnosticCodes = [];
    private bool _disposed;

    internal PetSelectionService(
        IReadOnlyList<CharacterPack> packs,
        AppSettings settings,
        Func<CharacterPack, CancellationToken, Task<PreparedPetSelection?>> prepareAsync,
        Action<PreparedPetSelection> applySelection,
        Action<AppSettings> requestSave)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _prepareAsync = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
        _applySelection = applySelection ?? throw new ArgumentNullException(nameof(applySelection));
        _requestSave = requestSave ?? throw new ArgumentNullException(nameof(requestSave));
        _packs = packs.ToDictionary(pack => (pack.Id, pack.Version));
    }

    public CharacterPack? CurrentPack { get; private set; }
    public string? CurrentPetId => CurrentPack?.Id;
    public string? CurrentVersion => CurrentPack?.Version;
    public IReadOnlyList<CharacterPack> AvailablePacks => _packs.Values
        .OrderBy(pack => pack.Id, StringComparer.Ordinal)
        .ThenBy(pack => pack.Version, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<string> DiagnosticCodes
    {
        get
        {
            lock (_diagnosticCodes)
                return _diagnosticCodes.ToArray();
        }
    }

    public event EventHandler<PetSelectionChangedEventArgs>? SelectionChanged;

    public Task<PetSelectionResult> InitializeAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(async token =>
        {
            CharacterPack? configured = null;
            if (_settings.SelectedPackVersions.TryGetValue(_settings.SelectedPetId, out var configuredVersion))
                _packs.TryGetValue((_settings.SelectedPetId, configuredVersion), out configured);

            if (configured is not null)
            {
                var configuredResult = await SelectCoreAsync(configured, token, updateSettings: true);
                if (configuredResult.Succeeded)
                    return configuredResult;
                if (configured.Id == "legacy")
                {
                    AddDiagnostic("legacy_unavailable");
                    return configuredResult;
                }
            }

            var legacy = ResolveBundledLegacy();
            if (legacy is null)
            {
                AddDiagnostic("legacy_unavailable");
                return PetSelectionResult.Failed("legacy_unavailable");
            }

            var result = await SelectCoreAsync(legacy, token, updateSettings: true);
            if (!result.Succeeded)
                AddDiagnostic("legacy_unavailable");
            return result;
        }, cancellationToken);

    public Task<PetSelectionResult> SelectAsync(
        string petId,
        string version,
        CancellationToken cancellationToken) =>
        SelectAsync(petId, version, updateSettings: true, cancellationToken);

    internal Task<PetSelectionResult> SelectAsync(
        string petId,
        string version,
        bool updateSettings,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(petId) || string.IsNullOrWhiteSpace(version) ||
                !_packs.TryGetValue((petId, version), out var candidate))
            {
                AddDiagnostic("selection_unknown");
                return PetSelectionResult.Failed("selection_unknown");
            }

            return await SelectCoreAsync(candidate, token, updateSettings);
        }, cancellationToken);

    public Task<PetSelectionResult> SelectByIdAsync(
        string petId,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(petId))
                return PetSelectionResult.Failed("selection_unknown");

            var matches = _packs.Values
                .Where(pack => pack.Id.Equals(petId, StringComparison.Ordinal))
                .OrderBy(pack => pack.Version, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
            {
                AddDiagnostic("selection_unknown");
                return PetSelectionResult.Failed("selection_unknown");
            }

            CharacterPack candidate;
            if (matches.Length == 1)
            {
                candidate = matches[0];
            }
            else if (_settings.SelectedPackVersions.TryGetValue(petId, out var configuredVersion) &&
                matches.SingleOrDefault(pack => pack.Version.Equals(configuredVersion, StringComparison.Ordinal)) is { } configured)
            {
                candidate = configured;
            }
            else
            {
                AddDiagnostic("selection_ambiguous");
                return PetSelectionResult.Failed("selection_ambiguous");
            }

            return await SelectCoreAsync(candidate, token, updateSettings: true);
        }, cancellationToken);

    public async Task RefreshAvailablePacksAsync(
        IReadOnlyList<CharacterPack> packs,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packs);
        var replacement = packs.ToDictionary(pack => (pack.Id, pack.Version));
        await _selectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _packs.Clear();
            foreach (var entry in replacement)
                _packs.Add(entry.Key, entry.Value);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private async Task<PetSelectionResult> SelectCoreAsync(
        CharacterPack candidate,
        CancellationToken cancellationToken,
        bool updateSettings)
    {
        if (CurrentPack is { } current &&
            current.Id.Equals(candidate.Id, StringComparison.Ordinal) &&
            current.Version.Equals(candidate.Version, StringComparison.Ordinal))
        {
            return PetSelectionResult.Unchanged();
        }

        PreparedPetSelection? prepared;
        try
        {
            prepared = await _prepareAsync(candidate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            prepared = null;
        }

        if (prepared is null)
        {
            AddDiagnostic("selection_prepare_failed");
            return PetSelectionResult.Failed("selection_prepare_failed");
        }

        using (prepared)
        {
            try
            {
                _applySelection(prepared);
            }
            catch
            {
                AddDiagnostic("selection_apply_failed");
                return PetSelectionResult.Failed("selection_apply_failed");
            }

            CurrentPack = candidate;
            if (updateSettings)
            {
                var settingsChanged =
                    !_settings.SelectedPetId.Equals(candidate.Id, StringComparison.Ordinal) ||
                    !_settings.SelectedPackVersions.TryGetValue(candidate.Id, out var selectedVersion) ||
                    !selectedVersion.Equals(candidate.Version, StringComparison.Ordinal);
                _settings.SelectedPetId = candidate.Id;
                _settings.SelectedPackVersions[candidate.Id] = candidate.Version;
                if (settingsChanged)
                    _requestSave(_settings);
            }

            RaiseSelectionChanged(candidate);
            return PetSelectionResult.Selected();
        }
    }

    private CharacterPack? ResolveBundledLegacy()
    {
        var matches = _packs.Values
            .Where(pack => pack.Source == CharacterPackSource.Bundled && pack.Id == "legacy")
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task<PetSelectionResult> RunSerializedAsync(
        Func<CancellationToken, Task<PetSelectionResult>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _selectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await action(cancellationToken);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private void RaiseSelectionChanged(CharacterPack pack)
    {
        var handlers = SelectionChanged;
        if (handlers is null)
            return;

        var args = new PetSelectionChangedEventArgs(pack);
        foreach (EventHandler<PetSelectionChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                AddDiagnostic("selection_listener_failed");
            }
        }
    }

    private void AddDiagnostic(string code)
    {
        lock (_diagnosticCodes)
        {
            if (_diagnosticCodes.Count < MaximumDiagnostics &&
                !_diagnosticCodes.Contains(code, StringComparer.Ordinal))
            {
                _diagnosticCodes.Add(code);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _selectionGate.Dispose();
    }

    internal static Task<PreparedPetSelection?> PrepareAsync(
        CharacterPack pack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!pack.Clips.TryGetValue("idle", out var idle) || !idle.IsAvailable || !File.Exists(idle.AssetPath))
            return Task.FromResult<PreparedPetSelection?>(null);

        try
        {
            BitmapImage image;
            using (var stream = new FileStream(idle.AssetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
            }
            var idleFrame = PrepareIdleFrame(image, idle);

            var usesFallback = true;
            DrawingIcon icon;
            if (pack.TrayIconPath is not null)
            {
                try
                {
                    using var stream = new FileStream(pack.TrayIconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var source = new DrawingIcon(stream);
                    icon = (DrawingIcon)source.Clone();
                    usesFallback = false;
                }
                catch
                {
                    icon = TrayService.LoadFallbackIcon();
                }
            }
            else
            {
                icon = TrayService.LoadFallbackIcon();
            }

            return Task.FromResult<PreparedPetSelection?>(
                new PreparedPetSelection(pack, idleFrame, icon, icon, usesFallback));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return Task.FromResult<PreparedPetSelection?>(null);
        }
    }

    internal static BitmapSource PrepareIdleFrame(BitmapSource decodedImage, CharacterClip idleClip)
    {
        ArgumentNullException.ThrowIfNull(decodedImage);
        ArgumentNullException.ThrowIfNull(idleClip);
        if (idleClip.Format.Equals("png", StringComparison.Ordinal))
            return decodedImage;

        var width = idleClip.FrameWidth ??
            throw new InvalidOperationException("Validated idle sheet width is missing.");
        var height = idleClip.FrameHeight ??
            throw new InvalidOperationException("Validated idle sheet height is missing.");
        var frame = new CroppedBitmap(decodedImage, new System.Windows.Int32Rect(0, 0, width, height));
        frame.Freeze();
        return frame;
    }

}
