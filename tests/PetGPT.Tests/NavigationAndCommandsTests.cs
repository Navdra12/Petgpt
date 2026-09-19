using System.Reflection;
using System.Text;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class NavigationAndCommandsTests
{
    private static readonly Uri Root = new("https://chatgpt.com/");
    private static readonly Uri ProjectHome = new("https://chatgpt.com/g/opaque-project/project");
    private static readonly Uri ProjectConversation = new("https://chatgpt.com/g/opaque-project/c/opaque-chat");

    [Theory]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://chatgpt.com/g/opaque-project/project")]
    [InlineData("https://chatgpt.com/g/a%2Db/project")]
    public void SafeChatGptUrl_AcceptsExactHttpsOriginWithoutUnsafeComponents(string value)
    {
        Assert.True(ChatNavigationUrlPolicy.TryValidateSafeUrl(value, out var uri));
        Assert.Equal("chatgpt.com", uri.Host);
    }

    [Fact]
    public void ConfiguredHome_AcceptsOnlyOpaqueProjectLanding()
    {
        Assert.True(ChatNavigationUrlPolicy.TryValidateHome(ProjectHome.AbsoluteUri, out var home));
        Assert.Equal(ProjectHome, home);
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(ProjectConversation.AbsoluteUri, out _));
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(Root.AbsoluteUri, out _));
    }

    [Theory]
    [InlineData("http://chatgpt.com/g/x/project")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,x")]
    [InlineData("file:///c:/temp/x")]
    [InlineData("https://chatgpt.com.evil.example/g/x/project")]
    [InlineData("https://evil-chatgpt.com/g/x/project")]
    [InlineData("https://www.chatgpt.com/g/x/project")]
    [InlineData("https://sub.chatgpt.com/g/x/project")]
    [InlineData("https://chatgpt.com:444/g/x/project")]
    [InlineData("https://user:password@chatgpt.com/g/x/project")]
    [InlineData("https://chatgpt.com/g/x/project?mode=history")]
    [InlineData("https://chatgpt.com/g/x/project#fragment")]
    [InlineData("https://chatgpt.com/auth/login")]
    [InlineData("https://chatgpt.com/login")]
    [InlineData("https://chatgpt.com/share/opaque")]
    [InlineData("https://chatgpt.com/c/opaque")]
    [InlineData("https://chatgpt.com/g/x/c/y")]
    [InlineData("https://chatgpt.com/g/x/files")]
    public void ConfiguredHome_RejectsUnsafeOrUnsupportedRoutes(string value)
    {
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Theory]
    [InlineData("https://chatgpt.com/g/%/project")]
    [InlineData("https://chatgpt.com/g/%0/project")]
    [InlineData("https://chatgpt.com/g/%GG/project")]
    [InlineData("https://chatgpt.com/g/%0A/project")]
    [InlineData("https://chatgpt.com/g/%7F/project")]
    [InlineData("https://chatgpt.com/g/x/pro\u0001ject")]
    public void ConfiguredHome_RejectsMalformedPercentEncodingAndControls(string value)
    {
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Fact]
    public void ConfiguredHome_RejectsOverlongUrl()
    {
        var value = "https://chatgpt.com/g/" + new string('a', 2048) + "/project";

        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://www.chatgpt.com/g/x/project")]
    public void InitialNavigation_UnconfiguredOrInvalidFallsBackToRoot(string? value)
    {
        Assert.Equal(Root, ChatNavigationUrlPolicy.ResolveInitialNavigation(value));
    }

    [Fact]
    public void InitialNavigation_UsesConfiguredHomeAsTheOnlyFirstTarget()
    {
        var state = new ChatWebViewNavigationState(ProjectHome.AbsoluteUri);

        Assert.Equal(ProjectHome, state.InitialNavigationUri);
        Assert.NotEqual(Root, state.InitialNavigationUri);
        Assert.Null(state.CurrentUri);
    }

    [Theory]
    [InlineData("https://chatgpt.com/", ChatRouteKind.ChatRoot)]
    [InlineData("https://chatgpt.com/g/x/project", ChatRouteKind.ProjectLanding)]
    [InlineData("https://chatgpt.com/g/x/c/y", ChatRouteKind.ProjectConversation)]
    [InlineData("https://chatgpt.com/auth/login", ChatRouteKind.Authentication)]
    [InlineData("https://chatgpt.com/login", ChatRouteKind.Authentication)]
    [InlineData("https://example.com/", ChatRouteKind.Unrelated)]
    [InlineData("https://chatgpt.com/share/x", ChatRouteKind.UnknownChatGpt)]
    [InlineData("https://chatgpt.com/g/x/files", ChatRouteKind.UnknownChatGpt)]
    public void RouteClassification_IsTypedAndConservative(string value, ChatRouteKind expected)
    {
        Assert.Equal(expected, ChatNavigationUrlPolicy.Classify(new Uri(value)));
    }

    [Fact]
    public void ShowHideAndReloadState_PreserveObservedRoute()
    {
        var state = new ChatWebViewNavigationState(ProjectHome.AbsoluteUri);
        state.ObserveSource(ProjectConversation);

        state.OnVisibilityChanged(false);
        state.OnVisibilityChanged(true);

        Assert.Equal(ProjectConversation, state.CurrentUri);
        Assert.Equal(ProjectConversation, state.ReloadUri);
    }

    [Fact]
    public async Task NewPetChat_NavigatesDirectlyToExactConfiguredHome()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        harness.Service.UpdateGuardState(GenerationCapabilityState.Idle, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Equal([ProjectHome], harness.Navigations);
        Assert.False(harness.Service.HistoryMode);
        Assert.Equal(0, harness.GlobalNewChatCalls);
    }

    [Fact]
    public async Task NewPetChat_WhenUnconfiguredDoesNotFakeGlobalCreation()
    {
        var harness = new NavigationHarness(null, Root);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.InvalidHome, result);
        Assert.Empty(harness.Navigations);
        Assert.Equal(0, harness.GlobalNewChatCalls);
    }

    [Fact]
    public async Task History_NavigatesToProjectHomeAndSetsHistoryIntent()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        harness.Service.UpdateGuardState(GenerationCapabilityState.Idle, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Equal([ProjectHome], harness.Navigations);
        Assert.True(harness.Service.HistoryMode);
    }

    [Fact]
    public async Task InvalidHome_CannotNavigateToArbitraryUrl()
    {
        var harness = new NavigationHarness("https://example.com/project", ProjectConversation);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.InvalidHome, result);
        Assert.Empty(harness.Navigations);
    }

    [Theory]
    [InlineData(GenerationCapabilityState.Generating, ComposerDraftState.Empty, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.NonEmpty, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.Unknown, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.Empty, false)]
    [InlineData(GenerationCapabilityState.Unknown, ComposerDraftState.Empty, false)]
    public void LeaveGuard_UsesPositiveGenerationAndConservativeDraftState(
        GenerationCapabilityState generation,
        ComposerDraftState draft,
        bool expected)
    {
        Assert.Equal(expected, ChatNavigationService.RequiresLeaveConfirmation(generation, draft));
    }

    [Fact]
    public async Task LeaveGuard_UserChoosesStayAndNavigationDoesNotOccur()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation)
        {
            ConfirmLeave = false
        };
        harness.Service.UpdateGuardState(GenerationCapabilityState.Generating, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.Stayed, result);
        Assert.Empty(harness.Navigations);
        Assert.Equal(1, harness.PromptCalls);
    }

    [Fact]
    public async Task LeaveGuard_UserChoosesLeaveAndNavigationOccursOnce()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation)
        {
            ConfirmLeave = true
        };
        harness.Service.UpdateGuardState(GenerationCapabilityState.Generating, ComposerDraftState.Unknown);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Single(harness.Navigations);
        Assert.Equal(1, harness.PromptCalls);
    }

    [Theory]
    [InlineData("https://chatgpt.com/", true)]
    [InlineData("https://chatgpt.com/g/x/project", true)]
    [InlineData("https://chatgpt.com/auth/login", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://petgpt.invalid/", false)]
    public void EnhancementEligibility_RequiresExactOriginAndNonAuthenticationRoute(string value, bool expected)
    {
        Assert.Equal(expected, ChatNavigationUrlPolicy.IsEnhancementEligible(new Uri(value)));
    }

    [Theory]
    [InlineData(false, false, true, WebDocumentTransition.TeardownAndDisable)]
    [InlineData(true, true, false, WebDocumentTransition.AwaitNavigationCompletion)]
    [InlineData(true, false, true, WebDocumentTransition.AdvanceRoute)]
    [InlineData(true, false, false, WebDocumentTransition.AttachSameDocument)]
    public void SourceTransitionPolicy_HandlesEligibilityLossAndSameDocumentReturn(
        bool eligible,
        bool isNewDocument,
        bool hasCurrentDocument,
        WebDocumentTransition expected)
    {
        Assert.Equal(
            expected,
            WebDocumentTransitionPolicy.Decide(eligible, isNewDocument, hasCurrentDocument));
    }

    [Fact]
    public void Bridge_AcceptsClosedReadyMessageForCurrentDocumentOnly()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");

        var result = bridge.AcceptMessage(
            ProjectConversation.AbsoluteUri,
            ProjectConversation,
            ReadyMessage(document));

        Assert.Equal(BridgeMessageResult.Accepted, result);
        Assert.True(bridge.Capabilities.Generation);
        Assert.False(bridge.Capabilities.ComposerEmpty);
    }

    [Fact]
    public void Bridge_RejectsOldDocumentAndRouteRevision()
    {
        using var bridge = NewBridge();
        var old = bridge.BeginDocument(ProjectHome, "0123456789abcdef");
        bridge.AdvanceRoute(ProjectConversation);

        Assert.Equal(
            BridgeMessageResult.StaleDocument,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(old)));
    }

    [Fact]
    public void Bridge_RejectsOversizedUnknownAndUntrustedMessages()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var unknownProperty = ReadyMessage(document).Replace(
            "\"payload\":",
            "\"unexpected\":true,\"payload\":",
            StringComparison.Ordinal);

        Assert.Equal(
            BridgeMessageResult.Oversized,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, new string('x', 2049)));
        Assert.Equal(
            BridgeMessageResult.InvalidSchema,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, unknownProperty));
        Assert.Equal(
            BridgeMessageResult.InvalidOrigin,
            bridge.AcceptMessage("https://example.com/", ProjectConversation, ReadyMessage(document)));
        Assert.Equal(
            BridgeMessageResult.InvalidOrigin,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, new Uri("https://example.com/"), ReadyMessage(document)));
    }

    [Fact]
    public void Bridge_RateLimitsPageStatusBursts()
    {
        var now = TimeSpan.Zero;
        using var bridge = new WebViewBridge(() => now);
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document);

        for (var index = 0; index < 40; index++)
            Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        Assert.Equal(
            BridgeMessageResult.RateLimited,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
        now = TimeSpan.FromMilliseconds(50);
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
    }

    [Fact]
    public void Bridge_CommandMessageCannotInvokeNavigation()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var command = Envelope(document, "command", "{\"name\":\"navigate\"}");

        var result = bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, command);

        Assert.Equal(BridgeMessageResult.UnsupportedKind, result);
        Assert.Empty(harness.Navigations);
    }

    [Fact]
    public void Bridge_UnsupportedComposerCapabilityRemainsUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document)));

        var composer = Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":true}");

        Assert.Equal(
            BridgeMessageResult.CapabilityUnavailable,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, composer));
        Assert.Equal(ComposerDraftState.Unknown, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_SyntheticComposerBooleanCapabilityCanReportNonEmptyWithoutText()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document, composerEmpty: true);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        var composer = Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":false}");

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, composer));
        Assert.Equal(ComposerDraftState.NonEmpty, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_MissingGenerationSignalIsUnknownUntilObserved()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: false)));

        Assert.Equal(GenerationCapabilityState.Unknown, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_GenerationCapabilityReportsTypedStateOnly()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: true)));

        var generating = Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"generating\"}");
        var idle = Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"idle\"}");

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, generating));
        Assert.Equal(GenerationCapabilityState.Generating, bridge.GenerationState);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, idle));
        Assert.Equal(GenerationCapabilityState.Idle, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_RevokedGenerationCapabilityResetsStateToUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: true)));
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(
                ProjectConversation.AbsoluteUri,
                ProjectConversation,
                Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"generating\"}")));

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: false)));

        Assert.Equal(GenerationCapabilityState.Unknown, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_RevokedComposerCapabilityResetsStateToUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, composerEmpty: true)));
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(
                ProjectConversation.AbsoluteUri,
                ProjectConversation,
                Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":true}")));

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, composerEmpty: false)));

        Assert.Equal(ComposerDraftState.Unknown, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_RepeatedReadyConfigurationIsIdempotent()
    {
        using var bridge = NewBridge();
        var events = 0;
        bridge.StatusChanged += (_, _) => events++;
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document);

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        Assert.Equal(1, events);
    }

    [Fact]
    public void Bridge_TeardownPreventsLateStatusUpdate()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        bridge.InvalidateDocument();

        Assert.Equal(
            BridgeMessageResult.StaleDocument,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document)));
        Assert.False(bridge.Capabilities.Generation);
    }

    [Fact]
    public void Bridge_SpaRouteRevisionRequiresFreshCapabilityAttachment()
    {
        using var bridge = NewBridge();
        var landing = bridge.BeginDocument(ProjectHome, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectHome.AbsoluteUri, ProjectHome, ReadyMessage(landing)));

        var conversation = bridge.AdvanceRoute(ProjectConversation);

        Assert.Equal(landing.DocumentSession, conversation.DocumentSession);
        Assert.Equal(landing.RouteRevision + 1, conversation.RouteRevision);
        Assert.False(bridge.Capabilities.Generation);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(conversation)));
    }

    [Fact]
    public void Bridge_PublicContractHasNoResponseTextExtractionApi()
    {
        var publicMembers = typeof(WebViewBridge).GetMembers(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(publicMembers, member =>
            member.Name.Contains("ResponseText", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Conversation", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("InnerHtml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompactOff_RemovesOnlyCompactLayer()
    {
        var state = new AppearanceLayerState();
        state.Apply("compact", new ThemeLayer("theme", null));

        var operations = state.Apply(null, new ThemeLayer("theme", null));

        Assert.Equal([new AppearanceLayerOperation(AppearanceLayerKind.Compact, AppearanceOperation.Remove, null)], operations);
        Assert.Equal("theme", state.ThemeCss);
    }

    [Fact]
    public void ThemeOff_RemovesOnlyThemeLayer()
    {
        var state = new AppearanceLayerState();
        state.Apply("compact", new ThemeLayer("theme", null));

        var operations = state.Apply("compact", null);

        Assert.Equal([new AppearanceLayerOperation(AppearanceLayerKind.Theme, AppearanceOperation.Remove, null)], operations);
        Assert.Equal("compact", state.CompactCss);
    }

    [Fact]
    public void RepeatedAppearanceApply_IsIdempotent()
    {
        var state = new AppearanceLayerState();
        var theme = new ThemeLayer("theme", null);

        Assert.NotEmpty(state.Apply("compact", theme));
        Assert.Empty(state.Apply("compact", theme));
    }

    [Theory]
    [InlineData(ChatRouteKind.ProjectLanding, false, true, false)]
    [InlineData(ChatRouteKind.ProjectConversation, true, true, false)]
    [InlineData(ChatRouteKind.ProjectConversation, false, true, true)]
    [InlineData(ChatRouteKind.ProjectConversation, false, false, false)]
    public void CompactPolicy_RelaxesHistoryAndFailsOpenWithoutSelector(
        ChatRouteKind route,
        bool historyMode,
        bool selectorAvailable,
        bool expectedSuppression)
    {
        var result = CompactAppearancePolicy.Evaluate(true, route, historyMode, selectorAvailable);

        Assert.Equal(expectedSuppression, result.SuppressNavigation);
    }

    [Fact]
    public void ThemeService_LegacyOrDisabledThemeRemovesPreviousTheme()
    {
        var service = new ThemeService();
        var capabilities = ThemeSelectorCapabilities.All;

        Assert.Null(service.BuildLayer(null, enabled: true, capabilities));
        Assert.Null(service.BuildLayer(ValidTheme(), enabled: false, capabilities));
    }

    [Fact]
    public void ThemeService_PackSwitchChangesThemeWithoutNavigationOrReload()
    {
        var navigationCalls = 0;
        var reloadCalls = 0;
        var controller = new AppearanceController(_ => { }, () => navigationCalls++, () => reloadCalls++);

        controller.UpdateTheme(new ThemeService().BuildLayer(ValidTheme("#112233"), true, ThemeSelectorCapabilities.All));
        controller.UpdateTheme(new ThemeService().BuildLayer(ValidTheme("#334455"), true, ThemeSelectorCapabilities.All));

        Assert.Equal(0, navigationCalls);
        Assert.Equal(0, reloadCalls);
        Assert.Contains("#334455", controller.State.ThemeCss, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeService_PartialForegroundBackgroundCapabilitySkipsPair()
    {
        var layer = new ThemeService().BuildLayer(
            ValidTheme(),
            enabled: true,
            new ThemeSelectorCapabilities(PageSurfacePair: false, SecondarySurfacePair: false, ComposerPair: true, Scrollbar: true));

        Assert.NotNull(layer);
        Assert.DoesNotContain("--petgpt-page-background", layer.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("--petgpt-page-text", layer.Css, StringComparison.Ordinal);
        Assert.Contains("--petgpt-composer-background", layer.Css, StringComparison.Ordinal);
        Assert.Contains("--petgpt-composer-text", layer.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeService_InvalidTokenCannotInjectCss()
    {
        var colors = ValidColors();
        colors["accent"] = "red;} body{display:none";
        var tokens = new ThemeTokens("tokens.json", colors, ValidMetrics(), null);

        Assert.Null(new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All));
    }

    [Fact]
    public void ThemeService_DecorationIsReencodedAndNeverExposesPathOrNetworkUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Pets", "legacy", "assets", "idle.png");
        var tokens = new ThemeTokens(
            "tokens.json",
            ValidColors(),
            ValidMetrics(),
            new ThemeDecoration(path, 20, "corner"));

        var layer = new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All);

        Assert.NotNull(layer);
        Assert.StartsWith("data:image/png;base64,", layer.DecorationDataUri, StringComparison.Ordinal);
        Assert.DoesNotContain(path, layer.Css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", layer.Css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", layer.Css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThemeService_FailedDecorationKeepsSolidTheme()
    {
        var tokens = new ThemeTokens(
            "tokens.json",
            ValidColors(),
            ValidMetrics(),
            new ThemeDecoration(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), 20, "background"));

        var layer = new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All);

        Assert.NotNull(layer);
        Assert.Null(layer.DecorationDataUri);
        Assert.Contains("--petgpt-page-background", layer.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenInBrowser_PrefersSafeCurrentChatGptUrl()
    {
        Assert.Equal(
            ProjectConversation,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(ProjectConversation, ProjectHome.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://petgpt.invalid/")]
    [InlineData("javascript:alert(1)")]
    public void OpenInBrowser_UnsafeCurrentFallsBackToHome(string current)
    {
        Assert.Equal(
            ProjectHome,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(new Uri(current), ProjectHome.AbsoluteUri));
    }

    [Fact]
    public void OpenInBrowser_InvalidHomeFallsBackToRoot()
    {
        Assert.Equal(
            Root,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(new Uri("https://example.com/"), "https://example.com/project"));
    }

    private static WebViewBridge NewBridge() => new();

    private static string ReadyMessage(
        BridgeDocumentIdentity document,
        bool generation = true,
        bool composerEmpty = false) =>
        Envelope(
            document,
            "ready",
            "{\"adapterVersion\":\"1\",\"capabilities\":{" +
            "\"generation\":" + generation.ToString().ToLowerInvariant() + "," +
            "\"composerEmpty\":" + composerEmpty.ToString().ToLowerInvariant() + "," +
            "\"pageSurface\":true,\"secondarySurface\":false," +
            "\"composerSurface\":true,\"scrollbar\":true," +
            "\"compactNavigation\":false}}");

    private static string Envelope(BridgeDocumentIdentity document, string kind, string payload) =>
        "{\"v\":1,\"kind\":\"" + kind + "\",\"documentSession\":\"" +
        document.DocumentSession + "\",\"routeRevision\":" + document.RouteRevision +
        ",\"payload\":" + payload + "}";

    private static ThemeTokens ValidTheme(string background = "#171D38") =>
        new("tokens.json", ValidColors(background), ValidMetrics(), null);

    private static Dictionary<string, string> ValidColors(string background = "#171D38") =>
        new(StringComparer.Ordinal)
        {
            ["background"] = background,
            ["surface"] = "#222B4A",
            ["text"] = "#F4F1FF",
            ["mutedText"] = "#C4CAE3",
            ["accent"] = "#B9A1FF",
            ["border"] = "#596388",
            ["composerBackground"] = "#292F50",
            ["composerText"] = "#F4F1FF",
            ["scrollbarThumb"] = "#8792C4"
        };

    private static Dictionary<string, int> ValidMetrics() =>
        new(StringComparer.Ordinal)
        {
            ["surfaceRadiusPx"] = 14,
            ["composerRadiusPx"] = 18,
            ["borderWidthPx"] = 1,
            ["scrollbarWidthPx"] = 8
        };

    private sealed class NavigationHarness
    {
        private Uri? _current;

        public NavigationHarness(string? home, Uri? current)
        {
            _current = current;
            Service = new ChatNavigationService(
                home,
                () => _current,
                uri =>
                {
                    Navigations.Add(uri);
                    _current = uri;
                },
                _ =>
                {
                    PromptCalls++;
                    return Task.FromResult(ConfirmLeave);
                });
            Service.ObserveSource(current);
        }

        public ChatNavigationService Service { get; }
        public List<Uri> Navigations { get; } = [];
        public bool ConfirmLeave { get; set; } = true;
        public int PromptCalls { get; private set; }
        public int GlobalNewChatCalls { get; private set; }
    }
}
