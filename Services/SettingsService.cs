using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PetGPT.Characters;
using PetGPT.Models;

namespace PetGPT.Services;

public sealed class SettingsService
{
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumDiagnostics = 16;
    private const int MaximumRecoveryFiles = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<string> _diagnosticCodes = [];
    private readonly string _settingsDirectory;
    private readonly string _legacyPath;
    private readonly string _v2Path;
    private readonly string _backupPath;
    private readonly TimeSpan _debounceDelay;
    private AppSettings? _pendingSnapshot;
    private CancellationTokenSource? _debounceCancellation;
    private bool _readOnlyRecovery;

    public SettingsService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PetGPT"),
            TimeSpan.FromMilliseconds(500))
    {
    }

    internal SettingsService(string settingsDirectory, TimeSpan debounceDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounceDelay, TimeSpan.Zero);

        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _legacyPath = Path.Combine(_settingsDirectory, "settings.json");
        _v2Path = Path.Combine(_settingsDirectory, "settings.v2.json");
        _backupPath = Path.Combine(_settingsDirectory, "settings.v2.last-good.json");
        _debounceDelay = debounceDelay;
    }

    public IReadOnlyList<string> DiagnosticCodes
    {
        get
        {
            lock (_stateGate)
                return _diagnosticCodes.ToArray();
        }
    }

    public SettingsLoadResult Load()
    {
        lock (_stateGate)
        {
            _diagnosticCodes.Clear();
            _readOnlyRecovery = false;
        }

        if (File.Exists(_v2Path))
        {
            try
            {
                return Result(ReadV2(_v2Path), SettingsLoadSource.V2, 2);
            }
            catch (FutureSchemaException exception)
            {
                AddDiagnostic("future_schema");
                lock (_stateGate)
                    _readOnlyRecovery = true;

                return Result(new AppSettings(), SettingsLoadSource.Defaults, exception.SchemaVersion);
            }
            catch (SettingsFormatException exception)
            {
                AddDiagnostic(exception.Code);
                PreserveInvalidV2();
            }
            catch
            {
                AddDiagnostic("v2_read_failed");
                PreserveInvalidV2();
            }
        }

        if (File.Exists(_backupPath))
        {
            try
            {
                return Result(ReadV2(_backupPath), SettingsLoadSource.LastGoodBackup, 2);
            }
            catch
            {
                AddDiagnostic("backup_invalid");
            }
        }

        if (File.Exists(_legacyPath))
        {
            try
            {
                return Result(ReadLegacy(), SettingsLoadSource.LegacyV0, 0);
            }
            catch
            {
                AddDiagnostic("legacy_invalid");
            }
        }

        return Result(new AppSettings(), SettingsLoadSource.Defaults, null);
    }

    public void RequestSave(AppSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AppSettings copy;
        try
        {
            copy = snapshot.Copy();
            Validate(copy);
        }
        catch
        {
            AddDiagnostic("snapshot_invalid");
            return;
        }

        CancellationToken cancellationToken;
        lock (_stateGate)
        {
            if (_readOnlyRecovery)
            {
                AddDiagnosticLocked("read_only");
                return;
            }

            _pendingSnapshot = copy;
            _debounceCancellation?.Cancel();
            _debounceCancellation?.Dispose();
            _debounceCancellation = new CancellationTokenSource();
            cancellationToken = _debounceCancellation.Token;
        }

        _ = DebounceAndWriteAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? debounceCancellation;
        lock (_stateGate)
        {
            debounceCancellation = _debounceCancellation;
            _debounceCancellation = null;
        }

        debounceCancellation?.Cancel();
        debounceCancellation?.Dispose();
        await WritePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private SettingsLoadResult Result(AppSettings settings, SettingsLoadSource source, int? version)
    {
        bool isReadOnly;
        IReadOnlyList<string> diagnostics;
        lock (_stateGate)
        {
            isReadOnly = _readOnlyRecovery;
            diagnostics = _diagnosticCodes.ToArray();
        }

        return new SettingsLoadResult(settings.Copy(), source, version, isReadOnly, diagnostics);
    }

    private AppSettings ReadV2(string path)
    {
        var bytes = ReadBounded(path);
        using var document = ParseAndRejectDuplicates(bytes);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out var schemaProperty) ||
            schemaProperty.ValueKind != JsonValueKind.Number ||
            !schemaProperty.TryGetInt32(out var schemaVersion))
        {
            throw new SettingsFormatException("v2_invalid");
        }

        if (schemaVersion > 2)
            throw new FutureSchemaException(schemaVersion);

        if (schemaVersion != 2)
            throw new SettingsFormatException("v2_invalid");

        try
        {
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions)
                ?? throw new SettingsFormatException("v2_invalid");
            Validate(settings);
            return settings;
        }
        catch (SettingsFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException)
        {
            throw new SettingsFormatException("v2_invalid", exception);
        }
    }

    private AppSettings ReadLegacy()
    {
        var bytes = ReadBounded(_legacyPath);
        using var document = ParseAndRejectDuplicates(bytes);

        LegacySettings legacy;
        try
        {
            legacy = document.RootElement.Deserialize<LegacySettings>(JsonOptions) ?? new LegacySettings();
        }
        catch (JsonException exception)
        {
            throw new SettingsFormatException("legacy_invalid", exception);
        }

        var settings = new AppSettings
        {
            PetPlacement = new PetPlacementSettings
            {
                XWithinWorkAreaDip = legacy.PetLeft,
                YWithinWorkAreaDip = legacy.PetTop
            },
            ChatWindow = new ChatWindowSettings
            {
                WidthDip = legacy.BubbleWidth ?? 500,
                HeightDip = legacy.BubbleHeight ?? 650
            },
            CompactMode = legacy.CompactMode ?? true
        };

        Validate(settings);
        return settings;
    }

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
            throw new SettingsFormatException("file_too_large");

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumFileBytes)
            throw new SettingsFormatException("file_too_large");

        return bytes;
    }

    private static JsonDocument ParseAndRejectDuplicates(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetString(bytes);
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new SettingsFormatException("v2_corrupt", exception);
        }

        try
        {
            RejectDuplicateKeys(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new SettingsFormatException("duplicate_key");

                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateKeys(item);
        }
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 2 ||
            !IsBoundedIdentifier(settings.SelectedPetId) ||
            settings.SelectedPackVersions is null ||
            settings.SelectedPackVersions.Count > 128 ||
            settings.PetPlacement is null ||
            settings.ChatWindow is null ||
            settings.Roleplay is null ||
            settings.PetOptions is null ||
            settings.PetOptions.Count > 128)
        {
            throw new SettingsFormatException("v2_invalid");
        }

        foreach (var entry in settings.SelectedPackVersions)
        {
            if (!IsBoundedIdentifier(entry.Key) || !SemanticVersion.TryParse(entry.Value, out _))
                throw new SettingsFormatException("v2_invalid");
        }

        if (settings.ChatHomeUrl is { } home &&
            (home.Length > 2048 || !Uri.TryCreate(home, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(uri.UserInfo) ||
             !(uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase))))
        {
            throw new SettingsFormatException("v2_invalid");
        }

        ValidateMonitorId(settings.PetPlacement.MonitorId);
        ValidateCoordinate(settings.PetPlacement.XWithinWorkAreaDip);
        ValidateCoordinate(settings.PetPlacement.YWithinWorkAreaDip);

        if (settings.ChatWindow.PlacementMode is not ("FollowPet" or "Free" or "Remembered") ||
            !IsFiniteInRange(settings.ChatWindow.WidthDip, 100, 10_000) ||
            !IsFiniteInRange(settings.ChatWindow.HeightDip, 100, 10_000))
        {
            throw new SettingsFormatException("v2_invalid");
        }

        ValidateMonitorId(settings.ChatWindow.MonitorId);
        ValidateCoordinate(settings.ChatWindow.XWithinWorkAreaDip);
        ValidateCoordinate(settings.ChatWindow.YWithinWorkAreaDip);

        if (settings.Roleplay.ActivationMode != "ReviewThenSend")
            throw new SettingsFormatException("v2_invalid");

        foreach (var entry in settings.PetOptions)
        {
            if (!IsBoundedIdentifier(entry.Key) || entry.Value is null ||
                !IsFiniteInRange(entry.Value.Scale, 0.5, 2.0))
            {
                throw new SettingsFormatException("v2_invalid");
            }
        }
    }

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool IsFiniteInRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static void ValidateCoordinate(double? value)
    {
        if (value.HasValue && !IsFiniteInRange(value.Value, -1_000_000, 1_000_000))
            throw new SettingsFormatException("v2_invalid");
    }

    private static void ValidateMonitorId(string? monitorId)
    {
        if (monitorId is { Length: > 512 })
            throw new SettingsFormatException("v2_invalid");
    }

    private async Task DebounceAndWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            await WritePendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                AppSettings? snapshot;
                lock (_stateGate)
                {
                    if (_readOnlyRecovery)
                        return;

                    snapshot = _pendingSnapshot;
                    _pendingSnapshot = null;
                }

                if (snapshot is null)
                    return;

                try
                {
                    await WriteAtomicAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_stateGate)
                        _pendingSnapshot ??= snapshot;
                    throw;
                }
                catch
                {
                    AddDiagnostic("write_failed");
                    lock (_stateGate)
                        _pendingSnapshot ??= snapshot;
                    return;
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteAtomicAsync(AppSettings snapshot, CancellationToken cancellationToken)
    {
        Validate(snapshot);
        Directory.CreateDirectory(_settingsDirectory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new SettingsFormatException("file_too_large");

        var temporaryPath = Path.Combine(
            _settingsDirectory,
            $"settings.v2.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_v2Path))
            {
                File.Replace(temporaryPath, _v2Path, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _v2Path);
                File.Copy(_v2Path, _backupPath, overwrite: true);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                AddDiagnostic("temp_cleanup_failed");
            }
        }
    }

    private void PreserveInvalidV2()
    {
        if (!File.Exists(_v2Path))
            return;

        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
            string? recoveryPath = null;
            for (var suffix = 0; suffix < 100; suffix++)
            {
                var candidate = Path.Combine(
                    _settingsDirectory,
                    $"settings.v2.corrupt.{stamp}.{suffix:D2}.json");
                if (!File.Exists(candidate))
                {
                    recoveryPath = candidate;
                    break;
                }
            }

            if (recoveryPath is null)
                throw new IOException("No bounded recovery name is available.");

            File.Move(_v2Path, recoveryPath);
            AddDiagnostic("recovery_saved");
            PruneRecoveryFiles();
        }
        catch
        {
            AddDiagnostic("recovery_failed");
            lock (_stateGate)
                _readOnlyRecovery = true;
        }
    }

    private void PruneRecoveryFiles()
    {
        try
        {
            var recoveryFiles = Directory
                .GetFiles(_settingsDirectory, "settings.v2.corrupt.*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(MaximumRecoveryFiles);

            foreach (var file in recoveryFiles)
                File.Delete(file);
        }
        catch
        {
            AddDiagnostic("recovery_prune_failed");
        }
    }

    private void AddDiagnostic(string code)
    {
        lock (_stateGate)
            AddDiagnosticLocked(code);
    }

    private void AddDiagnosticLocked(string code)
    {
        if (_diagnosticCodes.Count < MaximumDiagnostics && !_diagnosticCodes.Contains(code, StringComparer.Ordinal))
            _diagnosticCodes.Add(code);
    }

    private sealed class LegacySettings
    {
        public double? PetLeft { get; set; }
        public double? PetTop { get; set; }
        public double? BubbleWidth { get; set; }
        public double? BubbleHeight { get; set; }
        public bool? CompactMode { get; set; }
    }

    private sealed class SettingsFormatException : Exception
    {
        public SettingsFormatException(string code, Exception? innerException = null)
            : base(code, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private sealed class FutureSchemaException(int schemaVersion) : Exception
    {
        public int SchemaVersion { get; } = schemaVersion;
    }
}
