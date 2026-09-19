using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Personas;
using PetGPT.Services;

namespace PetGPT.Reactions;

public sealed record BridgeReactionObservation(
    BridgeDocumentIdentity Document,
    string Href,
    GenerationSerial Generation,
    string AssistantId);

public sealed record ReactionCorrelation(
    BridgeDocumentIdentity Document,
    GenerationSerial Generation,
    TurnSerial Turn,
    string AssistantId,
    bool WasBaselined,
    bool AlreadyDispatched);

public sealed record ReactionValidationContext(
    bool ReactionsEnabled,
    CharacterPack Pack,
    PersonaSessionState PersonaState,
    PersonaContext? Persona,
    BridgeDocumentIdentity? CurrentDocument,
    ReactionCorrelation Correlation,
    GenerationSerial? CurrentGeneration = null);

public static class ReactionValidator
{
    public static bool TryValidate(
        ParsedReactionMarker marker,
        ReactionValidationContext context,
        out ValidatedReaction? reaction)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(context);
        reaction = null;

        if (!context.ReactionsEnabled ||
            context.Persona is null ||
            context.PersonaState is not (PersonaSessionState.AwaitingMarker or PersonaSessionState.ProtocolObserved) ||
            context.CurrentDocument is null ||
            context.CurrentDocument != context.Correlation.Document ||
            context.Correlation.Generation.Value <= 0 ||
            context.Correlation.Turn.Value <= 0 ||
            string.IsNullOrEmpty(context.Correlation.AssistantId) ||
            context.Correlation.AssistantId.Length > 128 ||
            context.Correlation.WasBaselined ||
            context.Correlation.AlreadyDispatched ||
            context.CurrentGeneration is { } currentGeneration && currentGeneration != context.Correlation.Generation ||
            !marker.Epoch.Equals(context.Persona.Epoch, StringComparison.Ordinal) ||
            !marker.PetId.Equals(context.Pack.Id, StringComparison.Ordinal) ||
            !marker.PetId.Equals(context.Persona.CharacterId, StringComparison.Ordinal) ||
            !context.Pack.Reactions.TryGetValue(marker.ReactionId, out var configured))
        {
            return false;
        }

        PersonaContext currentPackContext;
        try
        {
            currentPackContext = new PersonaAssembler().Build(context.Pack, context.Persona.Epoch);
        }
        catch
        {
            return false;
        }
        if (!currentPackContext.SourceFingerprint.Equals(context.Persona.SourceFingerprint, StringComparison.Ordinal) ||
            !currentPackContext.PackVersion.Equals(context.Persona.PackVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var effectiveIntensity = marker.Intensity ?? configured.DefaultIntensity;
        var band = configured.IntensityBands
            .Where(candidate => candidate.Minimum <= effectiveIntensity)
            .OrderByDescending(candidate => candidate.Minimum)
            .FirstOrDefault();
        var candidates = band?.AnimationCandidates ?? configured.AnimationCandidates;
        reaction = new ValidatedReaction(
            context.Pack.Id,
            configured.Id,
            effectiveIntensity,
            configured.VisibleMs,
            Array.AsReadOnly(candidates.ToArray()),
            context.Correlation.Generation,
            context.Correlation.Turn);
        return true;
    }
}

public sealed class ReactionLiveTurnCoordinator
{
    private BridgeDocumentIdentity? _document;
    private GenerationSerial? _generation;
    private TurnSerial? _turn;
    private string? _assistantId;
    private bool _dispatched;
    private bool _generationConfirmed;
    private GenerationCapabilityState _lastGenerationState = GenerationCapabilityState.Unknown;
    private bool _generationStatePublished;
    private long _nextTurn;

    public GenerationSerial? CurrentGeneration => _generation;
    public BridgeDocumentIdentity? CurrentDocument => _document;

    public PetEvent.NativeSendObserved ObserveSubmission(BridgeSubmissionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Generation.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(observation));

        _document = observation.Document;
        _generation = observation.Generation;
        _turn = new TurnSerial(checked(++_nextTurn));
        _assistantId = null;
        _dispatched = false;
        _generationConfirmed = false;
        _lastGenerationState = GenerationCapabilityState.Unknown;
        _generationStatePublished = false;
        return new PetEvent.NativeSendObserved(observation.Generation, _turn.Value);
    }

    public bool TryAdvanceRoute(BridgeDocumentIdentity document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_document is null || _generation is null || _turn is null ||
            !_document.DocumentSession.Equals(document.DocumentSession, StringComparison.Ordinal) ||
            _document.RouteRevision == long.MaxValue ||
            document.RouteRevision != _document.RouteRevision + 1)
        {
            Invalidate();
            return false;
        }
        _document = document;
        return true;
    }

    public PetEvent? ObserveGeneration(GenerationCapabilityState state)
    {
        if (_generationStatePublished && state == _lastGenerationState)
            return null;
        _lastGenerationState = state;
        _generationStatePublished = true;
        if (state == GenerationCapabilityState.Unknown || _generation is null)
            return new PetEvent.GenerationUnknown();
        if (state == GenerationCapabilityState.Generating)
        {
            _generationConfirmed = true;
            return new PetEvent.GenerationObserved(_generation.Value);
        }
        if (state == GenerationCapabilityState.Idle && _generationConfirmed)
            return new PetEvent.GenerationIdle(_generation.Value);
        return null;
    }

    public bool TryCorrelate(BridgeReactionObservation observation, out ReactionCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        correlation = default!;
        if (_document is null || _generation is null || _turn is null ||
            observation.Document != _document ||
            observation.Generation != _generation ||
            string.IsNullOrEmpty(observation.AssistantId) || observation.AssistantId.Length > 128 ||
            _dispatched ||
            _assistantId is not null && !_assistantId.Equals(observation.AssistantId, StringComparison.Ordinal))
        {
            return false;
        }

        _assistantId ??= observation.AssistantId;
        correlation = new ReactionCorrelation(
            _document,
            _generation.Value,
            _turn.Value,
            _assistantId,
            WasBaselined: false,
            AlreadyDispatched: false);
        return true;
    }

    public void Commit(ReactionCorrelation correlation)
    {
        if (_document == correlation.Document &&
            _generation == correlation.Generation &&
            _turn == correlation.Turn &&
            _assistantId == correlation.AssistantId)
        {
            _dispatched = true;
        }
    }

    public void Invalidate()
    {
        _document = null;
        _generation = null;
        _turn = null;
        _assistantId = null;
        _dispatched = false;
        _generationConfirmed = false;
        _lastGenerationState = GenerationCapabilityState.Unknown;
        _generationStatePublished = false;
    }
}
