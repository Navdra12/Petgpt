using PetGPT.Characters;
using PetGPT.Models;

namespace PetGPT.Animation;

public sealed class AnimationStateEngine
{
    private static readonly TimeSpan TypingDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProvisionalGenerationDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingReactionDuration = TimeSpan.FromSeconds(10);

    private CharacterPack _pack;
    private bool _reducedMotion;
    private bool _sleeping;
    private bool _exiting;
    private bool _dragging;
    private bool _hover;
    private bool _chatVisible;
    private ChatActivity _chatActivity = ChatActivity.Unknown;
    private TimeSpan? _typingUntil;
    private TimeSpan? _provisionalGeneratingUntil;
    private GenerationSerial? _generation;
    private TurnSerial? _turn;
    private ReactionState? _reaction;
    private TurnSerial? _latestReactionTurn;
    private PlaybackDecision? _lastDecision;
    private long _playbackSerial;
    private long _reactionSerial;

    public AnimationStateEngine(CharacterPack pack, bool reducedMotion)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _reducedMotion = reducedMotion;
    }

    public PlaybackDecision Current(TimeSpan monotonicNow)
    {
        Expire(monotonicNow);
        return Decide(monotonicNow);
    }

    public PlaybackDecision Apply(PetEvent petEvent, TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(petEvent);
        Expire(monotonicNow);

        if (_exiting)
            return Decide(monotonicNow);

        switch (petEvent)
        {
            case PetEvent.DragStarted:
                _dragging = true;
                break;
            case PetEvent.DragEnded:
                _dragging = false;
                break;
            case PetEvent.HoverEntered:
                _hover = true;
                break;
            case PetEvent.HoverLeft:
                _hover = false;
                break;
            case PetEvent.ChatVisibilityChanged visibility:
                _chatVisible = visibility.IsVisible;
                break;
            case PetEvent.TypingActivity typing:
                _typingUntil = typing.IsActive ? monotonicNow + TypingDuration : null;
                break;
            case PetEvent.NativeSendObserved send:
                DropReaction();
                _generation = send.Generation;
                _turn = send.Turn;
                _chatActivity = ChatActivity.Unknown;
                _provisionalGeneratingUntil = monotonicNow + ProvisionalGenerationDuration;
                break;
            case PetEvent.GenerationObserved observed:
                var currentGeneration = _generation;
                var isNewerGeneration = !currentGeneration.HasValue ||
                    observed.Generation.Value > currentGeneration.GetValueOrDefault().Value;
                if (isNewerGeneration || currentGeneration == observed.Generation)
                {
                    if (isNewerGeneration)
                    {
                        if (_reaction?.Reaction.Generation != observed.Generation)
                            DropReaction();
                        _turn = null;
                    }
                    _generation = observed.Generation;
                    _chatActivity = ChatActivity.Generating;
                    _provisionalGeneratingUntil = null;
                }
                break;
            case PetEvent.GenerationIdle idle:
                if (_chatActivity == ChatActivity.Generating &&
                    (!_generation.HasValue || _generation.Value == idle.Generation))
                {
                    _generation = idle.Generation;
                    _chatActivity = ChatActivity.Idle;
                    _provisionalGeneratingUntil = null;
                }
                break;
            case PetEvent.GenerationUnknown:
                _chatActivity = ChatActivity.Unknown;
                break;
            case PetEvent.ValidatedReactionReceived received:
                AcceptReaction(received.Reaction, monotonicNow);
                break;
            case PetEvent.ReactionDisplayed displayed:
                if (_reaction is { Lane: ReactionLane.Pending } reaction &&
                    reaction.PlaybackKey.Equals(displayed.PlaybackKey, StringComparison.Ordinal) &&
                    SelectState(monotonicNow) == PetVisualState.Reaction)
                {
                    reaction.Lane = ReactionLane.Playing;
                    reaction.PlaybackStartedAt = monotonicNow;
                    reaction.LeaseDeadline = monotonicNow +
                        TimeSpan.FromMilliseconds(reaction.Reaction.VisibleMs);
                }
                break;
            case PetEvent.SleepChanged sleep:
                _sleeping = sleep.IsSleeping;
                if (_sleeping)
                    DropReaction();
                break;
            case PetEvent.PetChanged changed:
                _pack = changed.Pack ?? throw new ArgumentNullException(nameof(changed.Pack));
                _reducedMotion = changed.ReducedMotion;
                DropReaction();
                _generation = null;
                _turn = null;
                _latestReactionTurn = null;
                _chatActivity = ChatActivity.Unknown;
                _provisionalGeneratingUntil = null;
                _typingUntil = null;
                _lastDecision = null;
                break;
            case PetEvent.RouteInvalidated:
                DropReaction();
                _generation = null;
                _turn = null;
                _chatActivity = ChatActivity.Unknown;
                _provisionalGeneratingUntil = null;
                break;
            case PetEvent.CancelCurrentActivity:
                DropReaction();
                break;
            case PetEvent.DeadlineElapsed:
                break;
            case PetEvent.Exit:
                _exiting = true;
                _provisionalGeneratingUntil = null;
                _typingUntil = null;
                DropReaction();
                break;
        }

        Expire(monotonicNow);
        return Decide(monotonicNow);
    }

    private void AcceptReaction(ValidatedReaction reaction, TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        if (_sleeping || _exiting ||
            !reaction.PetId.Equals(_pack.Id, StringComparison.Ordinal) ||
            (_latestReactionTurn.HasValue && reaction.Turn.Value <= _latestReactionTurn.Value.Value) ||
            (_generation.HasValue && reaction.Generation != _generation.Value) ||
            (_turn.HasValue && reaction.Turn != _turn.Value))
        {
            return;
        }

        _latestReactionTurn = reaction.Turn;
        _reaction = new ReactionState(
            reaction,
            monotonicNow,
            $"{_pack.Id}@{_pack.Version}:reaction:{reaction.Turn.Value}:{reaction.ReactionId}:{++_reactionSerial}");
    }

    private void Expire(TimeSpan monotonicNow)
    {
        if (_typingUntil.HasValue && monotonicNow >= _typingUntil.Value)
            _typingUntil = null;

        if (_provisionalGeneratingUntil.HasValue && monotonicNow >= _provisionalGeneratingUntil.Value)
            _provisionalGeneratingUntil = null;

        if (_reaction is null)
            return;

        if (_reaction.Lane == ReactionLane.Pending &&
            monotonicNow >= _reaction.ReceivedAt + PendingReactionDuration)
        {
            DropReaction();
            return;
        }

        if (_reaction.LeaseDeadline.HasValue && monotonicNow >= _reaction.LeaseDeadline.Value)
            DropReaction();
    }

    private PlaybackDecision Decide(TimeSpan monotonicNow)
    {
        var state = SelectState(monotonicNow);
        var clip = state == PetVisualState.Reaction && _reaction is not null
            ? ResolveClip(_reaction.Reaction.AnimationCandidateIds)
            : ResolveSystemClip(state);
        var playback = GetPlaybackIdentity(state, clip, monotonicNow);
        var nextDeadline = GetNextDeadline(monotonicNow);

        var decision = new PlaybackDecision(
            state,
            _chatActivity,
            _reaction?.Lane ?? ReactionLane.None,
            clip,
            playback.Key,
            playback.StartedAt,
            _reaction?.LeaseDeadline,
            nextDeadline,
            _reaction?.Reaction.ReactionId,
            _generation,
            _turn,
            _reducedMotion,
            0,
            state != PetVisualState.Exiting);
        _lastDecision = decision;
        return decision;
    }

    private PetVisualState SelectState(TimeSpan monotonicNow)
    {
        if (_exiting)
            return PetVisualState.Exiting;
        if (_dragging)
            return PetVisualState.Dragging;
        if (_sleeping)
            return PetVisualState.Sleeping;
        if (_chatActivity == ChatActivity.Generating ||
            (_provisionalGeneratingUntil.HasValue && monotonicNow < _provisionalGeneratingUntil.Value))
        {
            return PetVisualState.Generating;
        }
        if (_reaction is not null)
            return PetVisualState.Reaction;
        if (_typingUntil.HasValue && monotonicNow < _typingUntil.Value)
            return PetVisualState.UserTyping;
        if (_hover)
            return PetVisualState.Hover;
        if (_chatVisible)
            return PetVisualState.ChatOpen;
        return PetVisualState.Idle;
    }

    private CharacterClip ResolveSystemClip(PetVisualState state)
    {
        var mapping = state switch
        {
            PetVisualState.Dragging => "dragging",
            PetVisualState.Sleeping => "sleep",
            PetVisualState.Generating => "generating",
            PetVisualState.UserTyping => "userTyping",
            PetVisualState.Hover => "hover",
            PetVisualState.ChatOpen => "chatOpen",
            _ => "idle"
        };

        return _pack.SystemAnimations.TryGetValue(mapping, out var candidates)
            ? ResolveClip(candidates)
            : RequiredIdle();
    }

    private CharacterClip ResolveClip(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (_pack.Clips.TryGetValue(candidate, out var clip) && clip.IsAvailable)
                return clip;
        }

        return RequiredIdle();
    }

    private CharacterClip RequiredIdle()
    {
        if (_pack.Clips.TryGetValue("idle", out var idle) && idle.IsAvailable)
            return idle;
        throw new InvalidOperationException("The active validated pack has no usable idle clip.");
    }

    private (string Key, TimeSpan StartedAt) GetPlaybackIdentity(
        PetVisualState state,
        CharacterClip clip,
        TimeSpan monotonicNow)
    {
        if (state == PetVisualState.Reaction && _reaction is not null)
            return (_reaction.PlaybackKey, _reaction.PlaybackStartedAt ?? monotonicNow);

        if (_lastDecision is not null &&
            _lastDecision.State == state &&
            _lastDecision.Clip.Id.Equals(clip.Id, StringComparison.Ordinal) &&
            _lastDecision.RendererEnabled == (state != PetVisualState.Exiting))
        {
            return (_lastDecision.PlaybackKey, _lastDecision.PlaybackStartedAt);
        }

        return ($"{_pack.Id}@{_pack.Version}:{state}:{++_playbackSerial}", monotonicNow);
    }

    private TimeSpan? GetNextDeadline(TimeSpan monotonicNow)
    {
        if (_exiting)
            return null;

        TimeSpan? next = null;
        AddDeadline(_typingUntil);
        AddDeadline(_provisionalGeneratingUntil);
        if (_reaction is { Lane: ReactionLane.Pending } pending)
            AddDeadline(pending.ReceivedAt + PendingReactionDuration);
        if (_reaction?.LeaseDeadline is { } leaseDeadline)
            AddDeadline(leaseDeadline);
        return next;

        void AddDeadline(TimeSpan? candidate)
        {
            if (!candidate.HasValue || candidate.Value <= monotonicNow)
                return;
            if (!next.HasValue || candidate.Value < next.Value)
                next = candidate.Value;
        }
    }

    private void DropReaction() => _reaction = null;

    private sealed class ReactionState(
        ValidatedReaction reaction,
        TimeSpan receivedAt,
        string playbackKey)
    {
        public ValidatedReaction Reaction { get; } = reaction;
        public TimeSpan ReceivedAt { get; } = receivedAt;
        public string PlaybackKey { get; } = playbackKey;
        public ReactionLane Lane { get; set; } = ReactionLane.Pending;
        public TimeSpan? PlaybackStartedAt { get; set; }
        public TimeSpan? LeaseDeadline { get; set; }
    }
}
