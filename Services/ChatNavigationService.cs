using PetGPT.Models;

namespace PetGPT.Services;

public static class ChatNavigationUrlPolicy
{
    public const int MaximumUrlLength = 2048;
    public static readonly Uri RootUri = new("https://chatgpt.com/");

    public static bool TryValidateSafeUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumUrlLength ||
            value.Any(char.IsControl) || HasMalformedPercentEncoding(value) ||
            Uri.UnescapeDataString(value).Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !IsExactChatGptOrigin(candidate) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public static bool TryValidateHome(string? value, out Uri uri)
    {
        uri = null!;
        if (!TryValidateSafeUrl(value, out var candidate) ||
            Classify(candidate) != ChatRouteKind.ProjectLanding)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public static Uri ResolveInitialNavigation(string? configuredHome) =>
        TryValidateHome(configuredHome, out var home) ? home : RootUri;

    public static Uri ResolveExternalBrowserUri(Uri? current, string? configuredHome)
    {
        if (current is not null && TryValidateSafeUrl(current.OriginalString, out var safeCurrent))
            return safeCurrent;
        return TryValidateHome(configuredHome, out var home) ? home : RootUri;
    }

    public static ChatRouteKind Classify(Uri? uri)
    {
        if (uri is null || !IsExactChatGptOrigin(uri))
            return ChatRouteKind.Unrelated;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return ChatRouteKind.ChatRoot;
        if (segments[0].Equals("login", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("auth", StringComparison.OrdinalIgnoreCase) ||
            (segments.Length >= 2 &&
             segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
             segments[1].Equals("auth", StringComparison.OrdinalIgnoreCase)))
        {
            return ChatRouteKind.Authentication;
        }

        if (segments.Length == 3 &&
            segments[0].Equals("g", StringComparison.Ordinal) &&
            segments[2].Equals("project", StringComparison.Ordinal))
        {
            return ChatRouteKind.ProjectLanding;
        }

        if (segments.Length == 4 &&
            segments[0].Equals("g", StringComparison.Ordinal) &&
            segments[2].Equals("c", StringComparison.Ordinal))
        {
            return ChatRouteKind.ProjectConversation;
        }

        return ChatRouteKind.UnknownChatGpt;
    }

    public static bool IsExactChatGptOrigin(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsEnhancementEligible(Uri? uri)
    {
        var route = Classify(uri);
        return IsExactChatGptOrigin(uri) &&
            route is not ChatRouteKind.Authentication and not ChatRouteKind.Unrelated;
    }

    private static bool HasMalformedPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return true;
            }
            index += 2;
        }

        return false;
    }
}

internal sealed class ChatWebViewNavigationState
{
    public ChatWebViewNavigationState(string? configuredHome)
    {
        InitialNavigationUri = ChatNavigationUrlPolicy.ResolveInitialNavigation(configuredHome);
    }

    public Uri InitialNavigationUri { get; }
    public Uri? CurrentUri { get; private set; }
    public Uri? ReloadUri => CurrentUri;

    public void ObserveSource(Uri? source) => CurrentUri = source;

    public void OnVisibilityChanged(bool visible)
    {
        _ = visible;
    }
}

public sealed class ChatNavigationService
{
    private readonly Uri? _homeUri;
    private readonly Func<Uri?> _getCurrentUri;
    private readonly Action<Uri> _navigate;
    private readonly Func<CancellationToken, Task<bool>> _confirmLeaveAsync;
    private GenerationCapabilityState _generation = GenerationCapabilityState.Unknown;
    private ComposerDraftState _draft = ComposerDraftState.Unknown;

    public ChatNavigationService(
        string? configuredHome,
        Func<Uri?> getCurrentUri,
        Action<Uri> navigate,
        Func<CancellationToken, Task<bool>> confirmLeaveAsync)
    {
        _getCurrentUri = getCurrentUri ?? throw new ArgumentNullException(nameof(getCurrentUri));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _confirmLeaveAsync = confirmLeaveAsync ?? throw new ArgumentNullException(nameof(confirmLeaveAsync));
        if (ChatNavigationUrlPolicy.TryValidateHome(configuredHome, out var home))
            _homeUri = home;
    }

    public bool HasConfiguredHome => _homeUri is not null;
    public Uri InitialNavigationUri => _homeUri ?? ChatNavigationUrlPolicy.RootUri;
    public ChatRouteKind CurrentRoute { get; private set; } = ChatRouteKind.Unrelated;
    public bool HistoryMode { get; private set; }

    public void ObserveSource(Uri? source)
    {
        CurrentRoute = ChatNavigationUrlPolicy.Classify(source);
        if (CurrentRoute != ChatRouteKind.ProjectLanding)
            HistoryMode = false;
    }

    public void UpdateGuardState(
        GenerationCapabilityState generation,
        ComposerDraftState draft)
    {
        _generation = generation;
        _draft = draft;
    }

    public async Task<NavigationResult> NavigateAsync(
        NavigationIntent intent,
        CancellationToken cancellationToken)
    {
        Uri target;
        switch (intent)
        {
            case NavigationIntent.Home:
                target = _homeUri ?? ChatNavigationUrlPolicy.RootUri;
                break;
            case NavigationIntent.NewPetChat:
            case NavigationIntent.History:
                if (_homeUri is null)
                    return NavigationResult.InvalidHome;
                target = _homeUri;
                break;
            default:
                return NavigationResult.Unavailable;
        }

        var current = _getCurrentUri();
        if (current is not null && Uri.Compare(
                current,
                target,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            HistoryMode = intent == NavigationIntent.History;
            return NavigationResult.AlreadyAtTarget;
        }

        if (intent is NavigationIntent.NewPetChat or NavigationIntent.History &&
            RequiresLeaveConfirmation(_generation, _draft) &&
            !await _confirmLeaveAsync(cancellationToken))
        {
            return NavigationResult.Stayed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        HistoryMode = intent == NavigationIntent.History;
        _navigate(target);
        return NavigationResult.Navigated;
    }

    public Uri ResolveExternalBrowserUri() =>
        ChatNavigationUrlPolicy.ResolveExternalBrowserUri(_getCurrentUri(), _homeUri?.AbsoluteUri);

    public static bool RequiresLeaveConfirmation(
        GenerationCapabilityState generation,
        ComposerDraftState draft) =>
        generation == GenerationCapabilityState.Generating ||
        draft != ComposerDraftState.Empty;
}
