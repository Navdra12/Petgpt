using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using PetGPT.Characters;
using PetGPT.Models;

namespace PetGPT.Services;

public sealed record ThemeSelectorCapabilities(
    bool PageSurfacePair,
    bool SecondarySurfacePair,
    bool ComposerPair,
    bool Scrollbar)
{
    public static ThemeSelectorCapabilities All { get; } = new(true, true, true, true);
}

public sealed record ThemeLayer(string Css, string? DecorationDataUri);

public sealed class ThemeService
{
    private static readonly Regex ColorPattern = new(
        "\\A#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] RequiredColors =
    [
        "background", "surface", "text", "mutedText", "accent", "border",
        "composerBackground", "composerText", "scrollbarThumb"
    ];
    private static readonly string[] RequiredMetrics =
    [
        "surfaceRadiusPx", "composerRadiusPx", "borderWidthPx", "scrollbarWidthPx"
    ];

    public ThemeLayer? BuildLayer(
        ThemeTokens? tokens,
        bool enabled,
        ThemeSelectorCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!enabled || tokens is null || !IsValid(tokens))
            return null;

        var css = new StringBuilder();
        string? decoration = null;
        if (capabilities.PageSurfacePair)
        {
            css.Append(":root{--petgpt-page-background:")
                .Append(tokens.Colors["background"])
                .Append(";--petgpt-page-text:")
                .Append(tokens.Colors["text"])
                .Append(";}")
                .Append("[data-petgpt-surface=\"page\"]{background-color:var(--petgpt-page-background)!important;color:var(--petgpt-page-text)!important;}");
            decoration = TryCreateDecoration(tokens.Decoration);
            if (decoration is not null)
            {
                var placement = tokens.Decoration!.Placement == "corner"
                    ? "right bottom/auto no-repeat"
                    : "center/cover no-repeat";
                css.Append("[data-petgpt-surface=\"page\"]::before{content:\"\";position:fixed;inset:0;pointer-events:none;z-index:0;opacity:")
                    .Append((tokens.Decoration.OpacityPercent / 100d).ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(";background:url(\"")
                    .Append(decoration)
                    .Append("\") ")
                    .Append(placement)
                    .Append(";}");
            }
        }

        if (capabilities.SecondarySurfacePair)
        {
            css.Append(":root{--petgpt-surface-background:")
                .Append(tokens.Colors["surface"])
                .Append(";--petgpt-surface-text:")
                .Append(tokens.Colors["mutedText"])
                .Append(";--petgpt-surface-radius:")
                .Append(tokens.Metrics["surfaceRadiusPx"])
                .Append("px;}")
                .Append("[data-petgpt-surface=\"secondary\"]{background-color:var(--petgpt-surface-background)!important;color:var(--petgpt-surface-text)!important;border-radius:var(--petgpt-surface-radius)!important;}");
        }

        if (capabilities.ComposerPair)
        {
            css.Append(":root{--petgpt-composer-background:")
                .Append(tokens.Colors["composerBackground"])
                .Append(";--petgpt-composer-text:")
                .Append(tokens.Colors["composerText"])
                .Append(";--petgpt-accent:")
                .Append(tokens.Colors["accent"])
                .Append(";--petgpt-border:")
                .Append(tokens.Colors["border"])
                .Append(";--petgpt-composer-radius:")
                .Append(tokens.Metrics["composerRadiusPx"])
                .Append("px;--petgpt-border-width:")
                .Append(tokens.Metrics["borderWidthPx"])
                .Append("px;}")
                .Append("[data-petgpt-surface=\"composer\"]{background-color:var(--petgpt-composer-background)!important;color:var(--petgpt-composer-text)!important;border:var(--petgpt-border-width) solid var(--petgpt-border)!important;border-radius:var(--petgpt-composer-radius)!important;}")
                .Append("[data-petgpt-surface=\"composer\"]:focus-within{outline:2px solid var(--petgpt-accent)!important;}");
        }

        if (capabilities.Scrollbar)
        {
            css.Append(":root{--petgpt-scrollbar-thumb:")
                .Append(tokens.Colors["scrollbarThumb"])
                .Append(";--petgpt-scrollbar-width:")
                .Append(tokens.Metrics["scrollbarWidthPx"])
                .Append("px;}html *{scrollbar-color:var(--petgpt-scrollbar-thumb) transparent;scrollbar-width:thin;}html *::-webkit-scrollbar{width:var(--petgpt-scrollbar-width);}html *::-webkit-scrollbar-thumb{background:var(--petgpt-scrollbar-thumb);}");
        }

        return css.Length == 0 ? null : new ThemeLayer(css.ToString(), decoration);
    }

    private static bool IsValid(ThemeTokens tokens)
    {
        if (tokens.Colors.Count != RequiredColors.Length ||
            tokens.Metrics.Count != RequiredMetrics.Length ||
            RequiredColors.Any(name => !tokens.Colors.TryGetValue(name, out var value) || !ColorPattern.IsMatch(value)) ||
            RequiredMetrics.Any(name => !tokens.Metrics.ContainsKey(name)))
        {
            return false;
        }

        return tokens.Metrics["surfaceRadiusPx"] is >= 0 and <= 24 &&
            tokens.Metrics["composerRadiusPx"] is >= 0 and <= 24 &&
            tokens.Metrics["borderWidthPx"] is >= 0 and <= 4 &&
            tokens.Metrics["scrollbarWidthPx"] is >= 6 and <= 20 &&
            (tokens.Decoration is null ||
             tokens.Decoration.OpacityPercent is >= 0 and <= 30 &&
             tokens.Decoration.Placement is "background" or "corner");
    }

    private static string? TryCreateDecoration(ThemeDecoration? decoration)
    {
        if (decoration is null)
            return null;

        try
        {
            var info = new FileInfo(decoration.AssetPath);
            if (!info.Exists || info.Length <= 0 || info.Length > PackValidationLimits.DecorationBytes)
                return null;

            using var input = new FileStream(decoration.AssetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length <= 0 || output.Length > PackValidationLimits.DecorationBytes)
                return null;
            return "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed record CompactAppearance(bool Enabled, bool SuppressNavigation);

public static class CompactAppearancePolicy
{
    public static CompactAppearance Evaluate(
        bool enabled,
        ChatRouteKind route,
        bool historyMode,
        bool navigationSelectorAvailable) =>
        new(
            enabled,
            enabled &&
            route == ChatRouteKind.ProjectConversation &&
            !historyMode &&
            navigationSelectorAvailable);
}

public enum AppearanceLayerKind
{
    Compact,
    Theme
}

public enum AppearanceOperation
{
    Apply,
    Remove
}

public sealed record AppearanceLayerOperation(
    AppearanceLayerKind Layer,
    AppearanceOperation Operation,
    string? Css);

public sealed class AppearanceLayerState
{
    public string? CompactCss { get; private set; }
    public string? ThemeCss { get; private set; }

    public IReadOnlyList<AppearanceLayerOperation> Apply(string? compactCss, ThemeLayer? theme)
    {
        var operations = new List<AppearanceLayerOperation>(2);
        AddOperation(AppearanceLayerKind.Compact, CompactCss, compactCss, operations);
        AddOperation(AppearanceLayerKind.Theme, ThemeCss, theme?.Css, operations);
        CompactCss = compactCss;
        ThemeCss = theme?.Css;
        return operations;
    }

    private static void AddOperation(
        AppearanceLayerKind layer,
        string? previous,
        string? next,
        ICollection<AppearanceLayerOperation> operations)
    {
        if (string.Equals(previous, next, StringComparison.Ordinal))
            return;
        operations.Add(new AppearanceLayerOperation(
            layer,
            next is null ? AppearanceOperation.Remove : AppearanceOperation.Apply,
            next));
    }
}

public sealed class AppearanceController
{
    private readonly Action<AppearanceLayerOperation> _apply;

    public AppearanceController(
        Action<AppearanceLayerOperation> apply,
        Action navigate,
        Action reload)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        ArgumentNullException.ThrowIfNull(navigate);
        ArgumentNullException.ThrowIfNull(reload);
    }

    public AppearanceLayerState State { get; } = new();

    public void UpdateTheme(ThemeLayer? theme)
    {
        foreach (var operation in State.Apply(State.CompactCss, theme))
            _apply(operation);
    }
}
