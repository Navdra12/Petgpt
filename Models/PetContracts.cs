using System.Runtime.CompilerServices;
using PetGPT.Characters;

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

public readonly record struct GenerationSerial(long Value);

public readonly record struct TurnSerial(long Value);

public enum ChatActivity
{
    Unknown,
    Idle,
    Generating
}

public enum ReactionLane
{
    None,
    Pending,
    Playing
}

public enum PetVisualState
{
    Idle,
    ChatOpen,
    Hover,
    UserTyping,
    Reaction,
    Generating,
    Sleeping,
    Dragging,
    Exiting
}

public sealed record ValidatedReaction(
    string PetId,
    string ReactionId,
    int EffectiveIntensity,
    int VisibleMs,
    IReadOnlyList<string> AnimationCandidateIds,
    GenerationSerial Generation,
    TurnSerial Turn);

public abstract record PetEvent
{
    private PetEvent()
    {
    }

    public sealed record DragStarted() : PetEvent;
    public sealed record DragEnded() : PetEvent;
    public sealed record HoverEntered() : PetEvent;
    public sealed record HoverLeft() : PetEvent;
    public sealed record ChatVisibilityChanged(bool IsVisible) : PetEvent;
    public sealed record TypingActivity(bool IsActive) : PetEvent;
    public sealed record GenerationObserved(GenerationSerial Generation) : PetEvent;
    public sealed record GenerationIdle(GenerationSerial Generation) : PetEvent;
    public sealed record GenerationUnknown() : PetEvent;
    public sealed record NativeSendObserved(GenerationSerial Generation, TurnSerial Turn) : PetEvent;
    public sealed record ValidatedReactionReceived(ValidatedReaction Reaction) : PetEvent;
    public sealed record ReactionDisplayed(string PlaybackKey) : PetEvent;
    public sealed record SleepChanged(bool IsSleeping) : PetEvent;
    public sealed record PetChanged(CharacterPack Pack, bool ReducedMotion) : PetEvent;
    public sealed record RouteInvalidated() : PetEvent;
    public sealed record CancelCurrentActivity() : PetEvent;
    public sealed record DeadlineElapsed() : PetEvent;
    public sealed record Exit() : PetEvent;
}

public sealed record PlaybackDecision(
    PetVisualState State,
    ChatActivity ChatActivity,
    ReactionLane ReactionLane,
    CharacterClip Clip,
    string PlaybackKey,
    TimeSpan PlaybackStartedAt,
    TimeSpan? LeaseDeadline,
    TimeSpan? NextDeadline,
    string? ReactionId,
    GenerationSerial? Generation,
    TurnSerial? Turn,
    bool ReducedMotion,
    int StaticFrameIndex,
    bool RendererEnabled);
