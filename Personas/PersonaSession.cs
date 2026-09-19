using System.Diagnostics;
using System.Security.Cryptography;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Services;

namespace PetGPT.Personas;

public enum PersonaSessionState
{
    Inactive,
    NeedsActivation,
    Staged,
    AwaitingMarker,
    ProtocolObserved,
    Degraded
}

public enum PersonaCopyResult
{
    Copied,
    Unavailable,
    Failed
}

public enum StagePersonaResult
{
    Staged,
    ComposerNotEmpty,
    Generating,
    Unsupported,
    RouteChanged
}

public sealed record PersonaChatContext(
    ChatRouteKind Route,
    string RouteKey,
    BridgeDocumentIdentity? Document);

public sealed record PersonaStageSnapshot(
    PersonaChatContext Context,
    BridgeCapabilities Capabilities,
    ComposerDraftState ComposerDraft,
    GenerationCapabilityState Generation);

public sealed record BridgeSubmissionObservation(
    BridgeDocumentIdentity Document,
    GenerationSerial Generation,
    bool IsRegenerate);

public sealed record TrustedProtocolObservation(
    string Epoch,
    string PetId,
    BridgeDocumentIdentity Document);

public sealed class PersonaSession : IDisposable
{
    private static readonly TimeSpan LandingAdoptionWindow = TimeSpan.FromSeconds(15);
    private readonly PersonaAssembler _assembler;
    private readonly Func<string> _epochFactory;
    private readonly Stopwatch? _clock;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly HashSet<string> _observedConversationKeys = new(StringComparer.Ordinal);
    private bool _roleplayEnabled;
    private string _activationMode;
    private CharacterPack? _pack;
    private PersonaChatContext? _route;
    private GenerationCapabilityState _generation;
    private bool _submissionCapabilityKnown;
    private bool _submissionObservationAvailable;
    private GenerationSerial? _lastSubmit;
    private PendingLandingSubmission? _pendingLandingSubmit;
    private bool _hasObservedRoute;
    private bool _disposed;

    public PersonaSession(
        bool roleplayEnabled,
        string activationMode,
        PersonaAssembler? assembler = null,
        Func<string>? epochFactory = null,
        Func<TimeSpan>? monotonicNow = null)
    {
        _roleplayEnabled = roleplayEnabled;
        _activationMode = activationMode ?? string.Empty;
        _assembler = assembler ?? new PersonaAssembler();
        _epochFactory = epochFactory ?? CreateEpoch;
        if (monotonicNow is null)
        {
            _clock = Stopwatch.StartNew();
            _monotonicNow = () => _clock.Elapsed;
        }
        else
        {
            _monotonicNow = monotonicNow;
        }
        SetState(PersonaSessionState.Inactive, InactiveStatus());
    }

    public PersonaSessionState State { get; private set; }
    public PersonaContext? Context { get; private set; }
    public string? Epoch => Context?.Epoch;
    public bool ContextCopied { get; private set; }
    public string StatusText { get; private set; } = "Persona inactive";
    public bool CanReview => Context is not null;
    public bool CanCopy => Context is not null && State == PersonaSessionState.NeedsActivation;
    public bool CanApply =>
        Context is not null &&
        _route is not null &&
        IsActivatableRoute(_route) &&
        State is PersonaSessionState.NeedsActivation or
            PersonaSessionState.AwaitingMarker or
            PersonaSessionState.ProtocolObserved;

    public event EventHandler? StatusChanged;

    public void UpdateRoleplay(bool enabled, string activationMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_roleplayEnabled == enabled &&
            _activationMode.Equals(activationMode, StringComparison.Ordinal))
        {
            return;
        }

        _roleplayEnabled = enabled;
        _activationMode = activationMode ?? string.Empty;
        if (!IsRoleplayConfigured())
        {
            Context = null;
            ContextCopied = false;
            _lastSubmit = null;
            _pendingLandingSubmit = null;
            SetState(PersonaSessionState.Inactive, InactiveStatus());
            return;
        }

        RebuildForNewIdentity(force: true);
    }

    public void SelectPack(CharacterPack pack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;

        if (!IsRoleplayConfigured() || pack.Persona is null || pack.Reactions.Count == 0)
        {
            Context = null;
            ContextCopied = false;
            _lastSubmit = null;
            _pendingLandingSubmit = null;
            SetState(PersonaSessionState.Inactive, InactiveStatus());
            return;
        }

        var candidate = Assemble(NewEpoch());
        if (candidate is null)
            return;
        if (Context is not null &&
            Context.CharacterId.Equals(candidate.CharacterId, StringComparison.Ordinal) &&
            Context.PackVersion.Equals(candidate.PackVersion, StringComparison.Ordinal) &&
            Context.SourceFingerprint.Equals(candidate.SourceFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        Context = candidate;
        ContextCopied = false;
        _lastSubmit = null;
        _pendingLandingSubmit = null;
        SetStateForCurrentRoute();
    }

    public void ObserveContext(PersonaChatContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        _hasObservedRoute = true;
        var conversationWasObserved = context.Route == ChatRouteKind.ProjectConversation &&
            _observedConversationKeys.Contains(context.RouteKey);
        RememberConversation(context);

        if (Context is null)
        {
            _route = context;
            SetState(PersonaSessionState.Inactive, InactiveStatus());
            return;
        }

        if (_route is not null && SameContext(_route, context))
            return;

        if (!IsActivatableRoute(context))
        {
            if (_route is not null)
                RotateContext();
            _route = context;
            SetState(PersonaSessionState.Degraded, "Persona activation unavailable on this page");
            return;
        }

        if (_route is null)
        {
            _route = context;
            SetStateForCurrentRoute();
            return;
        }

        if (_route.Route == ChatRouteKind.ProjectLanding &&
            context.Route == ChatRouteKind.ProjectConversation &&
            _route.Document is not null &&
            context.Document is not null &&
            _route.Document.DocumentSession.Equals(context.Document.DocumentSession, StringComparison.Ordinal) &&
            State == PersonaSessionState.AwaitingMarker &&
            _pendingLandingSubmit is { } pending &&
            !conversationWasObserved &&
            _lastSubmit == pending.Generation &&
            pending.Document == _route.Document &&
            pending.Document.RouteRevision < long.MaxValue &&
            context.Document.RouteRevision == pending.Document.RouteRevision + 1 &&
            TryGetProjectKey(context.RouteKey, out var destinationProject) &&
            destinationProject.Equals(pending.ProjectKey, StringComparison.Ordinal) &&
            IsWithinLandingAdoptionWindow(pending))
        {
            _route = context;
            _pendingLandingSubmit = null;
            RaiseStatusChanged();
            return;
        }

        _route = context;
        RotateContext();
        SetStateForCurrentRoute();
    }

    public void InvalidateDocument()
    {
        if (_disposed)
            return;
        _hasObservedRoute = true;
        if (Context is not null && _route is not null)
            RotateContext();
        _route = null;
        _submissionCapabilityKnown = false;
        _submissionObservationAvailable = false;
        if (Context is not null)
            SetState(PersonaSessionState.Degraded, "Persona activation unavailable while the chat reloads");
    }

    public void ObserveGeneration(GenerationCapabilityState generation)
    {
        if (_disposed)
            return;
        _generation = generation;
    }

    public void ObserveSubmissionCapability(bool available)
    {
        if (_disposed)
            return;

        var wasAvailable = _submissionObservationAvailable;
        _submissionCapabilityKnown = true;
        _submissionObservationAvailable = available;
        if (available)
        {
            if (!wasAvailable && Context is not null && _route is not null && IsActivatableRoute(_route) &&
                State == PersonaSessionState.Degraded)
            {
                SetState(PersonaSessionState.NeedsActivation, "Character selected locally — Needs activation");
            }
            return;
        }

        var hasActivationEvidence = ContextCopied ||
            State is PersonaSessionState.Staged or
                PersonaSessionState.AwaitingMarker or
                PersonaSessionState.ProtocolObserved;
        if ((wasAvailable || hasActivationEvidence) &&
            Context is not null && _route is not null && IsActivatableRoute(_route))
        {
            RotateContext();
        }
        if (Context is not null && _route is not null && IsActivatableRoute(_route))
            SetState(PersonaSessionState.Degraded, "Persona activation unavailable — submission observation lost");
    }

    public void RestartActivation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Context is null || _pack is null)
            return;
        RotateContext();
        SetStateForCurrentRoute();
    }

    public PersonaCopyResult CopyContext(Func<string, bool> writeClipboard)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeClipboard);
        if (!CanCopy || Context is null)
            return PersonaCopyResult.Unavailable;

        try
        {
            if (!writeClipboard(Context.Text))
            {
                SetState(PersonaSessionState.NeedsActivation, "Context copy failed — Needs activation");
                return PersonaCopyResult.Failed;
            }
        }
        catch
        {
            SetState(PersonaSessionState.NeedsActivation, "Context copy failed — Needs activation");
            return PersonaCopyResult.Failed;
        }

        ContextCopied = true;
        SetState(PersonaSessionState.NeedsActivation, "Context copied — paste and send manually");
        return PersonaCopyResult.Copied;
    }

    public async Task<StagePersonaResult> ApplyAsync(
        PersonaStageSnapshot snapshot,
        Func<PersonaContext, CancellationToken, Task<StagePersonaResult>> stageAsync,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stageAsync);
        if (!CanApply || Context is null || _route is null || !SameContext(_route, snapshot.Context))
            return StagePersonaResult.RouteChanged;
        if (State is PersonaSessionState.AwaitingMarker or PersonaSessionState.ProtocolObserved)
            RestartActivation();
        if (!snapshot.Capabilities.ComposerEmpty || snapshot.ComposerDraft == ComposerDraftState.Unknown)
        {
            SetState(PersonaSessionState.NeedsActivation, "Safe staging unavailable — use Copy context");
            return StagePersonaResult.Unsupported;
        }
        if (snapshot.ComposerDraft == ComposerDraftState.NonEmpty)
        {
            SetState(PersonaSessionState.NeedsActivation, "Composer is not empty — use Copy context without overwriting it");
            return StagePersonaResult.ComposerNotEmpty;
        }
        if (snapshot.Generation == GenerationCapabilityState.Generating ||
            _generation == GenerationCapabilityState.Generating)
        {
            SetState(PersonaSessionState.NeedsActivation, "Wait for generation to finish before applying the character");
            return StagePersonaResult.Generating;
        }

        var expectedContext = Context;
        var expectedRoute = _route;
        var result = await stageAsync(expectedContext, cancellationToken);
        if (_disposed || !ReferenceEquals(Context, expectedContext) || _route is null || !SameContext(_route, expectedRoute))
            return StagePersonaResult.RouteChanged;

        switch (result)
        {
            case StagePersonaResult.Staged:
                ContextCopied = false;
                SetState(PersonaSessionState.Staged, "Context staged — review and send with ChatGPT");
                break;
            case StagePersonaResult.ComposerNotEmpty:
                SetState(PersonaSessionState.NeedsActivation, "Composer is not empty — context was not staged");
                break;
            case StagePersonaResult.Generating:
                SetState(PersonaSessionState.NeedsActivation, "Wait for generation to finish before applying the character");
                break;
            case StagePersonaResult.Unsupported:
                SetState(PersonaSessionState.NeedsActivation, "Safe staging unavailable — use Copy context");
                break;
            case StagePersonaResult.RouteChanged:
                SetStateForCurrentRoute();
                break;
        }
        return result;
    }

    public bool ObserveSubmission(BridgeSubmissionObservation observation)
    {
        if (_disposed || observation.IsRegenerate || observation.Generation.Value <= 0 ||
            Context is null || _route?.Document is null ||
            _route.Document != observation.Document ||
            _lastSubmit == observation.Generation ||
            State is not PersonaSessionState.Staged &&
            !(State == PersonaSessionState.NeedsActivation && ContextCopied))
        {
            return false;
        }

        _lastSubmit = observation.Generation;
        if (_route.Route == ChatRouteKind.ProjectLanding &&
            TryGetProjectKey(_route.RouteKey, out var projectKey))
        {
            _pendingLandingSubmit = new PendingLandingSubmission(
                observation.Generation,
                observation.Document,
                projectKey,
                _monotonicNow());
        }
        ContextCopied = false;
        SetState(PersonaSessionState.AwaitingMarker, "Waiting for character acknowledgement");
        return true;
    }

    public bool ObserveProtocol(TrustedProtocolObservation observation)
    {
        if (_disposed || Context is null || _route?.Document is null ||
            State != PersonaSessionState.AwaitingMarker ||
            !Context.Epoch.Equals(observation.Epoch, StringComparison.Ordinal) ||
            !Context.CharacterId.Equals(observation.PetId, StringComparison.Ordinal) ||
            _route.Document != observation.Document)
        {
            return false;
        }

        SetState(PersonaSessionState.ProtocolObserved, "Protocol observed — current character context acknowledged");
        return true;
    }

    private void RebuildForNewIdentity(bool force)
    {
        if (_pack is null || _pack.Persona is null || _pack.Reactions.Count == 0)
        {
            Context = null;
            SetState(PersonaSessionState.Inactive, InactiveStatus());
            return;
        }

        var candidate = Assemble(NewEpoch());
        if (candidate is null)
            return;
        if (!force && Context?.SourceFingerprint == candidate.SourceFingerprint)
            return;
        Context = candidate;
        ContextCopied = false;
        _lastSubmit = null;
        _pendingLandingSubmit = null;
        SetStateForCurrentRoute();
    }

    private void RotateContext()
    {
        if (_pack is null)
            return;
        var candidate = Assemble(NewEpoch());
        if (candidate is not null)
            Context = candidate;
        ContextCopied = false;
        _lastSubmit = null;
        _pendingLandingSubmit = null;
    }

    private PersonaContext? Assemble(string epoch)
    {
        try
        {
            return _assembler.Build(_pack!, epoch);
        }
        catch (PersonaAssemblyException exception)
        {
            Context = null;
            SetState(PersonaSessionState.Degraded, exception.DiagnosticCode switch
            {
                "persona_context_too_large" => "Persona context exceeds the safe activation size",
                _ => "Persona activation source is unavailable"
            });
            return null;
        }
    }

    private void SetStateForCurrentRoute()
    {
        if (Context is null)
        {
            SetState(PersonaSessionState.Inactive, InactiveStatus());
        }
        else if (_route is not null && IsActivatableRoute(_route) &&
            _submissionCapabilityKnown && !_submissionObservationAvailable)
        {
            SetState(PersonaSessionState.Degraded, "Persona activation unavailable — submission observation lost");
        }
        else if (_route is not null && IsActivatableRoute(_route))
        {
            SetState(PersonaSessionState.NeedsActivation, "Character selected locally — Needs activation");
        }
        else if (_hasObservedRoute)
        {
            SetState(PersonaSessionState.Degraded, "Persona activation unavailable on this page");
        }
        else
        {
            SetState(PersonaSessionState.Inactive, "Character selected locally — open a PetChat to activate");
        }
    }

    private string InactiveStatus()
    {
        if (!_roleplayEnabled)
            return "Roleplay disabled — character selected locally only";
        if (!_activationMode.Equals("ReviewThenSend", StringComparison.Ordinal))
            return "Roleplay activation mode unavailable";
        if (_pack?.Persona is null || _pack.Reactions.Count == 0)
            return "Character selected locally — persona unavailable";
        return "Persona inactive";
    }

    private bool IsRoleplayConfigured() =>
        _roleplayEnabled && _activationMode.Equals("ReviewThenSend", StringComparison.Ordinal);

    private static bool IsActivatableRoute(PersonaChatContext context) =>
        context.Document is not null &&
        context.Route is ChatRouteKind.ProjectLanding or ChatRouteKind.ProjectConversation;

    private static bool SameContext(PersonaChatContext left, PersonaChatContext right) =>
        left.Route == right.Route &&
        left.RouteKey.Equals(right.RouteKey, StringComparison.Ordinal) &&
        left.Document == right.Document;

    private void RememberConversation(PersonaChatContext context)
    {
        if (context.Route == ChatRouteKind.ProjectConversation)
            _observedConversationKeys.Add(context.RouteKey);
    }

    private static bool TryGetProjectKey(string routeKey, out string projectKey)
    {
        projectKey = string.Empty;
        var segments = routeKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 ||
            !segments[0].Equals("g", StringComparison.Ordinal) ||
            segments[1].Length == 0 ||
            segments[2] is not ("project" or "c"))
        {
            return false;
        }

        projectKey = segments[1];
        return true;
    }

    private bool IsWithinLandingAdoptionWindow(PendingLandingSubmission pending)
    {
        var age = _monotonicNow() - pending.ObservedAt;
        return age >= TimeSpan.Zero && age <= LandingAdoptionWindow;
    }

    private string NewEpoch()
    {
        var epoch = _epochFactory();
        if (epoch.Length != 16 || epoch.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new InvalidOperationException("Epoch factory returned an invalid value.");
        return epoch;
    }

    private static string CreateEpoch() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private void SetState(PersonaSessionState state, string status)
    {
        var changed = State != state || !StatusText.Equals(status, StringComparison.Ordinal);
        State = state;
        StatusText = status.Length <= 160 ? status : status[..160];
        if (changed)
            RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Context = null;
        _route = null;
        ContextCopied = false;
        _lastSubmit = null;
        _pendingLandingSubmit = null;
        State = PersonaSessionState.Inactive;
        StatusText = "Persona session closed";
        StatusChanged = null;
    }

    private sealed record PendingLandingSubmission(
        GenerationSerial Generation,
        BridgeDocumentIdentity Document,
        string ProjectKey,
        TimeSpan ObservedAt);
}
