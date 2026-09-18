using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetGPT.Personas;

namespace PetGPT.Characters;

public sealed class PackValidator
{
    public const string ApplicationCompatibilityVersion = "2.0.0";

    private const int MaximumJsonDepth = 32;
    private const int MaximumDiagnostics = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex IdentifierPattern = new(
        @"\A[a-z][a-z0-9_]{0,31}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ColorPattern = new(
        "^#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> ReservedPackIds = new(["list", "sleep", "wake"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedSystemAnimations = new(
        ["idle", "hover", "dragging", "chatOpen", "userTyping", "generating", "sleep"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> ForbiddenExtensions = new(
        [
            ".dll", ".exe", ".com", ".scr", ".msi", ".sys", ".drv", ".ocx", ".cpl",
            ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
            ".mjs", ".cjs", ".ts", ".tsx", ".sh", ".bash", ".zsh", ".fish", ".py",
            ".rb", ".pl", ".php", ".jar", ".class", ".xaml", ".html", ".htm",
            ".css", ".scss", ".less", ".svg", ".ttf", ".otf", ".woff", ".woff2", ".eot", ".fon"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RequiredColorNames =
    [
        "background", "surface", "text", "mutedText", "accent", "border",
        "composerBackground", "composerText", "scrollbarThumb"
    ];
    private static readonly string[] RequiredMetricNames =
    [
        "surfaceRadiusPx", "composerRadiusPx", "borderWidthPx", "scrollbarWidthPx"
    ];

    private readonly PackValidationLimits _limits;
    private readonly SemanticVersion _applicationVersion;

    public PackValidator()
        : this(PackValidationLimits.Default)
    {
    }

    public PackValidator(PackValidationLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (!SemanticVersion.TryParse(ApplicationCompatibilityVersion, out _applicationVersion))
            throw new InvalidOperationException("The application compatibility version is invalid.");
    }

    public PackValidationLimits Limits => _limits;

    public PackValidationResult ValidateDirectory(string packRoot, CharacterPackSource source)
    {
        var warnings = new List<string>();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
            var canonicalRoot = Path.GetFullPath(packRoot);
            if (!Directory.Exists(canonicalRoot))
                throw new PackFormatException("pack_missing");

            InspectTree(canonicalRoot);
            var manifestPath = Path.Combine(canonicalRoot, "pet.json");
            if (!File.Exists(manifestPath))
                throw new PackFormatException("manifest_missing");
            if (new FileInfo(manifestPath).Length > _limits.MaximumManifestBytes)
                throw new PackFormatException("manifest_too_large");

            using var manifest = ParseJsonFile(manifestPath, _limits.MaximumManifestBytes, "invalid_manifest");
            var root = RequireObject(manifest.RootElement, "invalid_manifest");
            RequireOnlyProperties(root,
            [
                "schemaVersion", "id", "displayName", "version", "minAppVersion", "persona",
                "presentation", "clips", "systemAnimations", "reactions", "theme", "trayIcon", "metadata"
            ], "unknown_property");

            if (RequireInt32(root, "schemaVersion", "invalid_manifest") != 1)
                throw new PackFormatException("unsupported_schema");

            var id = RequireText(root, "id", 32, "invalid_id");
            if (!IdentifierPattern.IsMatch(id))
                throw new PackFormatException("invalid_id");
            if (ReservedPackIds.Contains(id) || (id == "legacy" && source != CharacterPackSource.Bundled))
                throw new PackFormatException("reserved_id");
            if (source == CharacterPackSource.Bundled && id == "legacy")
            {
                // The bundled compatibility pack is the sole owner of this reserved ID.
            }

            var displayName = RequireText(root, "displayName", 80, "invalid_manifest");
            var versionText = RequireText(root, "version", 128, "invalid_semver");
            var minimumText = RequireText(root, "minAppVersion", 128, "invalid_semver");
            if (!SemanticVersion.TryParse(versionText, out _))
                throw new PackFormatException("invalid_semver");
            if (!SemanticVersion.TryParse(minimumText, out var minimumVersion))
                throw new PackFormatException("invalid_semver");
            if (minimumVersion.CompareTo(_applicationVersion) > 0)
                throw new PackFormatException("unsupported_app_version");

            var presentation = ParsePresentation(RequireProperty(root, "presentation", "invalid_manifest"));
            var persona = ParsePersona(root, canonicalRoot, id, source);

            long decodedBudget = 0;
            var countedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clips = ParseClips(
                RequireProperty(root, "clips", "invalid_manifest"),
                canonicalRoot,
                warnings,
                countedImages,
                ref decodedBudget);

            if (!clips.TryGetValue("idle", out var idle) || !idle.IsAvailable)
                throw new PackFormatException("idle_unavailable");

            var systemAnimations = ParseSystemAnimations(root, clips);
            var reactions = ParseReactions(root, clips, id == "legacy" && source == CharacterPackSource.Bundled);
            var theme = ParseTheme(root, canonicalRoot, warnings, countedImages, ref decodedBudget);
            var trayIcon = ParseOptionalPath(root, "trayIcon", canonicalRoot, ".ico", countedImages, ref decodedBudget);
            var metadata = ParseMetadata(root);

            if (decodedBudget > _limits.MaximumDecodedImageBytes)
                throw new PackFormatException("decoded_image_budget");

            if (id == "legacy" && source == CharacterPackSource.Bundled &&
                (persona is not null || reactions.Count != 0))
            {
                throw new PackFormatException("invalid_legacy_pack");
            }

            var pack = new CharacterPack(
                canonicalRoot,
                source,
                id,
                displayName,
                versionText,
                minimumText,
                persona,
                presentation,
                clips,
                systemAnimations,
                reactions,
                theme,
                trayIcon,
                metadata,
                warnings);
            return new PackValidationResult(pack, [], warnings);
        }
        catch (PackFormatException exception)
        {
            return PackValidationResult.Invalid(exception.Code);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or OverflowException)
        {
            return PackValidationResult.Invalid("pack_read_failed");
        }
    }

    internal static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.IndexOf('\0') >= 0)
            return false;
        if (value.Contains('\\') || value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.Contains(':'))
            return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return false;

        var segments = value.Split('/', StringSplitOptions.None);
        return segments.Length > 0 && segments.All(segment =>
            segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith(' ') && !segment.EndsWith('.'));
    }

    internal static string ResolveSafePath(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new PackFormatException("unsafe_path");

        var canonicalRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new PackFormatException("unsafe_path");
        return candidate;
    }

    private void InspectTree(string canonicalRoot)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(canonicalRoot));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        var fileCount = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PackFormatException("reparse_point");

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PackFormatException("reparse_point");

                var relative = Path.GetRelativePath(canonicalRoot, entry.FullName).Replace('\\', '/');
                if (!IsSafeRelativePath(relative))
                    throw new PackFormatException("unsafe_path");
                if (!names.Add(relative))
                    throw new PackFormatException("case_collision");

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                    continue;
                }

                var file = (FileInfo)entry;
                fileCount++;
                if (fileCount > _limits.MaximumFiles)
                    throw new PackFormatException("too_many_files");
                expandedBytes = checked(expandedBytes + file.Length);
                if (expandedBytes > _limits.MaximumExpandedBytes)
                    throw new PackFormatException("expanded_too_large");
                if (ForbiddenExtensions.Contains(file.Extension))
                    throw new PackFormatException("forbidden_file_type");
            }
        }
    }

    private PersonaProfile? ParsePersona(
        JsonElement manifest,
        string packRoot,
        string packId,
        CharacterPackSource source)
    {
        var personaElement = RequireProperty(manifest, "persona", "invalid_persona");
        if (personaElement.ValueKind == JsonValueKind.Null)
        {
            if (source == CharacterPackSource.Bundled && packId == "legacy")
                return null;
            throw new PackFormatException("invalid_persona");
        }

        var personaObject = RequireObject(personaElement, "invalid_persona");
        RequireOnlyProperties(personaObject, ["profile", "voice"], "invalid_persona");
        var profilePath = ResolveRequiredFile(packRoot, RequireText(personaObject, "profile", 512, "invalid_persona"));
        var voicePath = ResolveRequiredFile(packRoot, RequireText(personaObject, "voice", 512, "invalid_persona"));
        var combinedBytes = checked(new FileInfo(profilePath).Length + new FileInfo(voicePath).Length);
        if (combinedBytes > _limits.MaximumPersonaBytes)
            throw new PackFormatException("persona_too_large");

        using var profileDocument = ParseJsonFile(profilePath, _limits.MaximumPersonaBytes, "invalid_persona");
        var profile = RequireObject(profileDocument.RootElement, "invalid_persona");
        RequireOnlyProperties(profile,
        [
            "schemaVersion", "characterId", "identity", "worldview", "values", "likes", "dislikes",
            "fears", "taboos", "humor", "relationshipToUser", "appraisalPrinciples", "factualAnswerStyle"
        ], "invalid_persona");
        if (RequireInt32(profile, "schemaVersion", "invalid_persona") != 1 ||
            RequireText(profile, "characterId", 32, "invalid_persona") != packId)
        {
            throw new PackFormatException("invalid_persona");
        }

        var voiceBytes = File.ReadAllBytes(voicePath);
        var voice = DecodeStrict(voiceBytes, "invalid_persona");
        if (voice.Length == 0)
            throw new PackFormatException("invalid_persona");

        return new PersonaProfile(
            profilePath,
            voicePath,
            packId,
            RequireText(profile, "identity", 1000, "invalid_persona"),
            RequireText(profile, "worldview", 1000, "invalid_persona"),
            RequireStringArray(profile, "values", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireStringArray(profile, "likes", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireStringArray(profile, "dislikes", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireStringArray(profile, "fears", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireStringArray(profile, "taboos", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireStringArray(profile, "humor", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireText(profile, "relationshipToUser", 1000, "invalid_persona"),
            RequireStringArray(profile, "appraisalPrinciples", 20, 1000, allowEmpty: true, "invalid_persona"),
            RequireText(profile, "factualAnswerStyle", 1000, "invalid_persona"),
            voice);
    }

    private CharacterPresentation ParsePresentation(JsonElement element)
    {
        var presentation = RequireObject(element, "invalid_presentation");
        RequireOnlyProperties(presentation, ["widthDip", "heightDip", "anchor"], "invalid_presentation");
        var width = RequireFiniteDouble(presentation, "widthDip", "invalid_presentation");
        var height = RequireFiniteDouble(presentation, "heightDip", "invalid_presentation");
        var anchor = RequireObject(RequireProperty(presentation, "anchor", "invalid_presentation"), "invalid_presentation");
        RequireOnlyProperties(anchor, ["x", "y"], "invalid_presentation");
        var anchorX = RequireFiniteDouble(anchor, "x", "invalid_presentation");
        var anchorY = RequireFiniteDouble(anchor, "y", "invalid_presentation");
        if (width is < 64 or > 512 || height is < 64 or > 512 ||
            anchorX is < 0 or > 1 || anchorY is < 0 or > 1)
        {
            throw new PackFormatException("invalid_presentation");
        }

        return new CharacterPresentation(width, height, anchorX, anchorY);
    }

    private Dictionary<string, CharacterClip> ParseClips(
        JsonElement element,
        string packRoot,
        List<string> warnings,
        HashSet<string> countedImages,
        ref long decodedBudget)
    {
        var clipsObject = RequireObject(element, "invalid_clip");
        var properties = clipsObject.EnumerateObject().ToArray();
        if (properties.Length is < 1 or > 64)
            throw new PackFormatException("invalid_clip");

        var clips = new Dictionary<string, CharacterClip>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!IdentifierPattern.IsMatch(property.Name))
                throw new PackFormatException("invalid_clip");
            var clipObject = RequireObject(property.Value, "invalid_clip");
            var format = RequireText(clipObject, "format", 16, "invalid_clip");
            var isSheet = format == "pngSheet";
            if (format != "png" && !isSheet)
                throw new PackFormatException("invalid_clip");
            RequireOnlyProperties(
                clipObject,
                isSheet
                    ? ["format", "path", "frameWidth", "frameHeight", "columns", "frameCount", "fps", "playback"]
                    : ["format", "path", "playback"],
                "invalid_clip");

            var relativePath = RequireText(clipObject, "path", 512, "unsafe_path");
            var assetPath = ResolveSafePath(packRoot, relativePath);
            var playback = RequireText(clipObject, "playback", 16, "invalid_clip");
            if ((!isSheet && playback != "hold") || (isSheet && playback is not ("once" or "loop")))
                throw new PackFormatException("invalid_clip");

            int? frameWidth = null;
            int? frameHeight = null;
            int? columns = null;
            int? frameCount = null;
            int? fps = null;
            if (isSheet)
            {
                frameWidth = RequireInt32(clipObject, "frameWidth", "invalid_clip");
                frameHeight = RequireInt32(clipObject, "frameHeight", "invalid_clip");
                columns = RequireInt32(clipObject, "columns", "invalid_clip");
                frameCount = RequireInt32(clipObject, "frameCount", "invalid_clip");
                fps = RequireInt32(clipObject, "fps", "invalid_clip");
                if (frameWidth <= 0 || frameHeight <= 0 || columns <= 0 ||
                    frameCount is <= 0 or > 256 || fps is < 1 or > 24)
                {
                    throw new PackFormatException("invalid_clip");
                }
            }

            var available = TryReadPng(assetPath, countedImages, ref decodedBudget, out var imageWidth, out var imageHeight);
            if (!available)
            {
                if (property.Name == "idle")
                    throw new PackFormatException("idle_unavailable");
                AddWarning(warnings, "clip_unavailable");
            }
            else if (isSheet)
            {
                var expectedWidth = checked(frameWidth!.Value * columns!.Value);
                var rows = checked((frameCount!.Value + columns.Value - 1) / columns.Value);
                var expectedHeight = checked(frameHeight!.Value * rows);
                if (imageWidth != expectedWidth || imageHeight != expectedHeight)
                    throw new PackFormatException("sheet_geometry");
            }

            clips.Add(property.Name, new CharacterClip(
                property.Name,
                format,
                assetPath,
                playback,
                available,
                imageWidth,
                imageHeight,
                frameWidth,
                frameHeight,
                columns,
                frameCount,
                fps));
        }

        return clips;
    }

    private Dictionary<string, IReadOnlyList<string>> ParseSystemAnimations(
        JsonElement manifest,
        IReadOnlyDictionary<string, CharacterClip> clips)
    {
        if (!manifest.TryGetProperty("systemAnimations", out var element))
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var systemObject = RequireObject(element, "invalid_system_animation");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in systemObject.EnumerateObject())
        {
            if (!AllowedSystemAnimations.Contains(property.Name))
                throw new PackFormatException("invalid_system_animation");
            var candidates = ReadCandidateArray(property.Value, clips, "invalid_system_animation");
            result.Add(property.Name, Array.AsReadOnly(candidates));
        }

        return result;
    }

    private Dictionary<string, CharacterReaction> ParseReactions(
        JsonElement manifest,
        IReadOnlyDictionary<string, CharacterClip> clips,
        bool legacy)
    {
        var reactionsObject = RequireObject(RequireProperty(manifest, "reactions", "invalid_reaction"), "invalid_reaction");
        var properties = reactionsObject.EnumerateObject().ToArray();
        if (properties.Length > 64 || (!legacy && properties.Length == 0))
            throw new PackFormatException("invalid_reaction");

        var result = new Dictionary<string, CharacterReaction>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!IdentifierPattern.IsMatch(property.Name))
                throw new PackFormatException("invalid_reaction");
            var reaction = RequireObject(property.Value, "invalid_reaction");
            RequireOnlyProperties(
                reaction,
                ["meaning", "animationCandidates", "defaultIntensity", "visibleMs", "intensityBands"],
                "invalid_reaction");
            var meaning = RequireText(reaction, "meaning", 500, "invalid_reaction");
            var candidates = ReadCandidateArray(
                RequireProperty(reaction, "animationCandidates", "invalid_reaction"),
                clips,
                "invalid_reaction");
            var intensity = RequireInt32(reaction, "defaultIntensity", "invalid_reaction");
            var visibleMs = RequireInt32(reaction, "visibleMs", "invalid_reaction");
            if (intensity is < 0 or > 100 || visibleMs is < 500 or > 6000)
                throw new PackFormatException("invalid_reaction");

            var bands = new List<ReactionIntensityBand>();
            if (reaction.TryGetProperty("intensityBands", out var bandElement))
            {
                if (bandElement.ValueKind != JsonValueKind.Array || bandElement.GetArrayLength() > 101)
                    throw new PackFormatException("invalid_reaction");
                var prior = -1;
                foreach (var item in bandElement.EnumerateArray())
                {
                    var band = RequireObject(item, "invalid_reaction");
                    RequireOnlyProperties(band, ["min", "animationCandidates"], "invalid_reaction");
                    var minimum = RequireInt32(band, "min", "invalid_reaction");
                    if (minimum is < 0 or > 100 || minimum <= prior)
                        throw new PackFormatException("invalid_reaction");
                    prior = minimum;
                    bands.Add(new ReactionIntensityBand(
                        minimum,
                        ReadCandidateArray(
                            RequireProperty(band, "animationCandidates", "invalid_reaction"),
                            clips,
                            "invalid_reaction")));
                }
            }

            result.Add(property.Name, new CharacterReaction(
                property.Name,
                meaning,
                candidates,
                intensity,
                visibleMs,
                bands));
        }

        return result;
    }

    private ThemeTokens? ParseTheme(
        JsonElement manifest,
        string packRoot,
        List<string> warnings,
        HashSet<string> countedImages,
        ref long decodedBudget)
    {
        if (!manifest.TryGetProperty("theme", out var themeProperty) || themeProperty.ValueKind == JsonValueKind.Null)
            return null;
        if (themeProperty.ValueKind != JsonValueKind.String)
            throw new PackFormatException("invalid_theme");
        var themePath = ResolveSafePath(packRoot, themeProperty.GetString()!);
        if (!File.Exists(themePath))
        {
            AddWarning(warnings, "theme_unavailable");
            return null;
        }
        if (new FileInfo(themePath).Length > _limits.MaximumThemeBytes)
            throw new PackFormatException("theme_too_large");

        using var document = ParseJsonFile(themePath, _limits.MaximumThemeBytes, "invalid_theme");
        var theme = RequireObject(document.RootElement, "invalid_theme");
        RequireOnlyProperties(theme, ["schemaVersion", "colors", "metrics", "decoration"], "invalid_theme");
        if (RequireInt32(theme, "schemaVersion", "invalid_theme") != 1)
            throw new PackFormatException("invalid_theme");

        var colorsObject = RequireObject(RequireProperty(theme, "colors", "invalid_theme"), "invalid_theme");
        RequireOnlyProperties(colorsObject, RequiredColorNames, "invalid_theme");
        var colors = RequiredColorNames.ToDictionary(
            name => name,
            name => RequireText(colorsObject, name, 9, "invalid_theme"),
            StringComparer.Ordinal);
        if (colors.Values.Any(color => !ColorPattern.IsMatch(color)))
            throw new PackFormatException("invalid_theme");

        var metricsObject = RequireObject(RequireProperty(theme, "metrics", "invalid_theme"), "invalid_theme");
        RequireOnlyProperties(metricsObject, RequiredMetricNames, "invalid_theme");
        var metrics = RequiredMetricNames.ToDictionary(
            name => name,
            name => RequireInt32(metricsObject, name, "invalid_theme"),
            StringComparer.Ordinal);
        if (metrics["surfaceRadiusPx"] is < 0 or > 24 ||
            metrics["composerRadiusPx"] is < 0 or > 24 ||
            metrics["borderWidthPx"] is < 0 or > 4 ||
            metrics["scrollbarWidthPx"] is < 6 or > 20)
        {
            throw new PackFormatException("invalid_theme");
        }

        ThemeDecoration? decoration = null;
        if (theme.TryGetProperty("decoration", out var decorationElement))
        {
            var decorationObject = RequireObject(decorationElement, "invalid_theme");
            RequireOnlyProperties(decorationObject, ["path", "opacityPercent", "placement"], "invalid_theme");
            var decorationPath = ResolveRequiredFile(
                packRoot,
                RequireText(decorationObject, "path", 512, "invalid_theme"));
            var opacity = RequireInt32(decorationObject, "opacityPercent", "invalid_theme");
            var placement = RequireText(decorationObject, "placement", 16, "invalid_theme");
            if (opacity is < 0 or > 30 || placement is not ("background" or "corner"))
            {
                throw new PackFormatException("invalid_theme");
            }
            if (!Path.GetExtension(decorationPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new PackFormatException("invalid_theme");
            if (new FileInfo(decorationPath).Length > _limits.MaximumDecorationBytes)
                throw new PackFormatException("image_too_large");
            if (!TryReadPng(decorationPath, countedImages, ref decodedBudget, out _, out _))
                throw new PackFormatException("invalid_theme");
            decoration = new ThemeDecoration(decorationPath, opacity, placement);
        }

        return new ThemeTokens(themePath, colors, metrics, decoration);
    }

    private string? ParseOptionalPath(
        JsonElement root,
        string propertyName,
        string packRoot,
        string extension,
        HashSet<string> countedImages,
        ref long decodedBudget)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new PackFormatException("invalid_manifest");
        var path = ResolveRequiredFile(packRoot, element.GetString()!);
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new PackFormatException("invalid_manifest");
        if (new FileInfo(path).Length > _limits.MaximumImageBytes)
            throw new PackFormatException("image_too_large");
        ValidateIcon(path, countedImages, ref decodedBudget);
        return path;
    }

    private void ValidateIcon(string path, HashSet<string> countedImages, ref long decodedBudget)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 22 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
            throw new PackFormatException("invalid_tray_icon");
        var count = reader.ReadUInt16();
        if (count is < 1 or > 256 || stream.Length < 6L + (count * 16L))
            throw new PackFormatException("invalid_tray_icon");

        var largestDecoded = 0L;
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            var encodedBytes = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var pixelWidth = width == 0 ? 256 : width;
            var pixelHeight = height == 0 ? 256 : height;
            if (pixelWidth > _limits.MaximumImageDimension || pixelHeight > _limits.MaximumImageDimension ||
                encodedBytes == 0 || encodedBytes > _limits.MaximumImageBytes || offset < 6L + count * 16L || offset > stream.Length ||
                encodedBytes > stream.Length - offset)
            {
                throw new PackFormatException("invalid_tray_icon");
            }
            var nextEntry = stream.Position;
            stream.Position = offset;
            var payload = reader.ReadBytes(checked((int)encodedBytes));
            stream.Position = nextEntry;
            largestDecoded = Math.Max(largestDecoded, ValidateIconPayload(payload, pixelWidth, pixelHeight, decodedBudget));
        }

        if (countedImages.Add(path))
        {
            decodedBudget = checked(decodedBudget + largestDecoded);
            if (decodedBudget > _limits.MaximumDecodedImageBytes)
                throw new PackFormatException("decoded_image_budget");
        }
    }

    private long ValidateIconPayload(byte[] payload, int width, int height, long decodedBudget)
    {
        try
        {
            if (payload.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                using var png = new MemoryStream(payload, writable: false);
                var candidateBudget = decodedBudget;
                if (!TryReadPng(png, "icon", new HashSet<string>(), ref candidateBudget, out _, out _, width, height))
                    throw new PackFormatException("invalid_tray_icon");
                return checked(candidateBudget - decodedBudget);
            }

            // Support uncompressed BITMAPINFOHEADER-family DIBs. ICO height includes
            // both the color bitmap and the one-bit transparency mask.
            if (payload.Length < 40)
                throw new PackFormatException("invalid_tray_icon");
            var header = payload.AsSpan();
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var actualWidth = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var doubledHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var planes = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
            if (headerSize is not (40 or 52 or 56 or 108 or 124) || headerSize > payload.Length ||
                actualWidth != width || doubledHeight <= 0 || doubledHeight % 2 != 0 || doubledHeight / 2 != height ||
                actualWidth > _limits.MaximumImageDimension || doubledHeight / 2 > _limits.MaximumImageDimension ||
                planes != 1 || bitCount is not (1 or 4 or 8 or 16 or 24 or 32) || compression != 0)
            {
                throw new PackFormatException("invalid_tray_icon");
            }
            // Linked/embedded V5 color profiles are outside the supported data-only subset.
            if (headerSize == 124 && (BinaryPrimitives.ReadUInt32LittleEndian(header[112..]) != 0 ||
                                      BinaryPrimitives.ReadUInt32LittleEndian(header[116..]) != 0))
                throw new PackFormatException("invalid_tray_icon");
            var paletteEntries = bitCount <= 8 ? (colorsUsed == 0 ? 1L << bitCount : colorsUsed) : 0;
            if ((bitCount <= 8 && paletteEntries > (1L << bitCount)) || (bitCount > 8 && colorsUsed != 0))
                throw new PackFormatException("invalid_tray_icon");
            var colorBytes = checked(((checked((long)width * bitCount) + 31) / 32) * 4 * height);
            var maskBytes = checked((((long)width + 31) / 32) * 4 * height);
            var requiredBytes = checked(headerSize + paletteEntries * 4 + colorBytes + maskBytes);
            if (requiredBytes > payload.Length || (imageSize != 0 && imageSize != colorBytes && imageSize != colorBytes + maskBytes))
                throw new PackFormatException("invalid_tray_icon");
            var decodedBytes = checked((long)width * height * 4);
            if (checked(decodedBudget + decodedBytes) > _limits.MaximumDecodedImageBytes)
                throw new PackFormatException("decoded_image_budget");

            // Decode this entry alone, after checking its real header and budget.
            using var icon = new MemoryStream();
            using (var writer = new BinaryWriter(icon, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((byte)(width == 256 ? 0 : width));
                writer.Write((byte)(height == 256 ? 0 : height));
                writer.Write((ushort)0);
                writer.Write(planes);
                writer.Write(bitCount);
                writer.Write(payload.Length);
                writer.Write(22);
                writer.Write(payload);
            }
            icon.Position = 0;
            var decoder = new IconBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
                throw new PackFormatException("invalid_tray_icon");
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(new byte[checked((int)decodedBytes)], checked(width * 4), 0);
            return decodedBytes;
        }
        catch (PackFormatException exception) when (exception.Code != "decoded_image_budget")
        {
            throw new PackFormatException("invalid_tray_icon");
        }
        catch (Exception exception) when (exception is IOException or FormatException or OverflowException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new PackFormatException("invalid_tray_icon");
        }
    }

    private static Dictionary<string, string> ParseMetadata(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("metadata", out var metadataElement))
            return result;
        var metadata = RequireObject(metadataElement, "invalid_metadata");
        var properties = metadata.EnumerateObject().ToArray();
        if (properties.Length > 32)
            throw new PackFormatException("invalid_metadata");
        foreach (var property in properties)
        {
            if (property.Name.Length is < 1 or > 64 || property.Value.ValueKind != JsonValueKind.String)
                throw new PackFormatException("invalid_metadata");
            var value = property.Value.GetString()!;
            if (value.Length > 2000 || value.Any(char.IsControl))
                throw new PackFormatException("invalid_metadata");
            result.Add(property.Name, value);
        }
        return result;
    }

    private bool TryReadPng(
        string path,
        HashSet<string> countedImages,
        ref long decodedBudget,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadPng(stream, path, countedImages, ref decodedBudget, out width, out height);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool TryReadPng(
        Stream stream,
        string imageKey,
        HashSet<string> countedImages,
        ref long decodedBudget,
        out int width,
        out int height,
        int? expectedWidth = null,
        int? expectedHeight = null)
    {
        width = 0;
        height = 0;
        try
        {
            if (stream.Length > _limits.MaximumImageBytes)
                throw new PackFormatException("image_too_large");
            if (stream.Length < 45)
                return false;

            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return false;

            var sawHeader = false;
            var sawData = false;
            var sawEnd = false;
            var chunkHeader = new byte[8];
            var header = new byte[13];
            var crcBytes = new byte[4];
            while (stream.Position < stream.Length)
            {
                stream.ReadExactly(chunkHeader);
                var length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4));
                if (length > int.MaxValue || length > stream.Length - stream.Position - 4)
                    return false;
                var type = Encoding.ASCII.GetString(chunkHeader, 4, 4);
                var chunkData = new byte[(int)length];
                stream.ReadExactly(chunkData);
                stream.ReadExactly(crcBytes);
                var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes.AsSpan());
                if (PngCrc32(chunkHeader.AsSpan(4, 4), chunkData) != expectedCrc)
                    return false;
                if (!sawHeader)
                {
                    if (type != "IHDR" || length != 13)
                        return false;
                    chunkData.CopyTo(header, 0);
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)));
                    if (width is < 1 || height is < 1 ||
                        width > _limits.MaximumImageDimension || height > _limits.MaximumImageDimension ||
                        !IsValidPngHeader(header))
                    {
                        throw new PackFormatException("image_dimensions");
                    }
                    if ((expectedWidth.HasValue && width != expectedWidth.Value) ||
                        (expectedHeight.HasValue && height != expectedHeight.Value))
                        return false;
                    sawHeader = true;
                }
                else
                {
                    if (type == "IDAT")
                        sawData = true;
                    if (type == "IEND")
                    {
                        if (length != 0)
                            return false;
                        sawEnd = true;
                    }
                }
                if (sawEnd)
                    break;
            }

            if (!sawHeader || !sawData || !sawEnd)
                return false;
            var decodedBytesPerPixel = 0;
            if (!TryGetDecodedBytesPerPixel(header, out decodedBytesPerPixel))
                return false;
            var newlyCounted = !countedImages.Contains(imageKey);
            var candidateBudget = decodedBudget;
            if (newlyCounted)
            {
                var decodedBytes = checked((long)width * height * decodedBytesPerPixel);
                if (decodedBytes > _limits.MaximumDecodedImageBytes)
                    throw new PackFormatException("decoded_image_budget");
                candidateBudget = checked(decodedBudget + decodedBytes);
                if (candidateBudget > _limits.MaximumDecodedImageBytes)
                    throw new PackFormatException("decoded_image_budget");
            }

            stream.Position = 0;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 ||
                decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
            {
                return false;
            }
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            converted.CopyPixels(pixels, stride, 0);
            if (newlyCounted)
            {
                countedImages.Add(imageKey);
                decodedBudget = candidateBudget;
            }
            return true;
        }
        catch (PackFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or FormatException or OverflowException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidPngHeader(ReadOnlySpan<byte> header) =>
        TryGetDecodedBytesPerPixel(header, out _);

    private static bool TryGetDecodedBytesPerPixel(ReadOnlySpan<byte> header, out int bytesPerPixel)
    {
        var bitDepth = header[8];
        var colorType = header[9];
        var channels = colorType switch
        {
            0 when bitDepth is 1 or 2 or 4 or 8 or 16 => 1,
            2 when bitDepth is 8 or 16 => 3,
            3 when bitDepth is 1 or 2 or 4 or 8 => 1,
            4 when bitDepth is 8 or 16 => 2,
            6 when bitDepth is 8 or 16 => 4,
            _ => 0
        };
        bytesPerPixel = Math.Max(4, checked((channels * bitDepth + 7) / 8));
        return channels != 0 && header[10] == 0 && header[11] == 0 && header[12] is 0 or 1;
    }

    private static uint PngCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xffffffffu;
        Update(type);
        Update(data);
        return ~crc;

        void Update(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
        }
    }

    private static string[] ReadCandidateArray(
        JsonElement element,
        IReadOnlyDictionary<string, CharacterClip> clips,
        string code)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 8)
            throw new PackFormatException(code);
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new PackFormatException(code);
            var value = item.GetString()!;
            if (!clips.ContainsKey(value))
                throw new PackFormatException("undefined_clip");
            values.Add(value);
        }
        return values.ToArray();
    }

    private static string ResolveRequiredFile(string root, string relativePath)
    {
        var path = ResolveSafePath(root, relativePath);
        if (!File.Exists(path))
            throw new PackFormatException("required_file_missing");
        return path;
    }

    private static JsonDocument ParseJsonFile(string path, long maximumBytes, string code)
    {
        var file = new FileInfo(path);
        if (file.Length > maximumBytes)
            throw new PackFormatException(code);
        var bytes = File.ReadAllBytes(path);
        _ = DecodeStrict(bytes, code);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
        }
        catch (JsonException exception)
        {
            throw new PackFormatException(code, exception);
        }

        try
        {
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new PackFormatException("duplicate_property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static string DecodeStrict(byte[] bytes, string code)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PackFormatException(code, exception);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string code)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PackFormatException(code);
        return element;
    }

    private static JsonElement RequireProperty(JsonElement element, string name, string code)
    {
        if (!element.TryGetProperty(name, out var property))
            throw new PackFormatException(code);
        return property;
    }

    private static string RequireText(JsonElement element, string name, int maximumLength, string code)
    {
        var property = RequireProperty(element, name, code);
        if (property.ValueKind != JsonValueKind.String)
            throw new PackFormatException(code);
        var value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new PackFormatException(code);
        return value;
    }

    private static int RequireInt32(JsonElement element, string name, string code)
    {
        var property = RequireProperty(element, name, code);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw new PackFormatException(code);
        return value;
    }

    private static double RequireFiniteDouble(JsonElement element, string name, string code)
    {
        var property = RequireProperty(element, name, code);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new PackFormatException(code);
        return value;
    }

    private static string[] RequireStringArray(
        JsonElement element,
        string name,
        int maximumCount,
        int maximumLength,
        bool allowEmpty,
        string code)
    {
        var property = RequireProperty(element, name, code);
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maximumCount ||
            (!allowEmpty && property.GetArrayLength() == 0))
        {
            throw new PackFormatException(code);
        }
        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new PackFormatException(code);
            var value = item.GetString()!;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
                throw new PackFormatException(code);
            values.Add(value);
        }
        return values.ToArray();
    }

    private static void RequireOnlyProperties(JsonElement element, IEnumerable<string> allowed, string code)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        if (element.EnumerateObject().Any(property => !names.Contains(property.Name)))
            throw new PackFormatException(code);
    }

    private static void AddWarning(List<string> warnings, string code)
    {
        if (warnings.Count < MaximumDiagnostics && !warnings.Contains(code, StringComparer.Ordinal))
            warnings.Add(code);
    }

    internal sealed class PackFormatException : Exception
    {
        public PackFormatException(string code, Exception? innerException = null)
            : base(code, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
