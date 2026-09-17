using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PetGPT.Tests")]

namespace PetGPT.Models;

public enum SettingsLoadSource
{
    V2,
    LastGoodBackup,
    LegacyV0,
    Defaults
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadSource Source,
    int? SourceVersion,
    bool IsReadOnlyRecovery,
    IReadOnlyList<string> DiagnosticCodes);

public readonly record struct ScreenPointPx(double X, double Y);

public readonly record struct ScreenRectPx(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public ScreenPointPx Center => new(Left + (Width / 2), Top + (Height / 2));
}

public readonly record struct SizeDip(double Width, double Height);

public sealed record MonitorInfo(
    string Id,
    ScreenRectPx BoundsPx,
    ScreenRectPx WorkAreaPx,
    double DpiX,
    double DpiY,
    bool IsPrimary);

public sealed record WindowPlacement(
    string? MonitorId,
    double? XWithinWorkAreaDip,
    double? YWithinWorkAreaDip);
