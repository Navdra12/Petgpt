using System.Text.RegularExpressions;
using PetGPT.Characters;
using PetGPT.Models;

namespace PetGPT.Shell;

public enum LocalCommandKind
{
    OpenPetChooser,
    ListPets,
    SelectPet,
    Sleep,
    Wake,
    OpenThemeSettings,
    History,
    NewPetChat
}

public sealed record LocalCommandIntent(LocalCommandKind Kind, string? PetId = null);

public sealed record LocalCommandParseResult(
    LocalCommandIntent? Intent,
    string Message)
{
    public bool Succeeded => Intent is not null;

    // The local command grammar intentionally has no remote-fallback state.
    public bool SubmitToChatGpt => false;
}

public sealed record LocalCommandExecutionResult(bool Succeeded, string Message)
{
    public bool SubmitToChatGpt => false;
}

public sealed record LocalCommandActions(
    Func<Task> OpenPetChooserAsync,
    Func<IReadOnlyList<CharacterPack>> GetInstalledPacks,
    Func<string, CancellationToken, Task<PetSelectionResult>> SelectPetByIdAsync,
    Action<PetEvent> RaisePetEvent,
    Func<Task> OpenThemeSettingsAsync,
    Func<NavigationIntent, CancellationToken, Task<NavigationResult>> NavigateAsync);

public sealed partial class LocalCommandRouter
{
    public const int MaximumInputLength = 128;
    private readonly LocalCommandActions _actions;

    public LocalCommandRouter(LocalCommandActions actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public static LocalCommandParseResult Parse(string? input)
    {
        if (input is null || input.Length > MaximumInputLength)
            return Error("Enter a PetGPT command of at most 128 characters.");
        if (input.IndexOfAny(['\r', '\n']) >= 0)
            return Error("Commands must be entered on one line.");

        var value = input.Trim();
        if (value.Length == 0)
            return Error(HelpText);
        if (ContainsShellSyntax(value))
            return Error("Shell syntax, paths, quoting, and command chaining are not supported.");

        if (value.Equals("/theme", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.OpenThemeSettings);
        if (value.Equals("/history", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.History);
        if (value.Equals("/new", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.NewPetChat);
        if (value.Equals("/pet", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.OpenPetChooser);

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("/pet", StringComparison.OrdinalIgnoreCase))
            return Error(HelpText);

        var argument = parts[1];
        if (argument.Equals("list", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.ListPets);
        if (argument.Equals("sleep", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.Sleep);
        if (argument.Equals("wake", StringComparison.OrdinalIgnoreCase))
            return Success(LocalCommandKind.Wake);

        var canonicalId = argument.ToLowerInvariant();
        return PackIdPattern().IsMatch(canonicalId)
            ? new LocalCommandParseResult(
                new LocalCommandIntent(LocalCommandKind.SelectPet, canonicalId),
                string.Empty)
            : Error("Character IDs use lowercase letters, numbers, and underscores only.");
    }

    public async Task<LocalCommandExecutionResult> ExecuteAsync(
        string? input,
        CancellationToken cancellationToken = default)
    {
        var parsed = Parse(input);
        if (!parsed.Succeeded || parsed.Intent is null)
            return new LocalCommandExecutionResult(false, parsed.Message);

        switch (parsed.Intent.Kind)
        {
            case LocalCommandKind.OpenPetChooser:
                await _actions.OpenPetChooserAsync();
                return Ok("Character chooser opened.");
            case LocalCommandKind.ListPets:
                return Ok(FormatPackList(_actions.GetInstalledPacks()));
            case LocalCommandKind.SelectPet:
                var selection = await _actions.SelectPetByIdAsync(parsed.Intent.PetId!, cancellationToken);
                return selection.Succeeded
                    ? Ok(selection.Changed
                        ? $"Selected {parsed.Intent.PetId}."
                        : $"{parsed.Intent.PetId} is already selected.")
                    : new LocalCommandExecutionResult(
                        false,
                        selection.DiagnosticCode == "selection_ambiguous"
                            ? "Multiple installed versions match. Choose an exact version in Settings."
                            : "That installed character is unavailable.");
            case LocalCommandKind.Sleep:
                _actions.RaisePetEvent(new PetEvent.SleepChanged(true));
                return Ok("Pet is sleeping.");
            case LocalCommandKind.Wake:
                _actions.RaisePetEvent(new PetEvent.SleepChanged(false));
                return Ok("Pet is awake.");
            case LocalCommandKind.OpenThemeSettings:
                await _actions.OpenThemeSettingsAsync();
                return Ok("Appearance settings opened.");
            case LocalCommandKind.History:
                return NavigationExecutionResult(await _actions.NavigateAsync(
                    NavigationIntent.History,
                    cancellationToken));
            case LocalCommandKind.NewPetChat:
                return NavigationExecutionResult(await _actions.NavigateAsync(
                    NavigationIntent.NewPetChat,
                    cancellationToken));
            default:
                return new LocalCommandExecutionResult(false, HelpText);
        }
    }

    public const string HelpText =
        "Commands: /pet, /pet list, /pet <id>, /pet sleep, /pet wake, /theme, /history, /new";

    private static LocalCommandParseResult Success(LocalCommandKind kind) =>
        new(new LocalCommandIntent(kind), string.Empty);

    private static LocalCommandParseResult Error(string message) => new(null, message);

    private static LocalCommandExecutionResult Ok(string message) => new(true, message);

    private static LocalCommandExecutionResult NavigationExecutionResult(PetGPT.Models.NavigationResult result) =>
        result switch
        {
            PetGPT.Models.NavigationResult.Navigated or PetGPT.Models.NavigationResult.AlreadyAtTarget =>
                Ok("PetChats navigation opened."),
            PetGPT.Models.NavigationResult.Stayed =>
                new LocalCommandExecutionResult(false, "Stayed in the current chat."),
            PetGPT.Models.NavigationResult.InvalidHome =>
                new LocalCommandExecutionResult(false, "PetChats is not configured."),
            _ => new LocalCommandExecutionResult(false, "PetChats navigation is unavailable.")
        };

    private static string FormatPackList(IReadOnlyList<CharacterPack> packs)
    {
        if (packs.Count == 0)
            return "No valid character packs are installed.";

        return string.Join(
            Environment.NewLine,
            packs
                .OrderBy(pack => pack.Id, StringComparer.Ordinal)
                .ThenBy(pack => pack.Version, StringComparer.Ordinal)
                .Select(pack => $"{pack.Id}@{pack.Version} — {pack.DisplayName}"));
    }

    private static bool ContainsShellSyntax(string value) =>
        value.IndexOfAny(['|', '&', ';', '$', '`', '\'', '"']) >= 0 ||
        value.Contains("../", StringComparison.Ordinal) ||
        value.Contains("..\\", StringComparison.Ordinal) ||
        value.Contains('/') && !value.StartsWith("/", StringComparison.Ordinal);

    [GeneratedRegex("\\A[a-z][a-z0-9_]{0,31}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PackIdPattern();
}
