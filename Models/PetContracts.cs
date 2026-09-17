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
