using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using PetGPT.Personas;

namespace PetGPT.Characters;

public enum CharacterPackSource
{
    Bundled,
    Installed
}

public sealed class CharacterPack
{
    internal CharacterPack(
        string rootPath,
        CharacterPackSource source,
        string id,
        string displayName,
        string version,
        string minAppVersion,
        PersonaProfile? persona,
        CharacterPresentation presentation,
        IDictionary<string, CharacterClip> clips,
        IDictionary<string, IReadOnlyList<string>> systemAnimations,
        IDictionary<string, CharacterReaction> reactions,
        ThemeTokens? theme,
        string? trayIconPath,
        IDictionary<string, string> metadata,
        IEnumerable<string> warnings)
    {
        RootPath = Path.GetFullPath(rootPath);
        Source = source;
        Id = id;
        DisplayName = displayName;
        Version = version;
        MinAppVersion = minAppVersion;
        Persona = persona;
        Presentation = presentation;
        Clips = Freeze(clips);
        SystemAnimations = Freeze(systemAnimations.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray()),
            StringComparer.Ordinal));
        Reactions = Freeze(reactions);
        Theme = theme;
        TrayIconPath = trayIconPath;
        Metadata = Freeze(metadata);
        WarningCodes = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).Take(32).ToArray());
    }

    public string RootPath { get; }
    public CharacterPackSource Source { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string MinAppVersion { get; }
    public PersonaProfile? Persona { get; }
    public CharacterPresentation Presentation { get; }
    public IReadOnlyDictionary<string, CharacterClip> Clips { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SystemAnimations { get; }
    public IReadOnlyDictionary<string, CharacterReaction> Reactions { get; }
    public ThemeTokens? Theme { get; }
    public string? TrayIconPath { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public IReadOnlyList<string> WarningCodes { get; }

    private static IReadOnlyDictionary<string, TValue> Freeze<TValue>(IDictionary<string, TValue> source) =>
        new ReadOnlyDictionary<string, TValue>(new Dictionary<string, TValue>(source, StringComparer.Ordinal));
}

public sealed record CharacterPresentation(double WidthDip, double HeightDip, double AnchorX, double AnchorY);

public sealed record CharacterClip(
    string Id,
    string Format,
    string AssetPath,
    string Playback,
    bool IsAvailable,
    int PixelWidth,
    int PixelHeight,
    int? FrameWidth,
    int? FrameHeight,
    int? Columns,
    int? FrameCount,
    int? Fps);

public sealed class CharacterReaction
{
    internal CharacterReaction(
        string id,
        string meaning,
        IEnumerable<string> animationCandidates,
        int defaultIntensity,
        int visibleMs,
        IEnumerable<ReactionIntensityBand> intensityBands)
    {
        Id = id;
        Meaning = meaning;
        AnimationCandidates = Array.AsReadOnly(animationCandidates.ToArray());
        DefaultIntensity = defaultIntensity;
        VisibleMs = visibleMs;
        IntensityBands = Array.AsReadOnly(intensityBands.ToArray());
    }

    public string Id { get; }
    public string Meaning { get; }
    public IReadOnlyList<string> AnimationCandidates { get; }
    public int DefaultIntensity { get; }
    public int VisibleMs { get; }
    public IReadOnlyList<ReactionIntensityBand> IntensityBands { get; }
}

public sealed class ReactionIntensityBand
{
    internal ReactionIntensityBand(int minimum, IEnumerable<string> animationCandidates)
    {
        Minimum = minimum;
        AnimationCandidates = Array.AsReadOnly(animationCandidates.ToArray());
    }

    public int Minimum { get; }
    public IReadOnlyList<string> AnimationCandidates { get; }
}

public sealed class ThemeTokens
{
    internal ThemeTokens(
        string sourcePath,
        IDictionary<string, string> colors,
        IDictionary<string, int> metrics,
        ThemeDecoration? decoration)
    {
        SourcePath = sourcePath;
        Colors = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(colors, StringComparer.Ordinal));
        Metrics = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(metrics, StringComparer.Ordinal));
        Decoration = decoration;
    }

    public string SourcePath { get; }
    public IReadOnlyDictionary<string, string> Colors { get; }
    public IReadOnlyDictionary<string, int> Metrics { get; }
    public ThemeDecoration? Decoration { get; }
}

public sealed record ThemeDecoration(string AssetPath, int OpacityPercent, string Placement);

public sealed class PackValidationResult
{
    internal PackValidationResult(CharacterPack? pack, IEnumerable<string> errors, IEnumerable<string> warnings)
    {
        Pack = pack;
        DiagnosticCodes = Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).Take(32).ToArray());
        WarningCodes = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).Take(32).ToArray());
    }

    public CharacterPack? Pack { get; }
    public bool IsValid => Pack is not null && DiagnosticCodes.Count == 0;
    public IReadOnlyList<string> DiagnosticCodes { get; }
    public IReadOnlyList<string> WarningCodes { get; }

    internal static PackValidationResult Invalid(params string[] codes) => new(null, codes, []);
}

public sealed record PackValidationLimits
{
    public const long ArchiveBytes = 50L * 1024 * 1024;
    public const long ExpandedBytes = 100L * 1024 * 1024;
    public const int FileCount = 1_000;
    public const long ImageBytes = 8L * 1024 * 1024;
    public const int ImageDimension = 4_096;
    public const long DecodedImageBytes = 64L * 1024 * 1024;
    public const long ManifestBytes = 64L * 1024;
    public const long PersonaBytes = 32L * 1024;
    public const long ThemeBytes = 8L * 1024;
    public const long DecorationBytes = 2L * 1024 * 1024;

    public static PackValidationLimits Default { get; } = new();

    public long MaximumArchiveBytes { get; init; } = ArchiveBytes;
    public long MaximumExpandedBytes { get; init; } = ExpandedBytes;
    public int MaximumFiles { get; init; } = FileCount;
    public long MaximumImageBytes { get; init; } = ImageBytes;
    public int MaximumImageDimension { get; init; } = ImageDimension;
    public long MaximumDecodedImageBytes { get; init; } = DecodedImageBytes;
    public long MaximumManifestBytes { get; init; } = ManifestBytes;
    public long MaximumPersonaBytes { get; init; } = PersonaBytes;
    public long MaximumThemeBytes { get; init; } = ThemeBytes;
    public long MaximumDecorationBytes { get; init; } = DecorationBytes;
}

public readonly struct SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private SemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, string[] prerelease, string original)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = Array.AsReadOnly(prerelease.ToArray());
        Original = original;
    }

    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public string Original { get; }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (value is null || value.Length > 128)
            return false;

        var match = Pattern.Match(value);
        if (!match.Success ||
            !BigInteger.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !BigInteger.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !BigInteger.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups[4].Success
            ? match.Groups[4].Value.Split('.', StringSplitOptions.None)
            : [];
        if (prerelease.Any(identifier =>
                identifier.Length == 0 ||
                (identifier.All(char.IsAsciiDigit) && identifier.Length > 1 && identifier[0] == '0')))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease, value);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
            return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0)
            return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0)
            return result;

        if (Prerelease.Count == 0)
            return other.Prerelease.Count == 0 ? 0 : 1;
        if (other.Prerelease.Count == 0)
            return -1;

        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = BigInteger.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = BigInteger.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            result = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : string.CompareOrdinal(left, right);
            if (result != 0)
                return result;
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString() => Original ?? string.Empty;
}
