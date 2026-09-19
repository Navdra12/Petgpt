using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Personas;
using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class PersonaSessionTests
{
    private static readonly Uri LandingUri = new("https://chatgpt.com/g/project_opaque/project");
    private static readonly Uri ConversationUri = new("https://chatgpt.com/g/project_opaque/c/chat_opaque");
    private static readonly BridgeDocumentIdentity Document0 = new("0123456789abcdef", 0);
    private static readonly BridgeDocumentIdentity Document1 = new("0123456789abcdef", 1);

    [Fact]
    public void Assembler_IsDeterministicForIdenticalPackAndEpoch()
    {
        var assembler = new PersonaAssembler();
        var pack = Pack();

        var first = assembler.Build(pack, "0011223344556677");
        var second = assembler.Build(pack, "0011223344556677");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("Character ID", "trixie")]
    [InlineData("Identity", "A theatrical magician")]
    [InlineData("Worldview", "Skill deserves recognition")]
    [InlineData("Values", "Mastery")]
    [InlineData("Likes", "Clever solutions")]
    [InlineData("Dislikes", "Condescension")]
    [InlineData("Fears", "Being forgotten")]
    [InlineData("Taboos", "Fabricated evidence")]
    [InlineData("Humor", "Theatrical exaggeration")]
    [InlineData("Relationship to user", "A familiar companion and audience")]
    [InlineData("Appraisal principles", "Distinguish confidence from evidence")]
    [InlineData("Factual answer style", "Admit uncertainty and mistakes")]
    [InlineData("Voice", "Use crisp theatrical phrasing")]
    public void Assembler_IncludesCompleteValidatedProfile(string heading, string value)
    {
        var context = new PersonaAssembler().Build(Pack(), "0011223344556677");

        Assert.Contains(heading, context.Text, StringComparison.Ordinal);
        Assert.Contains(value, context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembler_IncludesExactReactionVocabularyAndMeanings()
    {
        var context = new PersonaAssembler().Build(Pack(), "0011223344556677");

        Assert.Equal(["offended", "smug"], context.AllowedReactions.Select(item => item.Id));
        Assert.Contains("A proud, self-satisfied reaction", context.Text, StringComparison.Ordinal);
        Assert.Contains("A boundary was crossed", context.Text, StringComparison.Ordinal);
        Assert.Contains("Default intensity: 65", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("happy", context.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assembler_UsesCurrentEpochPetAndAllowedReactionInMarkerExample()
    {
        var context = new PersonaAssembler().Build(Pack(), "0011223344556677");

        Assert.Contains(
            "https://petgpt.invalid/#r1/0011223344556677/trixie/offended/70/end",
            context.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Assembler_DoesNotEmitLocalPathsOrJsonFilenames()
    {
        var context = new PersonaAssembler().Build(
            Pack(rootPath: Path.Combine(Path.GetTempPath(), "private-persona-root")),
            "0011223344556677");

        Assert.DoesNotContain("private-persona-root", context.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile.json", context.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("voice.md", context.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assembler_FingerprintIsStableAndCanonical()
    {
        var first = new PersonaAssembler().Build(Pack(rootPath: Path.Combine(Path.GetTempPath(), "one")), "0011223344556677");
        var second = new PersonaAssembler().Build(Pack(rootPath: Path.Combine(Path.GetTempPath(), "two")), "8899aabbccddeeff");

        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first.SourceFingerprint);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("voice")]
    [InlineData("reaction")]
    public void Assembler_FingerprintChangesWithSemanticSource(string changedPart)
    {
        var baseline = new PersonaAssembler().Build(Pack(), "0011223344556677");
        var changed = changedPart switch
        {
            "profile" => Pack(identity: "A changed identity"),
            "voice" => Pack(voice: "A changed voice"),
            "reaction" => Pack(smugMeaning: "A changed reaction meaning"),
            _ => throw new InvalidOperationException()
        };

        var result = new PersonaAssembler().Build(changed, "0011223344556677");

        Assert.NotEqual(baseline.SourceFingerprint, result.SourceFingerprint);
    }

    [Fact]
    public void Assembler_AllowsExactUtf8Boundary()
    {
        var initial = new PersonaAssembler().Build(Pack(), "0011223344556677");
        var assembler = new PersonaAssembler(initial.Utf8ByteCount);

        var result = assembler.Build(Pack(), "0011223344556677");

        Assert.Equal(initial.Utf8ByteCount, result.Utf8ByteCount);
    }

    [Fact]
    public void Assembler_RejectsOverflowWithoutTruncation()
    {
        var exception = Assert.Throws<PersonaAssemblyException>(() =>
            new PersonaAssembler(512).Build(Pack(), "0011223344556677"));

        Assert.Equal("persona_context_too_large", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(false, true, "persona_unavailable")]
    [InlineData(true, false, "reaction_vocabulary_unavailable")]
    public void Assembler_RejectsIncompleteActivationSource(
        bool hasPersona,
        bool hasReactions,
        string expectedDiagnostic)
    {
        var exception = Assert.Throws<PersonaAssemblyException>(() =>
            new PersonaAssembler().Build(Pack(hasPersona: hasPersona, hasReactions: hasReactions), "0011223344556677"));

        Assert.Equal(expectedDiagnostic, exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.ProjectLanding, PersonaSessionState.NeedsActivation)]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.ProjectConversation, PersonaSessionState.NeedsActivation)]
    [InlineData(false, "ReviewThenSend", ChatRouteKind.ProjectConversation, PersonaSessionState.Inactive)]
    [InlineData(true, "Unsupported", ChatRouteKind.ProjectConversation, PersonaSessionState.Inactive)]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.ChatRoot, PersonaSessionState.Degraded)]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.UnknownChatGpt, PersonaSessionState.Degraded)]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.Authentication, PersonaSessionState.Degraded)]
    [InlineData(true, "ReviewThenSend", ChatRouteKind.Unrelated, PersonaSessionState.Degraded)]
    public void Session_TruthfullyDerivesStateFromRoleplayPackAndRoute(
        bool enabled,
        string activationMode,
        ChatRouteKind route,
        PersonaSessionState expected)
    {
        using var session = new PersonaSession(enabled, activationMode);
        session.SelectPack(Pack());
        session.ObserveContext(Context(route, Document0));

        Assert.Equal(expected, session.State);
    }

    [Fact]
    public void Session_EpochIsSixteenLowercaseHexAndFreshRestartRotates()
    {
        using var session = ReadySession();
        var first = session.Epoch;

        session.RestartActivation();

        Assert.Matches("^[0-9a-f]{16}$", first!);
        Assert.Matches("^[0-9a-f]{16}$", session.Epoch!);
        Assert.NotEqual(first, session.Epoch);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
    }

    [Fact]
    public void Session_ReobservingSameContextForShowHideDoesNotRotateEpoch()
    {
        using var session = ReadySession();
        var epoch = session.Epoch;

        session.ObserveContext(Context(ChatRouteKind.ProjectConversation, Document0));

        Assert.Equal(epoch, session.Epoch);
    }

    [Fact]
    public void Session_SelectedPackChangeInvalidatesAndRotates()
    {
        using var session = ReadySession();
        var epoch = session.Epoch;

        session.SelectPack(Pack(id: "fluttershy"));

        Assert.NotEqual(epoch, session.Epoch);
        Assert.Equal("fluttershy", session.Context!.CharacterId);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
    }

    [Fact]
    public void Session_SameIdVersionProfileChangeInvalidatesByFingerprint()
    {
        using var session = ReadySession();
        var epoch = session.Epoch;
        var fingerprint = session.Context!.SourceFingerprint;

        session.SelectPack(Pack(identity: "Changed without a version bump"));

        Assert.NotEqual(epoch, session.Epoch);
        Assert.NotEqual(fingerprint, session.Context!.SourceFingerprint);
    }

    [Fact]
    public void Session_LegacyWithoutPersonaBecomesInactive()
    {
        using var session = ReadySession();

        session.SelectPack(Pack(id: "legacy", hasPersona: false, hasReactions: false));

        Assert.Equal(PersonaSessionState.Inactive, session.State);
        Assert.Null(session.Context);
        Assert.Null(session.Epoch);
    }

    [Fact]
    public void Copy_WritesOnlyExactPersonaContextAndDoesNotClaimSubmission()
    {
        using var session = ReadySession();
        string? copied = null;

        var result = session.CopyContext(text => { copied = text; return true; });

        Assert.Equal(PersonaCopyResult.Copied, result);
        Assert.Equal(session.Context!.Text, copied);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.True(session.ContextCopied);
        Assert.Contains("paste and send", session.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copy_ClipboardFailureKeepsTruthfulState()
    {
        using var session = ReadySession();

        var result = session.CopyContext(_ => false);

        Assert.Equal(PersonaCopyResult.Failed, result);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.False(session.ContextCopied);
    }

    [Theory]
    [InlineData(false, ComposerDraftState.Empty, GenerationCapabilityState.Idle, StagePersonaResult.Unsupported)]
    [InlineData(true, ComposerDraftState.Unknown, GenerationCapabilityState.Idle, StagePersonaResult.Unsupported)]
    [InlineData(true, ComposerDraftState.NonEmpty, GenerationCapabilityState.Idle, StagePersonaResult.ComposerNotEmpty)]
    [InlineData(true, ComposerDraftState.Empty, GenerationCapabilityState.Generating, StagePersonaResult.Generating)]
    public async Task Apply_RejectsUnsafeComposerOrGenerationWithoutCallingStage(
        bool capability,
        ComposerDraftState draft,
        GenerationCapabilityState generation,
        StagePersonaResult expected)
    {
        using var session = ReadySession();
        var calls = 0;
        var snapshot = StageSnapshot(capability, draft, generation);

        var result = await session.ApplyAsync(
            snapshot,
            (_, _) => { calls++; return Task.FromResult(StagePersonaResult.Staged); },
            CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(0, calls);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
    }

    [Fact]
    public async Task Apply_SafeEmptyMatchingStageResultMovesToStagedWithoutSending()
    {
        using var session = ReadySession();
        PersonaContext? staged = null;

        var result = await session.ApplyAsync(
            StageSnapshot(true, ComposerDraftState.Empty, GenerationCapabilityState.Idle),
            (context, _) => { staged = context; return Task.FromResult(StagePersonaResult.Staged); },
            CancellationToken.None);

        Assert.Equal(StagePersonaResult.Staged, result);
        Assert.Same(session.Context, staged);
        Assert.Equal(PersonaSessionState.Staged, session.State);
        Assert.Contains("review and send", session.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StagePersonaResult.Unsupported)]
    [InlineData(StagePersonaResult.ComposerNotEmpty)]
    [InlineData(StagePersonaResult.Generating)]
    [InlineData(StagePersonaResult.RouteChanged)]
    public async Task Apply_FailedStageNeverClaimsStaged(StagePersonaResult result)
    {
        using var session = ReadySession();

        var actual = await session.ApplyAsync(
            StageSnapshot(true, ComposerDraftState.Empty, GenerationCapabilityState.Idle),
            (_, _) => Task.FromResult(result),
            CancellationToken.None);

        Assert.Equal(result, actual);
        Assert.NotEqual(PersonaSessionState.Staged, session.State);
    }

    [Fact]
    public async Task Apply_RouteRaceCannotCommitLateStageResult()
    {
        using var session = ReadySession();
        var completion = new TaskCompletionSource<StagePersonaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = session.ApplyAsync(
            StageSnapshot(true, ComposerDraftState.Empty, GenerationCapabilityState.Idle),
            (_, _) => completion.Task,
            CancellationToken.None);
        session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, "/g/project_opaque/c/other", Document1));

        completion.SetResult(StagePersonaResult.Staged);
        var result = await apply;

        Assert.Equal(StagePersonaResult.RouteChanged, result);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_AfterSubmissionExplicitlyRestartsActivation(bool protocolObserved)
    {
        using var session = ReadySession(epoch: "0011223344556677");
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));
        if (protocolObserved)
        {
            session.ObserveProtocol(new TrustedProtocolObservation(
                "0011223344556677",
                "trixie",
                Document0));
        }
        var epoch = session.Epoch;
        Assert.True(session.CanApply);

        var result = await session.ApplyAsync(
            StageSnapshot(false, ComposerDraftState.Unknown, GenerationCapabilityState.Idle),
            (_, _) => throw new InvalidOperationException("unsupported staging must not call the delegate"),
            CancellationToken.None);

        Assert.Equal(StagePersonaResult.Unsupported, result);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.True(session.CanCopy);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Submit_AfterPreparedContextMovesToAwaitingMarker(bool staged)
    {
        using var session = ReadySession();
        if (staged)
            StageSuccessfully(session);
        else
            Assert.Equal(PersonaCopyResult.Copied, session.CopyContext(_ => true));

        var accepted = session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), IsRegenerate: false));

        Assert.True(accepted);
        Assert.Equal(PersonaSessionState.AwaitingMarker, session.State);
        Assert.Contains("acknowledgement", session.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Submit_WithoutCopyOrStageDoesNotFalselyActivate()
    {
        using var session = ReadySession();

        var accepted = session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), IsRegenerate: false));

        Assert.False(accepted);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void Submit_RegenerateOrDuplicateDoesNotAdvanceTwice(bool regenerate, long serial)
    {
        using var session = ReadySession();
        Assert.Equal(PersonaCopyResult.Copied, session.CopyContext(_ => true));
        if (!regenerate)
            Assert.True(session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(serial), false)));

        var accepted = session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(serial), regenerate));

        Assert.False(accepted);
    }

    [Theory]
    [InlineData("0011223344556677", "trixie", true)]
    [InlineData("ffeeddccbbaa9988", "trixie", false)]
    [InlineData("0011223344556677", "fluttershy", false)]
    public void Protocol_OnlyTrustedCurrentEvidenceAfterSubmitIsAccepted(
        string epoch,
        string petId,
        bool expected)
    {
        using var session = ReadySession(epoch: "0011223344556677");
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));

        var accepted = session.ObserveProtocol(new TrustedProtocolObservation(epoch, petId, Document0));

        Assert.Equal(expected, accepted);
        Assert.Equal(
            expected ? PersonaSessionState.ProtocolObserved : PersonaSessionState.AwaitingMarker,
            session.State);
    }

    [Theory]
    [InlineData(PersonaSessionState.NeedsActivation)]
    [InlineData(PersonaSessionState.Staged)]
    public void Protocol_BeforeAwaitingMarkerDoesNotActivate(PersonaSessionState startingState)
    {
        using var session = ReadySession(epoch: "0011223344556677");
        if (startingState == PersonaSessionState.Staged)
            StageSuccessfully(session);

        Assert.False(session.ObserveProtocol(new TrustedProtocolObservation("0011223344556677", "trixie", Document0)));
        Assert.Equal(startingState, session.State);
    }

    [Fact]
    public void Protocol_StaleRouteEvidenceIsRejected()
    {
        using var session = ReadySession(epoch: "0011223344556677");
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));

        Assert.False(session.ObserveProtocol(new TrustedProtocolObservation("0011223344556677", "trixie", Document1)));
        Assert.Equal(PersonaSessionState.AwaitingMarker, session.State);
    }

    [Fact]
    public void Route_LandingToConversationAdoptsOnlyCorrelatedPreparedSubmit()
    {
        using var session = ReadySession(route: ChatRouteKind.ProjectLanding, uri: LandingUri.AbsolutePath);
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(7), false));
        var epoch = session.Epoch;

        session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, ConversationUri.AbsolutePath, Document1));

        Assert.Equal(PersonaSessionState.AwaitingMarker, session.State);
        Assert.Equal(epoch, session.Epoch);
    }

    [Fact]
    public void Route_LandingToConversationWithoutCorrelatedSubmitRequiresReactivation()
    {
        using var session = ReadySession(route: ChatRouteKind.ProjectLanding, uri: LandingUri.AbsolutePath);
        var epoch = session.Epoch;

        session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, ConversationUri.AbsolutePath, Document1));

        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Theory]
    [InlineData("/g/different_project/c/new_chat", 1)]
    [InlineData("/g/project_opaque/c/new_chat", 3)]
    public void Route_LandingAdoptionRejectsDifferentProjectOrRevisionGap(string routeKey, long revision)
    {
        using var session = ReadySession(route: ChatRouteKind.ProjectLanding, uri: LandingUri.AbsolutePath);
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(7), false));
        var epoch = session.Epoch;

        session.ObserveContext(new PersonaChatContext(
            ChatRouteKind.ProjectConversation,
            routeKey,
            new BridgeDocumentIdentity(Document0.DocumentSession, revision)));

        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Fact]
    public void Route_LandingAdoptionRejectsDelayedTransition()
    {
        var now = TimeSpan.Zero;
        using var session = ReadySession(
            route: ChatRouteKind.ProjectLanding,
            uri: LandingUri.AbsolutePath,
            monotonicNow: () => now);
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(7), false));
        var epoch = session.Epoch;
        now = TimeSpan.FromSeconds(16);

        session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, ConversationUri.AbsolutePath, Document1));

        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Fact]
    public void Route_LandingAdoptionRejectsPreviouslyObservedConversation()
    {
        using var session = ReadySession();
        session.ObserveContext(new PersonaChatContext(
            ChatRouteKind.ProjectLanding,
            LandingUri.AbsolutePath,
            new BridgeDocumentIdentity(Document0.DocumentSession, 1)));
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(
            new BridgeDocumentIdentity(Document0.DocumentSession, 1),
            new GenerationSerial(7),
            false));
        var epoch = session.Epoch;

        session.ObserveContext(new PersonaChatContext(
            ChatRouteKind.ProjectConversation,
            ConversationUri.AbsolutePath,
            new BridgeDocumentIdentity(Document0.DocumentSession, 2)));

        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("other-chat")]
    [InlineData("unrelated")]
    public void Route_InvalidationRotatesOrDegradesAndCannotReviveOldActivation(string transition)
    {
        using var session = ReadySession();
        session.CopyContext(_ => true);
        session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));
        var epoch = session.Epoch;

        if (transition == "reload")
            session.InvalidateDocument();
        else if (transition == "other-chat")
            session.ObserveContext(new PersonaChatContext(ChatRouteKind.ProjectConversation, "/g/project_opaque/c/other", Document1));
        else
            session.ObserveContext(new PersonaChatContext(ChatRouteKind.Unrelated, "https://example.com/", null));

        Assert.NotEqual(epoch, session.Epoch);
        Assert.NotEqual(PersonaSessionState.ProtocolObserved, session.State);
    }

    [Fact]
    public void Route_ReobservingSameIneligibleContextDoesNotRotateAgain()
    {
        using var session = ReadySession();
        var unrelated = new PersonaChatContext(ChatRouteKind.Unrelated, "https://example.com/", null);

        session.ObserveContext(unrelated);
        var epoch = session.Epoch;

        session.ObserveContext(unrelated);

        Assert.Equal(PersonaSessionState.Degraded, session.State);
        Assert.Equal(epoch, session.Epoch);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Session_SubmissionObservationCapabilityLossInvalidatesActivation(
        bool afterSubmit,
        bool previouslyAvailable)
    {
        using var session = ReadySession();
        if (previouslyAvailable)
            session.ObserveSubmissionCapability(true);
        session.CopyContext(_ => true);
        if (afterSubmit)
            session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));
        var epoch = session.Epoch;

        session.ObserveSubmissionCapability(false);

        Assert.Equal(PersonaSessionState.Degraded, session.State);
        Assert.False(session.ContextCopied);
        Assert.NotEqual(epoch, session.Epoch);
    }

    [Fact]
    public void Session_SubmissionObservationCapabilityRecoveryRequiresActivationAgain()
    {
        using var session = ReadySession();
        session.ObserveSubmissionCapability(true);
        session.ObserveSubmissionCapability(false);
        var epoch = session.Epoch;

        session.ObserveSubmissionCapability(true);

        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.Equal(epoch, session.Epoch);
    }

    [Fact]
    public void Session_SelectingPersonaAfterKnownSubmissionCapabilityLossStartsDegraded()
    {
        using var session = new PersonaSession(true, "ReviewThenSend");
        session.ObserveContext(Context(ChatRouteKind.ProjectConversation, Document0));
        session.ObserveSubmissionCapability(false);

        session.SelectPack(Pack());

        Assert.Equal(PersonaSessionState.Degraded, session.State);
    }

    [Fact]
    public void SwitchingPetDuringGenerationLeavesNewPersonaNeedsActivationAndDoesNotSend()
    {
        using var session = ReadySession();
        var epoch = session.Epoch;
        var sends = 0;

        session.ObserveGeneration(GenerationCapabilityState.Generating);
        session.SelectPack(Pack(id: "fluttershy"));

        Assert.NotEqual(epoch, session.Epoch);
        Assert.Equal(PersonaSessionState.NeedsActivation, session.State);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(PersonaSessionState.NeedsActivation, "Needs activation")]
    [InlineData(PersonaSessionState.Staged, "review and send")]
    [InlineData(PersonaSessionState.AwaitingMarker, "acknowledgement")]
    [InlineData(PersonaSessionState.ProtocolObserved, "Protocol observed")]
    public void Session_StatusIsBoundedAndTruthful(PersonaSessionState target, string expectedText)
    {
        using var session = ReadySession(epoch: "0011223344556677");
        if (target is PersonaSessionState.Staged or PersonaSessionState.AwaitingMarker or PersonaSessionState.ProtocolObserved)
            StageSuccessfully(session);
        if (target is PersonaSessionState.AwaitingMarker or PersonaSessionState.ProtocolObserved)
            session.ObserveSubmission(new BridgeSubmissionObservation(Document0, new GenerationSerial(1), false));
        if (target == PersonaSessionState.ProtocolObserved)
            session.ObserveProtocol(new TrustedProtocolObservation("0011223344556677", "trixie", Document0));

        Assert.Equal(target, session.State);
        Assert.Contains(expectedText, session.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(session.StatusText.Length, 1, 160);
    }

    [Fact]
    public async Task Bridge_StageRequiresPositiveCapabilityAndKnownEmptyComposer()
    {
        using var bridge = new WebViewBridge();
        bridge.BeginDocument(ConversationUri, Document0.DocumentSession);
        var sent = false;

        var result = await bridge.StagePersonaAsync(
            new PersonaAssembler().Build(Pack(), "0011223344556677"),
            _ => sent = true,
            CancellationToken.None);

        Assert.Equal(StagePersonaResult.Unsupported, result);
        Assert.False(sent);
    }

    [Fact]
    public async Task Bridge_StageOperationContainsOnlyClosedHostOwnedContextAndNeverSend()
    {
        using var bridge = ReadyEmptyBridge();
        var context = new PersonaAssembler().Build(Pack(), "0011223344556677");
        string? operation = null;
        using var cancellation = new CancellationTokenSource();

        var pending = bridge.StagePersonaAsync(context, json => operation = json, cancellation.Token);

        Assert.NotNull(operation);
        using var parsed = JsonDocument.Parse(operation);
        Assert.Equal("stagePersona", parsed.RootElement.GetProperty("op").GetString());
        Assert.Equal(context.Text, parsed.RootElement.GetProperty("text").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("send", out _));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Bridge_MatchingStageResultCompletesOutstandingRequest()
    {
        using var bridge = ReadyEmptyBridge();
        string? operation = null;
        var pending = bridge.StagePersonaAsync(
            new PersonaAssembler().Build(Pack(), "0011223344556677"),
            json => operation = json,
            CancellationToken.None);
        var requestId = JsonDocument.Parse(operation!).RootElement.GetProperty("requestId").GetString();

        var accepted = bridge.AcceptMessage(
            ConversationUri.AbsoluteUri,
            ConversationUri,
            Envelope(Document0, "activity", $"{{\"event\":\"stageResult\",\"requestId\":\"{requestId}\",\"result\":\"staged\"}}"));

        Assert.Equal(BridgeMessageResult.Accepted, accepted);
        Assert.Equal(StagePersonaResult.Staged, await pending);
    }

    [Theory]
    [InlineData("0000000000000000")]
    [InlineData("fedcba9876543210")]
    public void Bridge_UnsolicitedOrWrongStageResultIsIgnored(string requestId)
    {
        using var bridge = ReadyEmptyBridge();

        var result = bridge.AcceptMessage(
            ConversationUri.AbsoluteUri,
            ConversationUri,
            Envelope(Document0, "activity", $"{{\"event\":\"stageResult\",\"requestId\":\"{requestId}\",\"result\":\"staged\"}}"));

        Assert.Equal(BridgeMessageResult.Unsolicited, result);
    }

    [Fact]
    public async Task Bridge_RouteAdvanceCancelsOutstandingStage()
    {
        using var bridge = ReadyEmptyBridge();
        var pending = bridge.StagePersonaAsync(
            new PersonaAssembler().Build(Pack(), "0011223344556677"),
            _ => { },
            CancellationToken.None);

        bridge.AdvanceRoute(new Uri("https://chatgpt.com/g/project_opaque/c/other"));

        Assert.Equal(StagePersonaResult.RouteChanged, await pending);
    }

    [Fact]
    public async Task Bridge_GenerationStartingCancelsOutstandingStage()
    {
        using var bridge = ReadyEmptyBridge();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = bridge.StagePersonaAsync(
            new PersonaAssembler().Build(Pack(), "0011223344556677"),
            _ => { },
            cancellation.Token);

        var result = bridge.AcceptMessage(
            ConversationUri.AbsoluteUri,
            ConversationUri,
            Envelope(Document0, "activity", "{\"event\":\"generation\",\"state\":\"generating\"}"));

        Assert.Equal(BridgeMessageResult.Accepted, result);
        Assert.Equal(StagePersonaResult.Generating, await pending);
    }

    [Fact]
    public async Task Bridge_ComposerCapabilityRevocationCancelsOutstandingStage()
    {
        using var bridge = ReadyEmptyBridge();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = bridge.StagePersonaAsync(
            new PersonaAssembler().Build(Pack(), "0011223344556677"),
            _ => { },
            cancellation.Token);

        var result = bridge.AcceptMessage(
            ConversationUri.AbsoluteUri,
            ConversationUri,
            ReadyMessage(Document0, composerEmpty: false));

        Assert.Equal(BridgeMessageResult.Accepted, result);
        Assert.Equal(StagePersonaResult.Unsupported, await pending);
    }

    [Theory]
    [InlineData("submit", false)]
    [InlineData("regenerate", true)]
    public void Bridge_EmitsOnlyTypedStructuralSubmissionActivity(string eventName, bool regenerate)
    {
        using var bridge = new WebViewBridge();
        var document = bridge.BeginDocument(ConversationUri, Document0.DocumentSession);
        BridgeSubmissionObservation? observed = null;
        bridge.SubmissionObserved += (_, value) => observed = value;

        var result = bridge.AcceptMessage(
            ConversationUri.AbsoluteUri,
            ConversationUri,
            Envelope(document, "activity", $"{{\"event\":\"{eventName}\",\"generationSerial\":3}}"));

        Assert.Equal(BridgeMessageResult.Accepted, result);
        Assert.Equal(new BridgeSubmissionObservation(document, new GenerationSerial(3), regenerate), observed);
    }

    [Fact]
    public void Bridge_RouteCapabilityResetIsTransientUntilFreshAdapterReady()
    {
        using var bridge = ReadyEmptyBridge();
        Assert.True(bridge.IsAdapterReady);
        Assert.True(bridge.Capabilities.Submission);

        var next = bridge.AdvanceRoute(new Uri("https://chatgpt.com/g/project_opaque/c/next"));

        Assert.False(bridge.IsAdapterReady);
        Assert.False(bridge.Capabilities.Submission);
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ConversationUri.AbsoluteUri, ConversationUri, ReadyMessage(next, composerEmpty: false)));
        Assert.True(bridge.IsAdapterReady);
        Assert.True(bridge.Capabilities.Submission);
    }

    [Fact]
    public void PublicSessionContractRequiresNoResponseOrDraftText()
    {
        var members = typeof(PersonaSession).GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(members, member =>
            member.Name.Contains("Response", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("DraftText", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("InnerHtml", StringComparison.OrdinalIgnoreCase));
    }

    private static PersonaSession ReadySession(
        string? epoch = null,
        ChatRouteKind route = ChatRouteKind.ProjectConversation,
        string? uri = null,
        Func<TimeSpan>? monotonicNow = null)
    {
        var epochs = epoch is null ? null : new Queue<string>([epoch, "8899aabbccddeeff", "ffeeddccbbaa9988"]);
        var session = new PersonaSession(
            roleplayEnabled: true,
            activationMode: "ReviewThenSend",
            epochFactory: epochs is null ? null : () => epochs.Dequeue(),
            monotonicNow: monotonicNow);
        session.SelectPack(Pack());
        session.ObserveContext(new PersonaChatContext(
            route,
            uri ?? (route == ChatRouteKind.ProjectLanding ? LandingUri.AbsolutePath : ConversationUri.AbsolutePath),
            Document0));
        return session;
    }

    private static PersonaChatContext Context(ChatRouteKind route, BridgeDocumentIdentity document) =>
        new(route, route switch
        {
            ChatRouteKind.ProjectLanding => LandingUri.AbsolutePath,
            ChatRouteKind.ProjectConversation => ConversationUri.AbsolutePath,
            ChatRouteKind.ChatRoot => "/",
            ChatRouteKind.Authentication => "/auth/login",
            ChatRouteKind.UnknownChatGpt => "/share/opaque",
            _ => "https://example.com/"
        }, route is ChatRouteKind.ProjectLanding or ChatRouteKind.ProjectConversation ? document : null);

    private static PersonaStageSnapshot StageSnapshot(
        bool composerCapability,
        ComposerDraftState draft,
        GenerationCapabilityState generation) =>
        new(
            new PersonaChatContext(ChatRouteKind.ProjectConversation, ConversationUri.AbsolutePath, Document0),
            new BridgeCapabilities(true, composerCapability, false, false, false, false, false),
            draft,
            generation);

    private static void StageSuccessfully(PersonaSession session)
    {
        var result = session.ApplyAsync(
            StageSnapshot(true, ComposerDraftState.Empty, GenerationCapabilityState.Idle),
            (_, _) => Task.FromResult(StagePersonaResult.Staged),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(StagePersonaResult.Staged, result);
    }

    private static WebViewBridge ReadyEmptyBridge()
    {
        var bridge = new WebViewBridge();
        var document = bridge.BeginDocument(ConversationUri, Document0.DocumentSession);
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ConversationUri.AbsoluteUri, ConversationUri, ReadyMessage(document, composerEmpty: true)));
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(
                ConversationUri.AbsoluteUri,
                ConversationUri,
                Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":true}")));
        return bridge;
    }

    private static string ReadyMessage(BridgeDocumentIdentity document, bool composerEmpty) =>
        Envelope(
            document,
            "ready",
            "{\"adapterVersion\":\"1\",\"capabilities\":{" +
            "\"generation\":true," +
            "\"composerEmpty\":" + composerEmpty.ToString().ToLowerInvariant() + "," +
            "\"pageSurface\":false,\"secondarySurface\":false," +
            "\"composerSurface\":true,\"scrollbar\":true," +
            "\"compactNavigation\":false,\"submission\":true}}");

    private static string Envelope(BridgeDocumentIdentity document, string kind, string payload) =>
        "{\"v\":1,\"kind\":\"" + kind + "\",\"documentSession\":\"" +
        document.DocumentSession + "\",\"routeRevision\":" + document.RouteRevision +
        ",\"payload\":" + payload + "}";

    private static CharacterPack Pack(
        string id = "trixie",
        string version = "1.2.3",
        string identity = "A theatrical magician",
        string voice = "Use crisp theatrical phrasing and admit uncertainty.",
        string smugMeaning = "A proud, self-satisfied reaction",
        bool hasPersona = true,
        bool hasReactions = true,
        string? rootPath = null)
    {
        rootPath ??= Path.Combine(Path.GetTempPath(), "petgpt-persona-tests", id, version);
        var profile = hasPersona
            ? new PersonaProfile(
                Path.Combine(rootPath, "persona", "profile.json"),
                Path.Combine(rootPath, "persona", "voice.md"),
                id,
                identity,
                "Skill deserves recognition",
                ["Mastery", "Honest recognition"],
                ["Clever solutions"],
                ["Condescension"],
                ["Being forgotten"],
                ["Fabricated evidence"],
                ["Theatrical exaggeration"],
                "A familiar companion and audience",
                ["Distinguish confidence from evidence"],
                "Admit uncertainty and mistakes",
                voice)
            : null;
        var idle = new CharacterClip("idle", "png", Path.Combine(rootPath, "idle.png"), "hold", true, 1, 1, null, null, null, null, null);
        var reactions = hasReactions
            ? new Dictionary<string, CharacterReaction>(StringComparer.Ordinal)
            {
                ["smug"] = new CharacterReaction("smug", smugMeaning, ["idle"], 65, 1200, [new ReactionIntensityBand(50, ["idle"])]),
                ["offended"] = new CharacterReaction("offended", "A boundary was crossed", ["idle"], 70, 1400, [])
            }
            : new Dictionary<string, CharacterReaction>(StringComparer.Ordinal);
        return new CharacterPack(
            rootPath,
            id == "legacy" ? CharacterPackSource.Bundled : CharacterPackSource.Installed,
            id,
            id,
            version,
            "2.0.0",
            profile,
            new CharacterPresentation(150, 150, 0.5, 1),
            new Dictionary<string, CharacterClip>(StringComparer.Ordinal) { ["idle"] = idle },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["idle"] = ["idle"] },
            reactions,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
    }
}
