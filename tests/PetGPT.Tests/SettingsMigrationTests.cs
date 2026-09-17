using System.Text;
using System.Text.Json;
using PetGPT.Models;
using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void Load_UsesSafeDefaults_WhenNoSettingsExist()
    {
        using var folder = new TemporaryFolder();
        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Equal(2, result.Settings.SchemaVersion);
        Assert.Equal("legacy", result.Settings.SelectedPetId);
        Assert.Equal(500, result.Settings.ChatWindow.WidthDip);
        Assert.Equal(650, result.Settings.ChatWindow.HeightDip);
        Assert.True(result.Settings.CompactMode);
        Assert.False(result.Settings.Roleplay.Enabled);
        Assert.False(result.Settings.ReactionsEnabled);
    }

    [Fact]
    public void Load_MigratesLegacyNullablePositions()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.json", """{"PetLeft":null,"PetTop":null,"BubbleWidth":720,"BubbleHeight":480,"CompactMode":false}""");

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.LegacyV0, result.Source);
        Assert.Equal(0, result.SourceVersion);
        Assert.Null(result.Settings.PetPlacement.XWithinWorkAreaDip);
        Assert.Null(result.Settings.PetPlacement.YWithinWorkAreaDip);
        Assert.Equal(720, result.Settings.ChatWindow.WidthDip);
        Assert.Equal(480, result.Settings.ChatWindow.HeightDip);
        Assert.False(result.Settings.CompactMode);
    }

    [Fact]
    public void Load_MigratesLegacyMissingBubbleSizeToDefaults()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.json", """{"PetLeft":20,"PetTop":30}""");

        var result = CreateService(folder).Load();

        Assert.Equal(500, result.Settings.ChatWindow.WidthDip);
        Assert.Equal(650, result.Settings.ChatWindow.HeightDip);
        Assert.Equal(20, result.Settings.PetPlacement.XWithinWorkAreaDip);
        Assert.Equal(30, result.Settings.PetPlacement.YWithinWorkAreaDip);
    }

    [Fact]
    public void Load_MigratesLegacyCompactPreference()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.json", """{"CompactMode":false}""");

        Assert.False(CreateService(folder).Load().Settings.CompactMode);
    }

    [Fact]
    public void Load_PrefersValidV2OverBackupAndLegacy()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", ValidV2("v2"));
        folder.Write("settings.v2.last-good.json", ValidV2("backup"));
        folder.Write("settings.json", """{"CompactMode":false}""");

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.V2, result.Source);
        Assert.Equal("v2", result.Settings.SelectedPetId);
    }

    [Fact]
    public void Load_UsesLastGoodBackup_WhenV2IsCorrupt()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", "{not-json");
        folder.Write("settings.v2.last-good.json", ValidV2("backup"));

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.LastGoodBackup, result.Source);
        Assert.Equal("backup", result.Settings.SelectedPetId);
        Assert.Contains("v2_corrupt", result.DiagnosticCodes);
        Assert.Single(Directory.GetFiles(folder.Path, "settings.v2.corrupt.*.json"));
    }

    [Fact]
    public void Load_FallsBackToLegacy_WhenV2AndBackupAreCorrupt()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", "{");
        folder.Write("settings.v2.last-good.json", "[");
        folder.Write("settings.json", """{"CompactMode":false}""");

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.LegacyV0, result.Source);
        Assert.False(result.Settings.CompactMode);
        Assert.Contains("backup_invalid", result.DiagnosticCodes);
    }

    [Fact]
    public async Task Load_FutureSchemaIsReadOnly_AndCannotBeOverwritten()
    {
        using var folder = new TemporaryFolder();
        const string future = """{"SchemaVersion":99,"SelectedPetId":"future"}""";
        folder.Write("settings.v2.json", future);
        var service = CreateService(folder);

        var result = service.Load();
        result.Settings.SelectedPetId = "changed";
        service.RequestSave(result.Settings);
        await service.FlushAsync(CancellationToken.None);

        Assert.True(result.IsReadOnlyRecovery);
        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Equal(99, result.SourceVersion);
        Assert.Contains("future_schema", result.DiagnosticCodes);
        Assert.Equal(future, folder.Read("settings.v2.json"));
    }

    [Theory]
    [InlineData("NaN", "v2_corrupt")]
    [InlineData("Infinity", "v2_corrupt")]
    [InlineData("-Infinity", "v2_corrupt")]
    [InlineData("1e9999", "v2_invalid")]
    public void Load_RejectsNonfiniteNumbers(string value, string expectedDiagnostic)
    {
        using var folder = new TemporaryFolder();
        folder.Write(
            "settings.v2.json",
            ValidV2().Replace("\"WidthDip\":500", $"\"WidthDip\":{value}"));

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Contains(expectedDiagnostic, result.DiagnosticCodes);
    }

    [Fact]
    public void Load_RejectsImplausiblyLargeDimensions()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", ValidV2().Replace("\"HeightDip\":650", "\"HeightDip\":999999"));

        Assert.Equal(SettingsLoadSource.Defaults, CreateService(folder).Load().Source);
    }

    [Fact]
    public void Load_RejectsDuplicateJsonKeys()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", ValidV2().Replace("\"SchemaVersion\":2", "\"SchemaVersion\":2,\"SchemaVersion\":2"));

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Contains("duplicate_key", result.DiagnosticCodes);
    }

    [Fact]
    public void Load_RejectsOverlongIdentifiers()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", ValidV2(new string('x', 129)));

        Assert.Equal(SettingsLoadSource.Defaults, CreateService(folder).Load().Source);
    }

    [Fact]
    public void Load_RejectsUnknownProperties()
    {
        using var folder = new TemporaryFolder();
        folder.Write(
            "settings.v2.json",
            ValidV2().Replace(
                "\"SuspendHiddenBrowser\":false",
                "\"SuspendHiddenBrowser\":false,\"Unexpected\":true"));

        Assert.Equal(SettingsLoadSource.Defaults, CreateService(folder).Load().Source);
    }

    [Fact]
    public void Load_RejectsOversizedInput()
    {
        using var folder = new TemporaryFolder();
        folder.WriteBytes("settings.v2.json", new byte[(256 * 1024) + 1]);

        var result = CreateService(folder).Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Contains("file_too_large", result.DiagnosticCodes);
    }

    [Fact]
    public async Task Flush_ContainsWriteFailure()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "settings.v2.json"));
        var service = CreateService(folder);

        service.RequestSave(new AppSettings { SelectedPetId = "new" });
        var exception = await Record.ExceptionAsync(() => service.FlushAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Contains("write_failed", service.DiagnosticCodes);
    }

    [Fact]
    public async Task Flush_RetainsSnapshotForRetryAfterTransientWriteFailure()
    {
        using var folder = new TemporaryFolder();
        var blockedPath = folder.File("settings.v2.json");
        Directory.CreateDirectory(blockedPath);
        var service = CreateService(folder);

        service.RequestSave(new AppSettings { SelectedPetId = "retry" });
        await service.FlushAsync(CancellationToken.None);
        Directory.Delete(blockedPath);
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal("retry", CreateService(folder).Load().Settings.SelectedPetId);
    }

    [Fact]
    public async Task Flush_FailedReplacementLeavesPreviousSettingsReadable()
    {
        using var folder = new TemporaryFolder();
        folder.Write("settings.v2.json", ValidV2("before"));
        var service = CreateService(folder);
        Assert.Equal("before", service.Load().Settings.SelectedPetId);

        using (new FileStream(folder.File("settings.v2.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            service.RequestSave(new AppSettings { SelectedPetId = "after" });
            await service.FlushAsync(CancellationToken.None);
        }

        Assert.Equal("before", CreateService(folder).Load().Settings.SelectedPetId);
    }

    [Fact]
    public async Task Flush_SerializesConcurrentRequestsAndPersistsLatestSnapshot()
    {
        using var folder = new TemporaryFolder();
        var service = CreateService(folder);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            service.RequestSave(new AppSettings { SelectedPetId = $"pet-{i:D2}" });
        })));
        service.RequestSave(new AppSettings { SelectedPetId = "final" });
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal("final", CreateService(folder).Load().Settings.SelectedPetId);
    }

    [Fact]
    public async Task RequestSave_DebouncesOrdinaryWrites()
    {
        using var folder = new TemporaryFolder();
        var service = CreateService(folder, TimeSpan.FromMilliseconds(120));

        service.RequestSave(new AppSettings { SelectedPetId = "debounced" });
        await Task.Delay(30);
        Assert.False(File.Exists(folder.File("settings.v2.json")));

        await Task.Delay(220);
        Assert.True(File.Exists(folder.File("settings.v2.json")));
    }

    [Fact]
    public async Task Flush_WritesPendingSnapshotImmediately()
    {
        using var folder = new TemporaryFolder();
        var service = CreateService(folder, TimeSpan.FromMinutes(1));

        service.RequestSave(new AppSettings { SelectedPetId = "flushed" });
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal("flushed", CreateService(folder).Load().Settings.SelectedPetId);
    }

    [Fact]
    public async Task Flush_RotatesPreviousActiveSnapshotIntoLastGoodBackup()
    {
        using var folder = new TemporaryFolder();
        var service = CreateService(folder, TimeSpan.FromMinutes(1));

        service.RequestSave(new AppSettings { SelectedPetId = "first" });
        await service.FlushAsync(CancellationToken.None);
        service.RequestSave(new AppSettings { SelectedPetId = "second" });
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal("second", CreateService(folder).Load().Settings.SelectedPetId);
        using var backup = JsonDocument.Parse(folder.Read("settings.v2.last-good.json"));
        Assert.Equal("first", backup.RootElement.GetProperty("SelectedPetId").GetString());
    }

    [Fact]
    public void Load_PrunesCorruptRecoveryFilesToBoundedCount()
    {
        using var folder = new TemporaryFolder();

        for (var index = 0; index < 5; index++)
        {
            folder.Write("settings.v2.json", $"{{broken-{index}");
            _ = CreateService(folder).Load();
        }

        Assert.Equal(3, Directory.GetFiles(folder.Path, "settings.v2.corrupt.*.json").Length);
    }

    [Fact]
    public async Task RequestSave_CopiesMutableSnapshot()
    {
        using var folder = new TemporaryFolder();
        var service = CreateService(folder, TimeSpan.FromMinutes(1));
        var settings = new AppSettings { SelectedPetId = "queued" };

        service.RequestSave(settings);
        settings.SelectedPetId = "mutated-after-queue";
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal("queued", CreateService(folder).Load().Settings.SelectedPetId);
    }

    [Fact]
    public async Task Save_LeavesLegacyFileUntouched()
    {
        using var folder = new TemporaryFolder();
        const string legacy = """{"PetLeft":7,"PetTop":9,"CompactMode":false}""";
        folder.Write("settings.json", legacy);
        var service = CreateService(folder);
        var migrated = service.Load().Settings;

        service.RequestSave(migrated);
        await service.FlushAsync(CancellationToken.None);

        Assert.Equal(legacy, folder.Read("settings.json"));
        Assert.True(File.Exists(folder.File("settings.v2.json")));
    }

    private static SettingsService CreateService(TemporaryFolder folder, TimeSpan? delay = null) =>
        new(folder.Path, delay ?? TimeSpan.FromMilliseconds(500));

    private static string ValidV2(string petId = "legacy") => $$"""
        {
          "SchemaVersion":2,
          "SelectedPetId":"{{petId}}",
          "SelectedPackVersions":{},
          "ChatHomeUrl":null,
          "PetPlacement":{"MonitorId":null,"XWithinWorkAreaDip":null,"YWithinWorkAreaDip":null},
          "ChatWindow":{"PlacementMode":"FollowPet","WidthDip":500,"HeightDip":650,"MonitorId":null,"XWithinWorkAreaDip":null,"YWithinWorkAreaDip":null},
          "CompactMode":true,
          "ThemesEnabled":true,
          "Roleplay":{"Enabled":false,"ActivationMode":"ReviewThenSend"},
          "ReactionsEnabled":false,
          "ShowControlMarkers":false,
          "PetOptions":{},
          "SuspendHiddenBrowser":false
        }
        """;

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PetGPT.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public string Read(string name) => System.IO.File.ReadAllText(File(name), Encoding.UTF8);

        public void Write(string name, string contents) =>
            System.IO.File.WriteAllText(File(name), contents, new UTF8Encoding(false));

        public void WriteBytes(string name, byte[] contents) => System.IO.File.WriteAllBytes(File(name), contents);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // A failed-write test can intentionally leave a transient handle behind.
            }
        }
    }
}
