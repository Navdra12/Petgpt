using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetGPT.Characters;
using Xunit;

namespace PetGPT.Tests;

public sealed class CharacterPackTests
{
    [Theory]
    [InlineData("1.2.3\n")]
    [InlineData("1.2.3-alpha.1+build\n")]
    public void FinalReview_SemanticVersionRejectsTerminalNewline(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void FinalReview_SemanticVersionPrereleaseCannotMutateComparison()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0-alpha", out var version));
        Assert.True(SemanticVersion.TryParse("1.0.0-beta", out var later));
        var identifiers = Assert.IsAssignableFrom<IList<string>>(version.Prerelease);
        Assert.Throws<NotSupportedException>(() => identifiers[0] = "zeta");
        Assert.True(version.CompareTo(later) < 0);
    }

    [Theory]
    [InlineData("clips", "invalid_clip")]
    [InlineData("reactions", "invalid_reaction")]
    public void FinalReview_RejectsTerminalNewlineIdentifiers(string property, string code)
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, mutate: json =>
        {
            var entries = json[property]!.AsObject();
            entries["extra\n"] = entries.First().Value!.DeepClone();
        });
        AssertInvalid(result, code);
    }

    [Theory]
    [InlineData("Assets/extra.png", null)]
    [InlineData("notes/one.txt", "Notes/two.txt")]
    [InlineData("notes/one.txt", "notes")]
    [InlineData("notes", "notes/one.txt")]
    [InlineData("notes/", "notes")]
    [InlineData("notes", "notes/")]
    [InlineData("notes/", "notes/")]
    public async Task FinalReview_ArchiveRejectsParentAliasesAndTypeConflicts(string first, string? second)
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var archive = tree.ZipPack(pack, "parents.petpack", zip =>
        {
            WriteZipText(zip, first, "first");
            if (second is not null)
                WriteZipText(zip, second, "second");
        });
        var published = false;
        var catalog = tree.CreateCatalog(beforePublish: _ => published = true);
        var result = await catalog.ImportAsync(archive, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("case_collision", result.DiagnosticCodes);
        Assert.False(published);
        Assert.Empty(catalog.LoadInstalled().Packs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(tree.InstalledRoot, ".staging")));
        Assert.Empty(Directory.EnumerateFiles(tree.InstalledRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("overlap")]
    [InlineData("png_mismatch")]
    [InlineData("png_oversized")]
    [InlineData("png_undecodable")]
    [InlineData("dib_mismatch")]
    [InlineData("dib_oversized")]
    [InlineData("dib_truncated")]
    [InlineData("dib_unsupported")]
    [InlineData("second_empty")]
    public void FinalReview_IconRejectsInvalidPayloads(string scenario)
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack(mutate: json => json["trayIcon"] = "assets/tray.ico");
        byte[] payload;
        if (scenario.StartsWith("dib", StringComparison.Ordinal))
        {
            payload = CreateDibPayload(scenario == "dib_mismatch" ? 2 : scenario == "dib_oversized" ? 4097 : 1, 1);
            if (scenario == "dib_truncated")
                payload = payload[..40];
            if (scenario == "dib_unsupported")
                payload[16] = 1; // RLE8 is not a supported ICO payload.
        }
        else
        {
            var png = Path.Combine(tree.Path, "payload.png");
            if (scenario == "png_undecodable")
                TestTree.WriteUndecodablePng(png, 1, 1);
            else
                TestTree.WritePng(png, scenario == "png_mismatch" ? 2 : scenario == "png_oversized" ? 4097 : 1, 1);
            payload = scenario == "empty" ? [] : File.ReadAllBytes(png);
        }
        var icon = CreateIcon(payload, secondEmpty: scenario == "second_empty");
        if (scenario == "overlap")
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(icon.AsSpan(18), 6);
        File.WriteAllBytes(Path.Combine(pack, "assets", "tray.ico"), icon);
        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_tray_icon");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("dib")]
    [InlineData("fallback")]
    public void FinalReview_IconAcceptsValidPayloads(string format)
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack(mutate: json => json["trayIcon"] = "assets/tray.ico");
        var icon = format == "fallback"
            ? File.ReadAllBytes(Path.Combine(RepositoryRoot, "Assets", "petgpt-fallback.ico"))
            : CreateIcon(format == "dib" ? CreateDibPayload(1, 1) : File.ReadAllBytes(Path.Combine(pack, "assets", "idle.png")));
        File.WriteAllBytes(Path.Combine(pack, "assets", "tray.ico"), icon);
        var result = new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed);
        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
    }

    private static byte[] CreateIcon(byte[] payload, bool secondEmpty = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)(secondEmpty ? 2 : 1));
        for (var index = 0; index < (secondEmpty ? 2 : 1); index++)
        {
            writer.Write(new byte[] { 1, 1, 0, 0 });
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(index == 0 ? payload.Length : 0);
            writer.Write(secondEmpty ? 38 : 22);
        }
        writer.Write(payload);
        return stream.ToArray();
    }

    private static byte[] CreateDibPayload(int width, int height)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(new byte[24]);
        writer.Write(new byte[width * height * 4 + ((width + 31) / 32 * 4 * height)]);
        return stream.ToArray();
    }

    [Fact]
    public void ValidateDirectory_AcceptsBundledLegacyPack()
    {
        var result = new PackValidator().ValidateDirectory(
            Path.Combine(RepositoryRoot, "Pets", "legacy"),
            CharacterPackSource.Bundled);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.Equal("legacy", result.Pack!.Id);
        Assert.Equal("2.0.0", result.Pack.MinAppVersion);
        Assert.Null(result.Pack.Persona);
        Assert.Empty(result.Pack.Reactions);
        Assert.Equal(150, result.Pack.Presentation.WidthDip);
        Assert.True(result.Pack.Clips["idle"].IsAvailable);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("alpha_2")]
    [InlineData("z0123456789012345678901234567890")]
    public void ValidateDirectory_AcceptsStableIds(string id)
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, id);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.Equal(id, result.Pack!.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alpha")]
    [InlineData("2alpha")]
    [InlineData("alpha-beta")]
    [InlineData("alpha beta")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void ValidateDirectory_RejectsInvalidIds(string id)
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, id);

        AssertInvalid(result, "invalid_id");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("sleep")]
    [InlineData("wake")]
    public void ValidateDirectory_RejectsReservedIds(string id)
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, id), "reserved_id");
    }

    [Fact]
    public void ValidateDirectory_RejectsLegacyForInstalledPack()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, "legacy"), "reserved_id");
    }

    [Fact]
    public void ValidateDirectory_RejectsUnsupportedSchemaVersion()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json => json["schemaVersion"] = 2), "unsupported_schema");
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-alpha.1+build.5")]
    [InlineData("2.0.0")]
    [InlineData("10.20.30-rc.1")]
    public void SemanticVersion_AcceptsStrictSemVer(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void SemanticVersion_AcceptsArbitrarilyLargeNumericComponents()
    {
        Assert.True(SemanticVersion.TryParse("123456789012345678901234567890.2.3", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+bad space")]
    [InlineData("v1.2.3")]
    public void SemanticVersion_RejectsNonSemVer(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void ValidateDirectory_RejectsUnsupportedMinimumAppVersion()
    {
        using var tree = new TestTree();
        AssertInvalid(
            ValidateSynthetic(tree, mutate: json => json["minAppVersion"] = "2.0.1"),
            "unsupported_app_version");
    }

    [Fact]
    public void ValidateDirectory_RejectsDuplicateJsonProperties()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var manifest = File.ReadAllText(Path.Combine(pack, "pet.json"), Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(pack, "pet.json"),
            manifest.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1"),
            new UTF8Encoding(false));

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "duplicate_property");
    }

    [Fact]
    public void ValidateDirectory_RejectsUnknownTopLevelProperty()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json => json["surprise"] = true), "unknown_property");
    }

    [Fact]
    public void Catalog_RejectsDuplicateBundledPackIds()
    {
        using var tree = new TestTree();
        tree.CreateSyntheticPack(Path.Combine(tree.BundledRoot, "one"), "alpha", "spark");
        tree.CreateSyntheticPack(Path.Combine(tree.BundledRoot, "two"), "alpha", "nod", version: "1.1.0");

        var result = tree.CreateCatalog().LoadInstalled();

        Assert.Contains("duplicate_pack_id", result.DiagnosticCodes);
        Assert.Single(result.Packs, pack => pack.Id == "alpha");
    }

    [Fact]
    public void ValidateDirectory_RejectsParentTraversal()
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, "../outside.png");
    }

    [Theory]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    public void ValidateDirectory_RejectsAbsoluteWindowsPath(string path)
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, path);
    }

    [Fact]
    public void ValidateDirectory_RejectsDriveQualifiedPath()
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, "C:idle.png");
    }

    [Fact]
    public void ValidateDirectory_RejectsUncPath()
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, "\\\\server\\share\\idle.png");
    }

    [Fact]
    public void ValidateDirectory_RejectsUriLookingPath()
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, "https://example.invalid/idle.png");
    }

    [Fact]
    public void ValidateDirectory_RejectsAlternateDataStreamPath()
    {
        using var tree = new TestTree();
        AssertUnsafeManifestPath(tree, "assets/idle.png:payload");
    }

    [Fact]
    public void ValidateDirectory_RejectsCanonicalRootEscape()
    {
        using var tree = new TestTree();
        Directory.CreateDirectory(Path.Combine(tree.Path, "pack-escape"));
        AssertUnsafeManifestPath(tree, "../pack-escape/idle.png");
    }

    [Fact]
    public void ValidateDirectory_RejectsReparsePoints_WhenCreationIsPermitted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var external = Directory.CreateDirectory(Path.Combine(tree.Path, "external"));
        TestTree.WritePng(Path.Combine(external.FullName, "idle.png"), 1, 1);
        var linked = Path.Combine(pack, "linked");
        try
        {
            Directory.CreateSymbolicLink(linked, external.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        TestTree.MutateManifest(pack, json =>
            json["clips"]!["idle"]!["path"] = "linked/idle.png");

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "reparse_point");
    }

    [Fact]
    public async Task ImportAsync_RejectsCaseCollidingArchiveEntries()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var archive = tree.ZipPack(pack, "collision.petpack", zip =>
        {
            WriteZipText(zip, "notes/Readme.md", "one");
            WriteZipText(zip, "notes/readme.md", "two");
        });

        var result = await tree.CreateCatalog().ImportAsync(archive, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("case_collision", result.DiagnosticCodes);
    }

    [Fact]
    public async Task ImportAsync_RejectsZipTraversalBeforeExtraction()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var archive = tree.ZipPack(pack, "traversal.petpack", zip => WriteZipText(zip, "../escaped.txt", "blocked"));

        var result = await tree.CreateCatalog().ImportAsync(archive, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("unsafe_path", result.DiagnosticCodes);
        Assert.False(File.Exists(Path.Combine(tree.Path, "escaped.txt")));
    }

    [Fact]
    public void ValidateDirectory_RejectsTooManyFiles()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        File.WriteAllText(Path.Combine(pack, "one.md"), "1");
        File.WriteAllText(Path.Combine(pack, "two.md"), "2");
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumFiles = 5 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "too_many_files");
    }

    [Fact]
    public async Task ImportAsync_RejectsArchiveOverByteLimit()
    {
        using var tree = new TestTree();
        var archive = Path.Combine(tree.Path, "large.petpack");
        File.WriteAllBytes(archive, Enumerable.Range(0, 1024).Select(i => (byte)(i * 37)).ToArray());
        var limits = PackValidationLimits.Default with { MaximumArchiveBytes = 128 };

        var result = await tree.CreateCatalog(new PackValidator(limits)).ImportAsync(archive, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("archive_too_large", result.DiagnosticCodes);
    }

    [Fact]
    public async Task ImportAsync_RejectsExpandedArchiveOverLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        File.WriteAllText(Path.Combine(pack, "large.md"), new string('x', 4096));
        var archive = tree.ZipPack(pack, "expanded.petpack");
        var limits = PackValidationLimits.Default with { MaximumExpandedBytes = 1024 };

        var result = await tree.CreateCatalog(new PackValidator(limits)).ImportAsync(archive, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("expanded_too_large", result.DiagnosticCodes);
    }

    [Fact]
    public void ValidateDirectory_RejectsImageOverEncodedByteLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var imageBytes = new FileInfo(Path.Combine(pack, "assets", "idle.png")).Length;
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumImageBytes = imageBytes - 1 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "image_too_large");
    }

    [Fact]
    public void ValidateDirectory_RejectsImageOverPixelDimensionLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WritePng(Path.Combine(pack, "assets", "idle.png"), 4097, 1);

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "image_dimensions");
    }

    [Fact]
    public void ValidateDirectory_RejectsDecodedImageBudgetOverflow()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WritePng(Path.Combine(pack, "assets", "wave.png"), 1, 1);
        TestTree.MutateManifest(pack, json =>
            json["clips"]!["wave"] = new JsonObject
            {
                ["format"] = "png",
                ["path"] = "assets/wave.png",
                ["playback"] = "hold"
            });
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumDecodedImageBytes = 7 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "decoded_image_budget");
    }

    [Fact]
    public void ValidateDirectory_RejectsManifestOverByteLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var size = new FileInfo(Path.Combine(pack, "pet.json")).Length;
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumManifestBytes = size - 1 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "manifest_too_large");
    }

    [Fact]
    public void ValidateDirectory_RejectsCombinedPersonaOverByteLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var profile = new FileInfo(Path.Combine(pack, "persona", "profile.json")).Length;
        var voice = new FileInfo(Path.Combine(pack, "persona", "voice.md")).Length;
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumPersonaBytes = profile + voice - 1 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "persona_too_large");
    }

    [Fact]
    public void ValidateDirectory_RejectsThemeOverByteLimit()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack(includeTheme: true);
        var size = new FileInfo(Path.Combine(pack, "theme", "tokens.json")).Length;
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumThemeBytes = size - 1 });

        AssertInvalid(validator.ValidateDirectory(pack, CharacterPackSource.Installed), "theme_too_large");
    }

    [Fact]
    public void ValidateDirectory_RejectsMissingRequiredIdle()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        File.Delete(Path.Combine(pack, "assets", "idle.png"));

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "idle_unavailable");
    }

    [Fact]
    public void ValidateDirectory_RejectsUndecodableRequiredIdle()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        File.WriteAllBytes(Path.Combine(pack, "assets", "idle.png"), [137, 80, 78, 71]);

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "idle_unavailable");
    }

    [Fact]
    public void ValidateDirectory_MarksMissingOptionalClipUnavailableWithWarning()
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, mutate: json =>
            json["clips"]!["wave"] = new JsonObject
            {
                ["format"] = "png",
                ["path"] = "assets/missing.png",
                ["playback"] = "hold"
            });

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.False(result.Pack!.Clips["wave"].IsAvailable);
        Assert.Contains("clip_unavailable", result.WarningCodes);
    }

    [Fact]
    public void ValidateDirectory_CorruptOptionalClipDoesNotConsumeDecodedBudget()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WriteUndecodablePng(Path.Combine(pack, "assets", "broken.png"), 1, 1);
        TestTree.MutateManifest(pack, json =>
            json["clips"]!["broken"] = new JsonObject
            {
                ["format"] = "png",
                ["path"] = "assets/broken.png",
                ["playback"] = "hold"
            });
        var validator = new PackValidator(PackValidationLimits.Default with { MaximumDecodedImageBytes = 4 });

        var result = validator.ValidateDirectory(pack, CharacterPackSource.Installed);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.False(result.Pack!.Clips["broken"].IsAvailable);
        Assert.Contains("clip_unavailable", result.WarningCodes);
    }

    [Fact]
    public void ValidateDirectory_UndecodableOptionalClipReturnsWarningInsteadOfThrowing()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WriteStructurallyValidButUndecodablePng(Path.Combine(pack, "assets", "broken.png"), 1, 1);
        TestTree.MutateManifest(pack, json =>
            json["clips"]!["broken"] = new JsonObject
            {
                ["format"] = "png",
                ["path"] = "assets/broken.png",
                ["playback"] = "hold"
            });

        var result = new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.False(result.Pack!.Clips["broken"].IsAvailable);
        Assert.Contains("clip_unavailable", result.WarningCodes);
    }

    [Fact]
    public void ValidateDirectory_RejectsUndefinedClipReference()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json =>
            json["reactions"]!["spark"]!["animationCandidates"] = new JsonArray("missing")), "undefined_clip");
    }

    [Fact]
    public void ValidateDirectory_RejectsInvalidPngSheetGeometry()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WritePng(Path.Combine(pack, "assets", "sheet.png"), 2, 1);
        AddSheet(pack, frameWidth: 1, frameHeight: 1, columns: 1, frameCount: 2, fps: 12);

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "sheet_geometry");
    }

    [Fact]
    public void ValidateDirectory_RejectsFrameCountOver256()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WritePng(Path.Combine(pack, "assets", "sheet.png"), 257, 1);
        AddSheet(pack, frameWidth: 1, frameHeight: 1, columns: 257, frameCount: 257, fps: 12);

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_clip");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void ValidateDirectory_RejectsFpsOutsideOneToTwentyFour(int fps)
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        TestTree.WritePng(Path.Combine(pack, "assets", "sheet.png"), 1, 1);
        AddSheet(pack, frameWidth: 1, frameHeight: 1, columns: 1, frameCount: 1, fps: fps);

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_clip");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateDirectory_RejectsEmptyReactionMeaning(string? meaning)
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json => json["reactions"]!["spark"]!["meaning"] = meaning), "invalid_reaction");
    }

    [Fact]
    public void ValidateDirectory_RejectsReactionMeaningOver500Characters()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json =>
            json["reactions"]!["spark"]!["meaning"] = new string('x', 501)), "invalid_reaction");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ValidateDirectory_RejectsDefaultIntensityOutsideBounds(int intensity)
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json =>
            json["reactions"]!["spark"]!["defaultIntensity"] = intensity), "invalid_reaction");
    }

    [Theory]
    [InlineData(499)]
    [InlineData(6001)]
    public void ValidateDirectory_RejectsVisibleMillisecondsOutsideBounds(int visibleMs)
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json =>
            json["reactions"]!["spark"]!["visibleMs"] = visibleMs), "invalid_reaction");
    }

    [Fact]
    public void ValidateDirectory_RejectsDuplicateIntensityBands()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json => json["reactions"]!["spark"]!["intensityBands"] =
            new JsonArray(Band(50), Band(50))), "invalid_reaction");
    }

    [Fact]
    public void ValidateDirectory_RejectsNonascendingIntensityBands()
    {
        using var tree = new TestTree();
        AssertInvalid(ValidateSynthetic(tree, mutate: json => json["reactions"]!["spark"]!["intensityBands"] =
            new JsonArray(Band(75), Band(25))), "invalid_reaction");
    }

    [Fact]
    public async Task ImportAsync_RejectsUserPackThatShadowsBundledId()
    {
        using var tree = new TestTree();
        tree.CreateSyntheticPack(Path.Combine(tree.BundledRoot, "builtin"), "alpha", "spark");
        var candidate = tree.CreateSyntheticPack(id: "alpha", reaction: "nod", version: "2.0.0");

        var result = await tree.CreateCatalog().ImportAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("bundled_id_shadow", result.DiagnosticCodes);
    }

    [Fact]
    public async Task ImportAsync_BlankSelectionReturnsBoundedFailure()
    {
        using var tree = new TestTree();

        var result = await tree.CreateCatalog().ImportAsync(" ", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("invalid_source", result.DiagnosticCodes);
    }

    [Fact]
    public async Task ImportAsync_FailureLeavesExistingInstalledVersionIntact()
    {
        using var tree = new TestTree();
        var installed = tree.CreateSyntheticPack(Path.Combine(tree.InstalledRoot, "alpha", "1.0.0"), "alpha", "spark");
        var original = File.ReadAllBytes(Path.Combine(installed, "pet.json"));
        var candidate = tree.CreateSyntheticPack(id: "alpha", version: "2.0.0");
        File.Delete(Path.Combine(candidate, "assets", "idle.png"));

        var result = await tree.CreateCatalog().ImportAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(installed, "pet.json")));
        Assert.False(Directory.Exists(Path.Combine(tree.InstalledRoot, "alpha", "2.0.0")));
    }

    [Fact]
    public async Task ImportAsync_AtomicPublishDoesNotExposePartialCandidate()
    {
        using var tree = new TestTree();
        var candidate = tree.CreateSyntheticPack(id: "alpha", version: "1.2.3");
        var observedBeforeMove = false;
        var catalog = tree.CreateCatalog(beforePublish: target =>
        {
            observedBeforeMove = true;
            Assert.False(Directory.Exists(target));
        });

        var result = await catalog.ImportAsync(candidate, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(",", result.DiagnosticCodes));
        Assert.True(observedBeforeMove);
        Assert.True(File.Exists(Path.Combine(tree.InstalledRoot, "alpha", "1.2.3", "pet.json")));
    }

    [Fact]
    public void Catalog_ResolvesOnlyExplicitInstalledVersion()
    {
        using var tree = new TestTree();
        tree.CreateSyntheticPack(Path.Combine(tree.InstalledRoot, "alpha", "1.0.0"), "alpha", "spark", "1.0.0");
        tree.CreateSyntheticPack(Path.Combine(tree.InstalledRoot, "alpha", "2.0.0"), "alpha", "spark", "2.0.0");
        var catalog = tree.CreateCatalog();
        _ = catalog.LoadInstalled();

        Assert.Equal("1.0.0", catalog.Resolve("alpha", "1.0.0")!.Version);
        Assert.Equal("2.0.0", catalog.Resolve("alpha", "2.0.0")!.Version);
        Assert.Null(catalog.Resolve("alpha", "3.0.0"));
        Assert.Null(catalog.Resolve("alpha", null));
    }

    [Fact]
    public void Catalog_ExcludesUnsupportedPackFromUsablePacks()
    {
        using var tree = new TestTree();
        tree.CreateSyntheticPack(
            Path.Combine(tree.InstalledRoot, "future", "1.0.0"),
            "future",
            "spark",
            mutate: json => json["minAppVersion"] = "3.0.0");

        var result = tree.CreateCatalog().LoadInstalled();

        Assert.DoesNotContain(result.Packs, pack => pack.Id == "future");
        Assert.Contains("unsupported_app_version", result.DiagnosticCodes);
    }

    [Theory]
    [InlineData("cobalt", "spark")]
    [InlineData("ochre", "nod")]
    public void ValidateDirectory_AcceptsIndependentSyntheticPackWithoutCoreChanges(string id, string reaction)
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, id, reaction);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.Contains(reaction, result.Pack!.Reactions.Keys);
    }

    [Fact]
    public void ValidateDirectory_ExposesImmutableCanonicalSnapshots()
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree);
        var pack = result.Pack!;

        Assert.True(Path.IsPathFullyQualified(pack.RootPath));
        Assert.StartsWith(Path.GetFullPath(tree.Path), pack.RootPath, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, CharacterClip>)pack.Clips).Add("mutated", pack.Clips["idle"]));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)pack.Reactions["spark"].AnimationCandidates).Add("mutated"));
    }

    [Fact]
    public void ValidateDirectory_RejectsClosedPersonaProfileViolations()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var profilePath = Path.Combine(pack, "persona", "profile.json");
        var profile = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
        profile["executable"] = "never";
        File.WriteAllText(profilePath, profile.ToJsonString(), new UTF8Encoding(false));

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_persona");
    }

    [Fact]
    public void ValidateDirectory_RejectsProfileForDifferentCharacter()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        var profilePath = Path.Combine(pack, "persona", "profile.json");
        var profile = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
        profile["characterId"] = "someone_else";
        File.WriteAllText(profilePath, profile.ToJsonString(), new UTF8Encoding(false));

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_persona");
    }

    [Fact]
    public void ValidateDirectory_AcceptsClosedThemeTokensAsInertData()
    {
        using var tree = new TestTree();
        var result = new PackValidator().ValidateDirectory(
            tree.CreateSyntheticPack(includeTheme: true),
            CharacterPackSource.Installed);

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.Equal("#112233", result.Pack!.Theme!.Colors["background"]);
    }

    [Fact]
    public void ValidateDirectory_MissingThemeFallsBackWithBoundedWarning()
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, mutate: json => json["theme"] = "theme/missing.json");

        Assert.True(result.IsValid, string.Join(",", result.DiagnosticCodes));
        Assert.Null(result.Pack!.Theme);
        Assert.Contains("theme_unavailable", result.WarningCodes);
    }

    [Fact]
    public void ValidateDirectory_RejectsCssInThemeTokens()
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack(includeTheme: true);
        var themePath = Path.Combine(pack, "theme", "tokens.json");
        var theme = JsonNode.Parse(File.ReadAllText(themePath))!.AsObject();
        theme["css"] = "body { display: none }";
        File.WriteAllText(themePath, theme.ToJsonString(), new UTF8Encoding(false));

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "invalid_theme");
    }

    [Theory]
    [InlineData("payload.psm1")]
    [InlineData("payload.vbs")]
    [InlineData("payload.sh")]
    [InlineData("payload.py")]
    public void ValidateDirectory_RejectsScriptAndExecutableFileTypes(string fileName)
    {
        using var tree = new TestTree();
        var pack = tree.CreateSyntheticPack();
        File.WriteAllText(Path.Combine(pack, fileName), "not executable here");

        AssertInvalid(new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed), "forbidden_file_type");
    }

    [Fact]
    public void ValidateDirectory_RejectsControlCharactersInMetadata()
    {
        using var tree = new TestTree();
        var result = ValidateSynthetic(tree, mutate: json =>
            json["metadata"]!["description"] = "bad\u0001metadata");

        AssertInvalid(result, "invalid_metadata");
    }

    [Fact]
    public async Task ImportAsync_ReturnsBoundedFailureForMalformedPath()
    {
        using var tree = new TestTree();

        var result = await tree.CreateCatalog().ImportAsync("bad\0path", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("import_failed", result.DiagnosticCodes);
    }

    [Fact]
    public async Task ImportAsync_RejectsReparseInstalledRoot_WhenCreationIsPermitted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new TestTree();
        var candidate = tree.CreateSyntheticPack(id: "alpha", version: "1.2.3");
        var external = Directory.CreateDirectory(Path.Combine(tree.Path, "outside"));
        Directory.Delete(tree.InstalledRoot);
        try
        {
            Directory.CreateSymbolicLink(tree.InstalledRoot, external.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await tree.CreateCatalog().ImportAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("reparse_point", result.DiagnosticCodes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.FullName));
    }

    [Fact]
    public async Task CatalogAndImport_RejectIdLevelReparsePoint_WhenCreationIsPermitted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new TestTree();
        var externalIdRoot = Directory.CreateDirectory(Path.Combine(tree.Path, "outside", "alpha"));
        tree.CreateSyntheticPack(
            Path.Combine(externalIdRoot.FullName, "1.0.0"),
            "alpha",
            "spark",
            "1.0.0");
        if (!TryCreateJunction(Path.Combine(tree.InstalledRoot, "alpha"), externalIdRoot.FullName))
            return;

        var catalog = tree.CreateCatalog();
        var loaded = catalog.LoadInstalled();
        Assert.DoesNotContain(loaded.Packs, pack => pack.Id == "alpha");
        Assert.Contains("reparse_point", loaded.DiagnosticCodes);

        var candidate = tree.CreateSyntheticPack(id: "alpha", version: "2.0.0");
        var imported = await catalog.ImportAsync(candidate, CancellationToken.None);

        Assert.False(imported.Succeeded);
        Assert.Contains("reparse_point", imported.DiagnosticCodes);
        Assert.False(Directory.Exists(Path.Combine(externalIdRoot.FullName, "2.0.0")));
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath) &&
                   (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static PackValidationResult ValidateSynthetic(
        TestTree tree,
        string id = "alpha",
        string reaction = "spark",
        Action<JsonObject>? mutate = null)
    {
        var pack = tree.CreateSyntheticPack(id: id, reaction: reaction, mutate: mutate);
        return new PackValidator().ValidateDirectory(pack, CharacterPackSource.Installed);
    }

    private static void AssertUnsafeManifestPath(TestTree tree, string path)
    {
        var result = ValidateSynthetic(tree, mutate: json => json["clips"]!["idle"]!["path"] = path);
        AssertInvalid(result, "unsafe_path");
    }

    private static void AssertInvalid(PackValidationResult result, string code)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Pack);
        Assert.Contains(code, result.DiagnosticCodes);
    }

    private static JsonObject Band(int minimum) => new()
    {
        ["min"] = minimum,
        ["animationCandidates"] = new JsonArray("idle")
    };

    private static void AddSheet(
        string pack,
        int frameWidth,
        int frameHeight,
        int columns,
        int frameCount,
        int fps)
    {
        TestTree.MutateManifest(pack, json =>
            json["clips"]!["sheet"] = new JsonObject
            {
                ["format"] = "pngSheet",
                ["path"] = "assets/sheet.png",
                ["frameWidth"] = frameWidth,
                ["frameHeight"] = frameHeight,
                ["columns"] = columns,
                ["frameCount"] = frameCount,
                ["fps"] = fps,
                ["playback"] = "loop"
            });
    }

    private static void WriteZipText(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private sealed class TestTree : IDisposable
    {
        public TestTree()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PetGPT.CharacterPackTests", Guid.NewGuid().ToString("N"));
            BundledRoot = System.IO.Path.Combine(Path, "app", "Pets");
            InstalledRoot = System.IO.Path.Combine(Path, "local", "Pets");
            Directory.CreateDirectory(BundledRoot);
            Directory.CreateDirectory(InstalledRoot);
        }

        public string Path { get; }
        public string BundledRoot { get; }
        public string InstalledRoot { get; }

        public string CreateSyntheticPack(
            string? destination = null,
            string id = "alpha",
            string reaction = "spark",
            string version = "1.0.0",
            bool includeTheme = false,
            Action<JsonObject>? mutate = null)
        {
            destination ??= System.IO.Path.Combine(Path, "candidates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(destination, "assets"));
            Directory.CreateDirectory(System.IO.Path.Combine(destination, "persona"));
            WritePng(System.IO.Path.Combine(destination, "assets", "idle.png"), 1, 1);

            var profile = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["characterId"] = id,
                ["identity"] = $"Synthetic {id}",
                ["worldview"] = "Tests should be deterministic.",
                ["values"] = new JsonArray("clarity"),
                ["likes"] = new JsonArray("small fixtures"),
                ["dislikes"] = new JsonArray(),
                ["fears"] = new JsonArray(),
                ["taboos"] = new JsonArray(),
                ["humor"] = new JsonArray(),
                ["relationshipToUser"] = "A synthetic test companion.",
                ["appraisalPrinciples"] = new JsonArray("Prefer literal evidence."),
                ["factualAnswerStyle"] = "State only fixture facts."
            };
            File.WriteAllText(
                System.IO.Path.Combine(destination, "persona", "profile.json"),
                profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                System.IO.Path.Combine(destination, "persona", "voice.md"),
                "Synthetic voice fixture. No executable behavior.",
                new UTF8Encoding(false));

            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["displayName"] = $"Synthetic {id}",
                ["version"] = version,
                ["minAppVersion"] = "2.0.0",
                ["persona"] = new JsonObject
                {
                    ["profile"] = "persona/profile.json",
                    ["voice"] = "persona/voice.md"
                },
                ["presentation"] = new JsonObject
                {
                    ["widthDip"] = 150,
                    ["heightDip"] = 150,
                    ["anchor"] = new JsonObject { ["x"] = 0.5, ["y"] = 1.0 }
                },
                ["clips"] = new JsonObject
                {
                    ["idle"] = new JsonObject
                    {
                        ["format"] = "png",
                        ["path"] = "assets/idle.png",
                        ["playback"] = "hold"
                    }
                },
                ["systemAnimations"] = new JsonObject { ["idle"] = new JsonArray("idle") },
                ["reactions"] = new JsonObject
                {
                    [reaction] = new JsonObject
                    {
                        ["meaning"] = $"Synthetic {reaction} reaction.",
                        ["animationCandidates"] = new JsonArray("idle"),
                        ["defaultIntensity"] = 50,
                        ["visibleMs"] = 1500
                    }
                },
                ["theme"] = null,
                ["trayIcon"] = null,
                ["metadata"] = new JsonObject
                {
                    ["author"] = "PetGPT tests",
                    ["description"] = "Generated geometric test data."
                }
            };

            if (includeTheme)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(destination, "theme"));
                File.WriteAllText(
                    System.IO.Path.Combine(destination, "theme", "tokens.json"),
                    ThemeJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                manifest["theme"] = "theme/tokens.json";
            }

            mutate?.Invoke(manifest);
            File.WriteAllText(
                System.IO.Path.Combine(destination, "pet.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return destination;
        }

        public CharacterCatalog CreateCatalog(
            PackValidator? validator = null,
            Action<string>? beforePublish = null) =>
            new(BundledRoot, InstalledRoot, validator ?? new PackValidator(), beforePublish);

        public string ZipPack(string pack, string fileName, Action<ZipArchive>? append = null)
        {
            var archivePath = System.IO.Path.Combine(Path, fileName);
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(pack, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(pack, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }

            append?.Invoke(archive);
            return archivePath;
        }

        public static void MutateManifest(string pack, Action<JsonObject> mutate)
        {
            var path = System.IO.Path.Combine(pack, "pet.json");
            var manifest = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
            mutate(manifest);
            File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        public static void WritePng(string path, int width, int height)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            Span<byte> ihdr = stackalloc byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(output, "IHDR", ihdr);

            var raw = new byte[checked(height * (1 + (width * 4)))];
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", []);
        }

        public static void WriteUndecodablePng(string path, int width, int height)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using (var output = File.Create(path))
            {
                output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                Span<byte> ihdr = stackalloc byte[13];
                WriteBigEndian(ihdr, 0, width);
                WriteBigEndian(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                WriteChunk(output, "IHDR", ihdr);
                WriteChunk(output, "IDAT", [1, 2, 3, 4]);
                WriteChunk(output, "IEND", []);
            }
            var bytes = File.ReadAllBytes(path);
            bytes[29] ^= 0xff; // Break the IHDR CRC while preserving bounded header fields.
            File.WriteAllBytes(path, bytes);
        }

        public static void WriteStructurallyValidButUndecodablePng(string path, int width, int height)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var output = File.Create(path);
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            Span<byte> ihdr = stackalloc byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 3; // Indexed color without the required PLTE chunk.
            WriteChunk(output, "IHDR", ihdr);
            var raw = new byte[checked(height * (1 + width))];
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
            WriteChunk(output, "IEND", []);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }

        private static JsonObject ThemeJson() => new()
        {
            ["schemaVersion"] = 1,
            ["colors"] = new JsonObject
            {
                ["background"] = "#112233",
                ["surface"] = "#223344",
                ["text"] = "#FFFFFF",
                ["mutedText"] = "#CCCCCC",
                ["accent"] = "#778899",
                ["border"] = "#445566",
                ["composerBackground"] = "#334455",
                ["composerText"] = "#FFFFFF",
                ["scrollbarThumb"] = "#667788"
            },
            ["metrics"] = new JsonObject
            {
                ["surfaceRadiusPx"] = 14,
                ["composerRadiusPx"] = 18,
                ["borderWidthPx"] = 1,
                ["scrollbarWidthPx"] = 8
            }
        };

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            WriteBigEndian(length, 0, data.Length);
            stream.Write(length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            var crcInput = new byte[typeBytes.Length + data.Length];
            typeBytes.CopyTo(crcInput, 0);
            data.CopyTo(crcInput.AsSpan(typeBytes.Length));
            Span<byte> crc = stackalloc byte[4];
            WriteBigEndian(crc, 0, unchecked((int)Crc32(crcInput)));
            stream.Write(crc);
        }

        private static void WriteBigEndian(Span<byte> destination, int offset, int value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xffffffffu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }

            return ~crc;
        }
    }
}
