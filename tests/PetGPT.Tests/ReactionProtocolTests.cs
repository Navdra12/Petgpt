using System.Reflection;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Personas;
using PetGPT.Reactions;
using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class ReactionProtocolTests
{
    private const string Epoch = "0123456789abcdef";
    private static readonly Uri Conversation = new("https://chatgpt.com/g/project/c/conversation");

    [Theory]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end", 65)]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/end", null)]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/0/end", 0)]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/100/end", 100)]
    public void ParserAcceptsOnlyCanonicalValidMarkers(string href, int? intensity)
    {
        Assert.True(ReactionProtocol.TryParse(href, out var marker));
        Assert.NotNull(marker);
        Assert.Equal(Epoch, marker.Epoch);
        Assert.Equal("trixie", marker.PetId);
        Assert.Equal("smug", marker.ReactionId);
        Assert.Equal(intensity, marker.Intensity);
    }

    [Theory]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/01/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/101/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/-1/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/1.0/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/6")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end/")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/extra/end")]
    [InlineData("http://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://example.com/#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid.evil.example/#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid:443/#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://user@petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid/?q=1#r1/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/sm%75g/65/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/smüg/65/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdeF/trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/1trixie/smug/65/end")]
    [InlineData("https://petgpt.invalid/#r1/0123456789abcdef/trixie/Smug/65/end")]
    [InlineData("https://petgpt.invalid/#r2/0123456789abcdef/trixie/smug/65/end")]
    [InlineData("")]
    [InlineData(" https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end")]
    public void ParserRejectsMalformedOrNormalizedInput(string href) =>
        Assert.False(ReactionProtocol.TryParse(href, out _));

    [Fact]
    public void ParserStillEnforcesGrammarAtExactMaximumBound()
    {
        var prefix = $"https://petgpt.invalid/#r1/{Epoch}/trixie/smug/";
        var fillerLength = ReactionProtocol.MaximumHrefLength - prefix.Length - "/end".Length;
        var href = prefix + new string('1', fillerLength) + "/end";
        Assert.Equal(ReactionProtocol.MaximumHrefLength, href.Length);
        Assert.False(ReactionProtocol.TryParse(href, out _)); // bounded, but intensity grammar remains authoritative
    }

    [Fact]
    public void ParserRejectsOversizedInput() =>
        Assert.False(ReactionProtocol.TryParse(new string('a', ReactionProtocol.MaximumHrefLength + 1), out _));

    [Fact]
    public void ParsedMarkerContainsOnlyBoundedFields()
    {
        var properties = typeof(ParsedReactionMarker).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal(["Epoch", "Intensity", "PetId", "ReactionId"], properties.Select(p => p.Name).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ParserAcceptsMaximumLengthIdentifiers()
    {
        var id = "a" + new string('0', 31);
        Assert.True(ReactionProtocol.TryParse($"https://petgpt.invalid/#r1/{Epoch}/{id}/{id}/100/end", out _));
    }

    [Fact]
    public void ValidatorUsesDefaultIntensityAndBaseCandidates()
    {
        var result = Validate(Marker(), intensity: null);
        Assert.Equal(40, result.EffectiveIntensity);
        Assert.Equal(["idle"], result.AnimationCandidateIds);
    }

    [Fact]
    public void ValidatorPreservesExplicitIntensitySelectsHighestBandAndVisibleMs()
    {
        var result = Validate(Marker(85), intensity: 85);
        Assert.Equal(85, result.EffectiveIntensity);
        Assert.Equal(["high"], result.AnimationCandidateIds);
        Assert.Equal(1200, result.VisibleMs);
    }

    [Fact]
    public void ValidatorBelowAllBandsUsesBaseCandidates()
    {
        var result = Validate(Marker(5), intensity: 5);
        Assert.Equal(["idle"], result.AnimationCandidateIds);
    }

    [Theory]
    [InlineData(false, PersonaSessionState.ProtocolObserved, "trixie", Epoch, "smug", false)]
    [InlineData(true, PersonaSessionState.NeedsActivation, "trixie", Epoch, "smug", false)]
    [InlineData(true, PersonaSessionState.ProtocolObserved, "other", Epoch, "smug", false)]
    [InlineData(true, PersonaSessionState.ProtocolObserved, "trixie", "fedcba9876543210", "smug", false)]
    [InlineData(true, PersonaSessionState.ProtocolObserved, "trixie", Epoch, "unknown", false)]
    [InlineData(true, PersonaSessionState.AwaitingMarker, "trixie", Epoch, "smug", true)]
    [InlineData(true, PersonaSessionState.ProtocolObserved, "trixie", Epoch, "smug", true)]
    public void ValidatorEnforcesSettingsSessionIdentityAndDedupe(
        bool enabled,
        PersonaSessionState state,
        string petId,
        string epoch,
        string reactionId,
        bool alreadyDispatched)
    {
        var pack = Pack();
        var persona = new PersonaAssembler().Build(pack, Epoch);
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 2);
        var marker = new ParsedReactionMarker(epoch, petId, reactionId, 65);
        var correlation = new ReactionCorrelation(document, new GenerationSerial(3), new TurnSerial(4), "assistant-1", false, alreadyDispatched);
        var context = new ReactionValidationContext(enabled, pack, state, persona, document, correlation);

        Assert.Equal(enabled && state is PersonaSessionState.AwaitingMarker or PersonaSessionState.ProtocolObserved &&
            petId == "trixie" && epoch == Epoch && reactionId == "smug" && !alreadyDispatched,
            ReactionValidator.TryValidate(marker, context, out _));
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbb", 2, 3, "assistant-1")]
    [InlineData("aaaaaaaaaaaaaaaa", 1, 3, "assistant-1")]
    [InlineData("aaaaaaaaaaaaaaaa", 2, 9, "assistant-1")]
    [InlineData("aaaaaaaaaaaaaaaa", 2, 3, "")]
    public void ValidatorRejectsStaleDocumentRouteGenerationOrAssistant(
        string session,
        long revision,
        long generation,
        string assistantId)
    {
        var pack = Pack();
        var persona = new PersonaAssembler().Build(pack, Epoch);
        var current = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 2);
        var correlation = new ReactionCorrelation(
            new BridgeDocumentIdentity(session, revision),
            new GenerationSerial(generation),
            new TurnSerial(4),
            assistantId,
            false,
            false);
        var context = new ReactionValidationContext(true, pack, PersonaSessionState.ProtocolObserved, persona, current, correlation, new GenerationSerial(3));

        Assert.False(ReactionValidator.TryValidate(Marker(65), context, out _));
    }

    [Fact]
    public void ValidatorRejectsStalePackFingerprint()
    {
        var currentPack = Pack();
        var stalePersona = new PersonaAssembler().Build(Pack(identity: "old identity"), Epoch);
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 2);
        var context = new ReactionValidationContext(
            true,
            currentPack,
            PersonaSessionState.ProtocolObserved,
            stalePersona,
            document,
            new ReactionCorrelation(document, new GenerationSerial(3), new TurnSerial(4), "assistant-1", false, false));

        Assert.False(ReactionValidator.TryValidate(Marker(65), context, out _));
    }

    [Fact]
    public void ValidatorProducesExistingReactionWithExactSourceId()
    {
        var reaction = Validate(Marker(65), intensity: 65);
        Assert.Equal("smug", reaction.ReactionId);
        Assert.Equal(new GenerationSerial(3), reaction.Generation);
        Assert.Equal(new TurnSerial(4), reaction.Turn);
    }

    [Fact]
    public void CoordinatorAllocatesTurnAndCorrelatesOneAssistantReaction()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        var send = coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(1), false));
        Assert.Equal(new TurnSerial(1), send.Turn);
        Assert.True(coordinator.TryCorrelate(new BridgeReactionObservation(document, MarkerHref(), new GenerationSerial(1), "assistant-1"), out var correlation));
        coordinator.Commit(correlation);
        Assert.False(coordinator.TryCorrelate(new BridgeReactionObservation(document, MarkerHref(), new GenerationSerial(1), "assistant-1"), out _));
    }

    [Fact]
    public void CoordinatorAllowsSameReactionOnNextTurnAndRegenerate()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(1), false));
        Assert.True(coordinator.TryCorrelate(new(document, MarkerHref(), new GenerationSerial(1), "assistant-1"), out var first));
        coordinator.Commit(first);
        var regenerate = coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(2), true));
        Assert.Equal(new TurnSerial(2), regenerate.Turn);
        Assert.True(coordinator.TryCorrelate(new(document, MarkerHref(), new GenerationSerial(2), "assistant-2"), out _));
    }

    [Fact]
    public void CoordinatorPreservesLandingTurnOnlyAcrossImmediateSameDocumentRevision()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var landing = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        var conversation = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 1);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(landing, new GenerationSerial(1), false));
        Assert.True(coordinator.TryAdvanceRoute(conversation));
        Assert.True(coordinator.TryCorrelate(new(conversation, MarkerHref(), new GenerationSerial(1), "assistant-1"), out _));
        Assert.False(coordinator.TryAdvanceRoute(new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 3)));
    }

    [Theory]
    [InlineData(GenerationCapabilityState.Generating, typeof(PetEvent.GenerationObserved))]
    [InlineData(GenerationCapabilityState.Unknown, typeof(PetEvent.GenerationUnknown))]
    public void CoordinatorMapsGenerationSignals(GenerationCapabilityState state, Type eventType)
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(7), false));
        Assert.IsType(eventType, coordinator.ObserveGeneration(state));
    }

    [Fact]
    public void CoordinatorEmitsIdleOnlyAfterConfirmedGenerating()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(7), false));
        Assert.Null(coordinator.ObserveGeneration(GenerationCapabilityState.Idle));
        coordinator.ObserveGeneration(GenerationCapabilityState.Generating);
        Assert.IsType<PetEvent.GenerationIdle>(coordinator.ObserveGeneration(GenerationCapabilityState.Idle));
    }

    [Fact]
    public void CoordinatorInvalidationDropsLateReaction()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(1), false));
        coordinator.Invalidate();
        Assert.False(coordinator.TryCorrelate(new(document, MarkerHref(), new GenerationSerial(1), "assistant-1"), out _));
    }

    [Fact]
    public void CoordinatorRejectsASecondAssistantIdentityForTheSameTurn()
    {
        var coordinator = new ReactionLiveTurnCoordinator();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        coordinator.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(1), false));
        Assert.True(coordinator.TryCorrelate(new(document, MarkerHref(), new GenerationSerial(1), "assistant-1"), out _));
        Assert.False(coordinator.TryCorrelate(new(document, MarkerHref(), new GenerationSerial(1), "assistant-2"), out _));
    }

    [Fact]
    public void AwaitingMarkerValidEvidenceTransitionsProtocolAndUsesSameReaction()
    {
        var pack = Pack();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        using var session = AwaitingSession(pack, document);
        var correlation = new ReactionCorrelation(document, new GenerationSerial(1), new TurnSerial(1), "assistant-1", false, false);
        var validation = new ReactionValidationContext(true, pack, session.State, session.Context, document, correlation);

        Assert.True(ReactionValidator.TryValidate(Marker(65), validation, out var reaction));
        Assert.True(session.ObserveProtocol(new TrustedProtocolObservation(Epoch, "trixie", document)));
        Assert.Equal(PersonaSessionState.ProtocolObserved, session.State);
        Assert.Equal("smug", reaction!.ReactionId);
    }

    [Fact]
    public void WrongMarkerCannotMoveAwaitingPersonaState()
    {
        var pack = Pack();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        using var session = AwaitingSession(pack, document);

        Assert.False(session.ObserveProtocol(new TrustedProtocolObservation("fedcba9876543210", "trixie", document)));
        Assert.Equal(PersonaSessionState.AwaitingMarker, session.State);
    }

    [Fact]
    public void ProtocolObservedAcceptsASeparateSubsequentLiveTurn()
    {
        var pack = Pack();
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 0);
        using var session = AwaitingSession(pack, document);
        Assert.True(session.ObserveProtocol(new TrustedProtocolObservation(Epoch, "trixie", document)));
        var validation = new ReactionValidationContext(
            true, pack, session.State, session.Context, document,
            new ReactionCorrelation(document, new GenerationSerial(2), new TurnSerial(2), "assistant-2", false, false));
        Assert.True(ReactionValidator.TryValidate(Marker(65), validation, out _));
    }

    [Fact]
    public void BridgeAcceptsOnlyExactReactionEnvelopeAndRaisesTypedEvidence()
    {
        using var bridge = new WebViewBridge();
        var document = bridge.BeginDocument(Conversation, "aaaaaaaaaaaaaaaa");
        BridgeReactionObservation? observed = null;
        bridge.ReactionObserved += (_, value) => observed = value;
        var json = Envelope(document, "reaction", $"{{\"href\":\"{MarkerHref()}\",\"generationSerial\":1,\"assistantId\":\"assistant-1\"}}");

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, json));
        Assert.NotNull(observed);
        Assert.Equal(MarkerHref(), observed.Href);
    }

    [Theory]
    [InlineData("{\"href\":\"x\",\"generationSerial\":1,\"assistantId\":\"assistant-1\",\"command\":\"exit\"}")]
    [InlineData("{\"href\":\"x\",\"generationSerial\":0,\"assistantId\":\"assistant-1\"}")]
    [InlineData("{\"href\":\"x\",\"generationSerial\":1,\"assistantId\":\"\"}")]
    [InlineData("{\"href\":\"x\",\"generationSerial\":1,\"assistantId\":\"bad\\nvalue\"}")]
    public void BridgeRejectsUnboundedOrAuthorityBearingReactionPayload(string payload)
    {
        using var bridge = new WebViewBridge();
        var document = bridge.BeginDocument(Conversation, "aaaaaaaaaaaaaaaa");
        Assert.Equal(BridgeMessageResult.InvalidSchema, bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, Envelope(document, "reaction", payload)));
    }

    [Fact]
    public void SustainedBridgeOverflowDisablesOnlyReactionsForDocument()
    {
        var now = TimeSpan.Zero;
        using var bridge = new WebViewBridge(() => now);
        var document = bridge.BeginDocument(Conversation, "aaaaaaaaaaaaaaaa");
        var message = Envelope(document, "reaction", $"{{\"href\":\"{MarkerHref()}\",\"generationSerial\":1,\"assistantId\":\"assistant-1\"}}");
        for (var index = 0; index < 40; index++)
            Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message));

        for (var tick = 1; tick <= 21; tick++)
        {
            now = TimeSpan.FromMilliseconds(tick * 100);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
        }

        Assert.True(bridge.ReactionsDisabledForDocument);
        Assert.NotNull(bridge.CurrentDocument);
    }

    [Fact]
    public void SustainedBridgeOverflowRemainsDisabledAcrossSameDocumentRouteRevision()
    {
        var now = TimeSpan.Zero;
        using var bridge = new WebViewBridge(() => now);
        var document = bridge.BeginDocument(Conversation, "aaaaaaaaaaaaaaaa");
        var message = Envelope(document, "reaction", $"{{\"href\":\"{MarkerHref()}\",\"generationSerial\":1,\"assistantId\":\"assistant-1\"}}");
        for (var index = 0; index < 40; index++)
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
        for (var tick = 1; tick <= 21; tick++)
        {
            now = TimeSpan.FromMilliseconds(tick * 100);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
            bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, message);
        }

        bridge.AdvanceRoute(new Uri("https://chatgpt.com/g/project/c/next"));
        Assert.True(bridge.ReactionsDisabledForDocument);
    }

    [Theory]
    [InlineData("reaction")]
    [InlineData("submit")]
    public void BridgeRejectsGenerationSerialBeyondJavaScriptSafeInteger(string kind)
    {
        using var bridge = new WebViewBridge();
        var document = bridge.BeginDocument(Conversation, "aaaaaaaaaaaaaaaa");
        var payload = kind == "reaction"
            ? $"{{\"href\":\"{MarkerHref()}\",\"generationSerial\":9007199254740992,\"assistantId\":\"assistant-1\"}}"
            : "{\"event\":\"submit\",\"generationSerial\":9007199254740992}";
        Assert.Equal(BridgeMessageResult.InvalidSchema, bridge.AcceptMessage(Conversation.AbsoluteUri, Conversation, Envelope(document, kind == "reaction" ? "reaction" : "activity", payload)));
    }

    [Theory]
    [InlineData("https://petgpt.invalid/", true)]
    [InlineData("https://petgpt.invalid/#r1/x", true)]
    [InlineData("http://petgpt.invalid/", true)]
    [InlineData("https://petgpt.invalid.evil.example/", false)]
    [InlineData("https://example.com/", false)]
    public void ReservedHostPolicyUsesExactHost(string uri, bool expected) =>
        Assert.Equal(expected, ReservedMarkerUrlPolicy.IsExactReservedHost(uri));

    [Fact]
    public void ReactionBridgePublicContractHasNoCommandNavigationSettingsOrPromptAuthority()
    {
        var names = typeof(BridgeReactionObservation).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(["AssistantId", "Document", "Generation", "Href"], names.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(names, name => name.Contains("Command", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Prompt", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Setting", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Navigation", StringComparison.OrdinalIgnoreCase));
    }

    private static ParsedReactionMarker Marker(int? intensity = null) => new(Epoch, "trixie", "smug", intensity);
    private static string MarkerHref() => $"https://petgpt.invalid/#r1/{Epoch}/trixie/smug/65/end";

    private static ValidatedReaction Validate(ParsedReactionMarker marker, int? intensity)
    {
        var pack = Pack();
        var persona = new PersonaAssembler().Build(pack, Epoch);
        var document = new BridgeDocumentIdentity("aaaaaaaaaaaaaaaa", 2);
        var context = new ReactionValidationContext(
            true,
            pack,
            PersonaSessionState.ProtocolObserved,
            persona,
            document,
            new ReactionCorrelation(document, new GenerationSerial(3), new TurnSerial(4), "assistant-1", false, false));
        Assert.Equal(intensity, marker.Intensity);
        Assert.True(ReactionValidator.TryValidate(marker, context, out var reaction));
        return Assert.IsType<ValidatedReaction>(reaction);
    }

    private static string Envelope(BridgeDocumentIdentity document, string kind, string payload) =>
        $"{{\"v\":1,\"kind\":\"{kind}\",\"documentSession\":\"{document.DocumentSession}\",\"routeRevision\":{document.RouteRevision},\"payload\":{payload}}}";

    private static PersonaSession AwaitingSession(CharacterPack pack, BridgeDocumentIdentity document)
    {
        var session = new PersonaSession(true, "ReviewThenSend", epochFactory: () => Epoch);
        session.SelectPack(pack);
        session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, "/g/project/c/conversation", document));
        session.ObserveSubmissionCapability(true);
        Assert.Equal(PersonaCopyResult.Copied, session.CopyContext(_ => true));
        Assert.True(session.ObserveSubmission(new BridgeSubmissionObservation(document, new GenerationSerial(1), false)));
        Assert.Equal(PersonaSessionState.AwaitingMarker, session.State);
        return session;
    }

    private static CharacterPack Pack(string identity = "current identity")
    {
        var root = Path.Combine(Path.GetTempPath(), "petgpt-reaction-tests", Guid.NewGuid().ToString("N"));
        var profile = new PersonaProfile(
            Path.Combine(root, "persona.json"),
            Path.Combine(root, "voice.md"),
            "trixie",
            identity,
            "worldview",
            ["value"], ["like"], ["dislike"], ["fear"], ["taboo"], ["humor"],
            "companion", ["appraise"], "factual", "voice");
        var clips = new Dictionary<string, CharacterClip>(StringComparer.Ordinal)
        {
            ["idle"] = new("idle", "png", Path.Combine(root, "idle.png"), "hold", true, 1, 1, null, null, null, null, null),
            ["mid"] = new("mid", "png", Path.Combine(root, "mid.png"), "hold", true, 1, 1, null, null, null, null, null),
            ["high"] = new("high", "png", Path.Combine(root, "high.png"), "hold", true, 1, 1, null, null, null, null, null)
        };
        var reactions = new Dictionary<string, CharacterReaction>(StringComparer.Ordinal)
        {
            ["smug"] = new("smug", "proud", ["idle"], 40, 1200,
                [new ReactionIntensityBand(50, ["mid"]), new ReactionIntensityBand(80, ["high"])])
        };
        return new CharacterPack(
            root, CharacterPackSource.Installed, "trixie", "Trixie", "1.0.0", "2.0.0", profile,
            new CharacterPresentation(150, 150, .5, 1), clips,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["idle"] = ["idle"] },
            reactions, null, null, new Dictionary<string, string>(), []);
    }
}
