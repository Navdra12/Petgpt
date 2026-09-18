using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Shell;
using Xunit;

namespace PetGPT.Tests;

public sealed class PetSelectionTests
{
    [Fact]
    public async Task Startup_SelectsExactConfiguredIdAndVersion()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var selected = Pack("alpha", "2.0.0");
        var settings = Settings("alpha", ("alpha", "2.0.0"));
        using var harness = new SelectionHarness([legacy, selected], settings);

        var result = await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Same(selected, harness.Service.CurrentPack);
        Assert.Equal(["alpha@2.0.0"], harness.Applied);
        Assert.Equal(0, harness.SaveCount);
    }

    [Fact]
    public async Task Startup_MissingSelectedPackFallsBackToLegacyAndPersistsCorrection()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var settings = Settings("missing", ("missing", "4.0.0"));
        using var harness = new SelectionHarness([legacy], settings);

        var result = await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Equal("legacy", settings.SelectedPetId);
        Assert.Equal("1.0.0", settings.SelectedPackVersions["legacy"]);
        Assert.Equal(1, harness.SaveCount);
    }

    [Fact]
    public async Task Startup_MissingConfiguredVersionDoesNotChooseAnotherVersion()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var alternate = Pack("alpha", "3.0.0");
        var settings = Settings("alpha", ("alpha", "2.0.0"));
        using var harness = new SelectionHarness([legacy, alternate], settings);

        await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.DoesNotContain("alpha@3.0.0", harness.Applied);
    }

    [Fact]
    public async Task Startup_LegacyWithoutStoredVersionBootstrapsExactBundledVersion()
    {
        var legacy = Pack("legacy", "1.2.3", CharacterPackSource.Bundled);
        var settings = Settings("legacy");
        using var harness = new SelectionHarness([legacy], settings);

        await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Equal("1.2.3", settings.SelectedPackVersions["legacy"]);
        Assert.Equal(1, harness.SaveCount);
    }

    [Fact]
    public async Task Startup_ConfiguredPackPreparationFailureFallsBackToLegacy()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var broken = Pack("broken", "1.0.0");
        var settings = Settings("broken", ("broken", "1.0.0"));
        using var harness = new SelectionHarness([legacy, broken], settings)
        {
            PreparationFailure = broken
        };

        var result = await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Equal("legacy", settings.SelectedPetId);
        Assert.Equal("1.0.0", settings.SelectedPackVersions["legacy"]);
        Assert.Equal(["legacy@1.0.0"], harness.Applied);
    }

    [Fact]
    public async Task Startup_UnavailableLegacyKeepsEmergencyPlaceholderState()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var settings = Settings("legacy");
        using var harness = new SelectionHarness([legacy], settings)
        {
            PreparationFailure = legacy
        };

        var result = await harness.Service.InitializeAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(harness.Service.CurrentPack);
        Assert.Equal("legacy", settings.SelectedPetId);
        Assert.Empty(settings.SelectedPackVersions);
        Assert.Contains("legacy_unavailable", harness.Service.DiagnosticCodes);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "pet-placeholder.png")));
    }

    [Fact]
    public async Task SuccessfulSelectionCommitsSettingsAndEmitsExactlyOnce()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var alpha = Pack("alpha", "2.0.0");
        var settings = Settings("legacy", ("legacy", "1.0.0"));
        using var harness = new SelectionHarness([legacy, alpha], settings);
        await harness.Service.InitializeAsync(CancellationToken.None);
        var events = new List<CharacterPack>();
        harness.Service.SelectionChanged += (_, args) => events.Add(args.Pack);

        var result = await harness.Service.SelectAsync("alpha", "2.0.0", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Same(alpha, harness.Service.CurrentPack);
        Assert.Equal("alpha", settings.SelectedPetId);
        Assert.Equal("2.0.0", settings.SelectedPackVersions["alpha"]);
        Assert.Equal(1, harness.SaveCount);
        Assert.Equal([alpha], events);
    }

    [Fact]
    public async Task SameIdDifferentVersionIsARealTransaction()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var first = Pack("alpha", "1.0.0");
        var second = Pack("alpha", "2.0.0");
        var settings = Settings("alpha", ("alpha", "1.0.0"));
        using var harness = new SelectionHarness([legacy, first, second], settings);
        await harness.Service.InitializeAsync(CancellationToken.None);
        var events = 0;
        harness.Service.SelectionChanged += (_, _) => events++;

        var result = await harness.Service.SelectAsync("alpha", "2.0.0", CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Same(second, harness.Service.CurrentPack);
        Assert.Equal("2.0.0", settings.SelectedPackVersions["alpha"]);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task SelectingCurrentExactVersionDoesNotReapplySaveOrEmit()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var settings = Settings("legacy", ("legacy", "1.0.0"));
        using var harness = new SelectionHarness([legacy], settings);
        await harness.Service.InitializeAsync(CancellationToken.None);
        var events = 0;
        harness.Service.SelectionChanged += (_, _) => events++;

        var result = await harness.Service.SelectAsync("legacy", "1.0.0", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal(["legacy@1.0.0"], harness.Applied);
        Assert.Equal(0, harness.SaveCount);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task PreparationFailureLeavesPreviousSelectionAndSettingsUnchanged()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var broken = Pack("broken", "1.0.0");
        var settings = Settings("legacy", ("legacy", "1.0.0"));
        using var harness = new SelectionHarness([legacy, broken], settings)
        {
            PreparationFailure = broken
        };
        await harness.Service.InitializeAsync(CancellationToken.None);
        var events = 0;
        harness.Service.SelectionChanged += (_, _) => events++;

        var result = await harness.Service.SelectAsync("broken", "1.0.0", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Equal("legacy", settings.SelectedPetId);
        Assert.False(settings.SelectedPackVersions.ContainsKey("broken"));
        Assert.Equal(["legacy@1.0.0"], harness.Applied);
        Assert.Equal(0, harness.SaveCount);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task PresentationApplyFailureLeavesPreviousSelectionAndSettingsUnchanged()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var broken = Pack("broken", "1.0.0");
        var settings = Settings("legacy", ("legacy", "1.0.0"));
        using var harness = new SelectionHarness([legacy, broken], settings);
        await harness.Service.InitializeAsync(CancellationToken.None);
        harness.ApplyFailure = broken;

        var result = await harness.Service.SelectAsync("broken", "1.0.0", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Equal("legacy", settings.SelectedPetId);
        Assert.False(settings.SelectedPackVersions.ContainsKey("broken"));
        Assert.Equal(0, harness.SaveCount);
        Assert.True(harness.LastPreparedResource?.IsDisposed);
    }

    [Fact]
    public async Task RuntimePreparation_MissingRequiredIdleReturnsFailure()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var pack = Pack("alpha", "1.0.0", root: root);

            var prepared = await PetSelectionService.PrepareAsync(pack, CancellationToken.None);

            Assert.Null(prepared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimePreparation_UndecodableRequiredIdleReturnsFailure()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var idle = Path.Combine(root, "assets", "idle.png");
            Directory.CreateDirectory(Path.GetDirectoryName(idle)!);
            await File.WriteAllTextAsync(idle, "not a png");
            var pack = Pack("alpha", "1.0.0", root: root);

            var prepared = await PetSelectionService.PrepareAsync(pack, CancellationToken.None);

            Assert.Null(prepared);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimePreparation_LoadsIdleEagerlyAndFallsBackFromMissingOptionalIcon()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var idle = Path.Combine(root, "assets", "idle.png");
            Directory.CreateDirectory(Path.GetDirectoryName(idle)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Pets", "legacy", "assets", "idle.png"), idle);
            var pack = Pack(
                "alpha",
                "1.0.0",
                root: root,
                trayIconPath: Path.Combine(root, "assets", "missing.ico"));

            using var prepared = await PetSelectionService.PrepareAsync(pack, CancellationToken.None);

            Assert.NotNull(prepared);
            Assert.True(prepared.IdleImage.IsFrozen);
            Assert.True(prepared.UsesFallbackTrayIcon);
            using var exclusive = new FileStream(idle, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown", "1.0.0")]
    [InlineData("alpha", "9.0.0")]
    public async Task UnknownExactSelectionIsRejected(string id, string version)
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var alpha = Pack("alpha", "1.0.0");
        using var harness = new SelectionHarness(
            [legacy, alpha],
            Settings("legacy", ("legacy", "1.0.0")));
        await harness.Service.InitializeAsync(CancellationToken.None);

        var result = await harness.Service.SelectAsync(id, version, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Same(legacy, harness.Service.CurrentPack);
        Assert.Single(harness.Applied);
    }

    [Fact]
    public async Task ConcurrentSelectionsAreSerializedWithoutInterleavedCommits()
    {
        var legacy = Pack("legacy", "1.0.0", CharacterPackSource.Bundled);
        var alpha = Pack("alpha", "1.0.0");
        var beta = Pack("beta", "1.0.0");
        var settings = Settings("legacy", ("legacy", "1.0.0"));
        using var harness = new SelectionHarness([legacy, alpha, beta], settings);
        await harness.Service.InitializeAsync(CancellationToken.None);
        harness.BlockPreparationFor = alpha;

        var first = harness.Service.SelectAsync("alpha", "1.0.0", CancellationToken.None);
        await harness.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = harness.Service.SelectAsync("beta", "1.0.0", CancellationToken.None);
        Assert.False(second.IsCompleted);
        harness.ReleasePreparation.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["legacy@1.0.0", "alpha@1.0.0", "beta@1.0.0"], harness.Applied);
        Assert.Same(beta, harness.Service.CurrentPack);
    }

    private static AppSettings Settings(string selectedId, params (string Id, string Version)[] versions)
    {
        var settings = new AppSettings { SelectedPetId = selectedId };
        foreach (var (id, version) in versions)
            settings.SelectedPackVersions[id] = version;
        return settings;
    }

    private static CharacterPack Pack(
        string id,
        string version,
        CharacterPackSource source = CharacterPackSource.Installed,
        string? root = null,
        string? trayIconPath = null,
        CharacterPresentation? presentation = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "PetGPT-selection-tests", id, version);
        var idlePath = Path.Combine(root, "assets", "idle.png");
        return new CharacterPack(
            root,
            source,
            id,
            id,
            version,
            "2.0.0",
            null,
            presentation ?? new CharacterPresentation(150, 150, 0.5, 1),
            new Dictionary<string, CharacterClip>(StringComparer.Ordinal)
            {
                ["idle"] = new("idle", "png", idlePath, "hold", true, 1, 1, null, null, null, null, null)
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, CharacterReaction>(StringComparer.Ordinal),
            null,
            trayIconPath,
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PetGPT-selection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SelectionHarness : IDisposable
    {
        private readonly List<PreparedPetSelection> _prepared = [];

        public SelectionHarness(IReadOnlyList<CharacterPack> packs, AppSettings settings)
        {
            Service = new PetSelectionService(
                packs,
                settings,
                PrepareAsync,
                Apply,
                _ => SaveCount++);
        }

        public PetSelectionService Service { get; }
        public CharacterPack? PreparationFailure { get; set; }
        public CharacterPack? ApplyFailure { get; set; }
        public CharacterPack? BlockPreparationFor { get; set; }
        public TaskCompletionSource PreparationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Applied { get; } = [];
        public int SaveCount { get; private set; }
        public TrackedResource? LastPreparedResource { get; private set; }

        private async Task<PreparedPetSelection?> PrepareAsync(CharacterPack pack, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(pack, BlockPreparationFor))
            {
                PreparationEntered.TrySetResult();
                await ReleasePreparation.Task.WaitAsync(cancellationToken);
            }

            if (ReferenceEquals(pack, PreparationFailure))
                return null;

            var pixels = new byte[4];
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            image.Freeze();
            var icon = (Icon)SystemIcons.Application.Clone();
            LastPreparedResource = new TrackedResource(icon);
            var prepared = new PreparedPetSelection(pack, image, icon, LastPreparedResource, usesFallbackTrayIcon: true);
            _prepared.Add(prepared);
            return prepared;
        }

        private void Apply(PreparedPetSelection prepared)
        {
            if (ReferenceEquals(prepared.Pack, ApplyFailure))
                throw new InvalidOperationException("synthetic apply failure");
            Applied.Add($"{prepared.Pack.Id}@{prepared.Pack.Version}");
        }

        public void Dispose()
        {
            Service.Dispose();
            foreach (var prepared in _prepared)
                prepared.Dispose();
        }
    }

    private sealed class TrackedResource(IDisposable inner) : IDisposable
    {
        private IDisposable? _inner = inner;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _inner, null)?.Dispose();
            IsDisposed = true;
        }
    }
}
