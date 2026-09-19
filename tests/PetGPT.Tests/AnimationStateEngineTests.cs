using System.Windows.Media;
using System.Windows.Media.Imaging;
using PetGPT.Animation;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Shell;
using PetGPT.Windows;
using Xunit;

namespace PetGPT.Tests;

public sealed class AnimationStateEngineTests
{
    [Fact]
    public void DefaultDecisionIsIdle()
    {
        var engine = Engine();

        var decision = engine.Current(T(0));

        Assert.Equal(PetVisualState.Idle, decision.State);
        Assert.Equal("idle", decision.Clip.Id);
    }

    [Fact]
    public void ChatOpenOverridesIdle()
    {
        var decision = Apply(Engine(), T(1), new PetEvent.ChatVisibilityChanged(true));

        Assert.Equal(PetVisualState.ChatOpen, decision.State);
        Assert.Equal("chat", decision.Clip.Id);
    }

    [Fact]
    public void HoverOverridesChatOpen()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ChatVisibilityChanged(true));

        var decision = Apply(engine, T(1), new PetEvent.HoverEntered());

        Assert.Equal(PetVisualState.Hover, decision.State);
    }

    [Fact]
    public void TypingOverridesHover()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.HoverEntered());

        var decision = Apply(engine, T(1), new PetEvent.TypingActivity(true));

        Assert.Equal(PetVisualState.UserTyping, decision.State);
    }

    [Fact]
    public void ReactionOverridesTyping()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.TypingActivity(true));

        var decision = Apply(engine, T(1), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        Assert.Equal(PetVisualState.Reaction, decision.State);
    }

    [Fact]
    public void ConfirmedGeneratingOverridesReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.GenerationObserved(new GenerationSerial(1)));

        Assert.Equal(PetVisualState.Generating, decision.State);
    }

    [Fact]
    public void SleepingOverridesGeneratingAndDropsReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));
        Apply(engine, T(1), new PetEvent.GenerationObserved(new GenerationSerial(1)));

        var decision = Apply(engine, T(2), new PetEvent.SleepChanged(true));

        Assert.Equal(PetVisualState.Sleeping, decision.State);
        Assert.Equal(ReactionLane.None, decision.ReactionLane);
    }

    [Fact]
    public void DraggingOverridesSleeping()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.SleepChanged(true));

        var decision = Apply(engine, T(1), new PetEvent.DragStarted());

        Assert.Equal(PetVisualState.Dragging, decision.State);
    }

    [Fact]
    public void ExitingOverridesEverything()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.SleepChanged(true));
        Apply(engine, T(1), new PetEvent.DragStarted());

        var decision = Apply(engine, T(2), new PetEvent.Exit());

        Assert.Equal(PetVisualState.Exiting, decision.State);
        Assert.False(decision.RendererEnabled);
    }

    [Fact]
    public void TypingExpiresTwoSecondsAfterLastActivity()
    {
        var engine = Engine();
        Apply(engine, T(1), new PetEvent.TypingActivity(true));

        var decision = Apply(engine, T(3.001), new PetEvent.DeadlineElapsed());

        Assert.Equal(PetVisualState.Idle, decision.State);
    }

    [Fact]
    public void RepeatedTypingExtendsExpiryWithoutRestartingPlayback()
    {
        var engine = Engine();
        var first = Apply(engine, T(1), new PetEvent.TypingActivity(true));
        var refreshed = Apply(engine, T(2), new PetEvent.TypingActivity(true));
        var beforeExpiry = Apply(engine, T(3.5), new PetEvent.DeadlineElapsed());

        Assert.Equal(first.PlaybackKey, refreshed.PlaybackKey);
        Assert.Equal(PetVisualState.UserTyping, beforeExpiry.State);
        Assert.Equal(T(4), beforeExpiry.NextDeadline);
    }

    [Fact]
    public void InactiveTypingClearsImmediately()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.TypingActivity(true));

        var decision = Apply(engine, T(0.5), new PetEvent.TypingActivity(false));

        Assert.Equal(PetVisualState.Idle, decision.State);
    }

    [Fact]
    public void PendingReactionExpiresAfterTenSeconds()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));
        Apply(engine, T(0.2), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(10.201), new PetEvent.DeadlineElapsed());

        Assert.Equal(ReactionLane.None, decision.ReactionLane);
        Assert.Equal(PetVisualState.Generating, decision.State);
    }

    [Fact]
    public void ReactionLeaseBeginsOnlyWhenFirstDisplayed()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));
        var pending = Apply(engine, T(1), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));
        Apply(engine, T(2), new PetEvent.DragStarted());
        Apply(engine, T(3), new PetEvent.GenerationIdle(new GenerationSerial(1)));

        var selected = Apply(engine, T(4), new PetEvent.DragEnded());
        var displayed = Apply(engine, T(4), new PetEvent.ReactionDisplayed(selected.PlaybackKey));

        Assert.Null(pending.LeaseDeadline);
        Assert.Equal(ReactionLane.Pending, selected.ReactionLane);
        Assert.Null(selected.LeaseDeadline);
        Assert.Equal(ReactionLane.Playing, displayed.ReactionLane);
        Assert.Equal(T(6), displayed.LeaseDeadline);
    }

    [Fact]
    public void ReactionPreemptionDoesNotPauseOrRestartLease()
    {
        var engine = Engine();
        var selected = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));
        var reaction = Apply(engine, T(0), new PetEvent.ReactionDisplayed(selected.PlaybackKey));
        Apply(engine, T(0.5), new PetEvent.DragStarted());

        var resumed = Apply(engine, T(1), new PetEvent.DragEnded());

        Assert.Equal(reaction.PlaybackKey, resumed.PlaybackKey);
        Assert.Equal(T(2), resumed.LeaseDeadline);
        Assert.Equal(T(0), resumed.PlaybackStartedAt);
    }

    [Fact]
    public void DragEndResumesReactionOnlyWhileLeaseIsAlive()
    {
        var engine = Engine();
        var selected = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1, visibleMs: 3000)));
        Apply(engine, T(0), new PetEvent.ReactionDisplayed(selected.PlaybackKey));
        Apply(engine, T(1), new PetEvent.DragStarted());

        var decision = Apply(engine, T(2), new PetEvent.DragEnded());

        Assert.Equal(PetVisualState.Reaction, decision.State);
        Assert.Equal(ReactionLane.Playing, decision.ReactionLane);
    }

    [Fact]
    public void ExpiredPreemptedReactionDoesNotResume()
    {
        var engine = Engine();
        var selected = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));
        Apply(engine, T(0), new PetEvent.ReactionDisplayed(selected.PlaybackKey));
        Apply(engine, T(0.5), new PetEvent.DragStarted());
        Apply(engine, T(2.1), new PetEvent.DeadlineElapsed());

        var decision = Apply(engine, T(2.2), new PetEvent.DragEnded());

        Assert.Equal(PetVisualState.Idle, decision.State);
        Assert.Equal(ReactionLane.None, decision.ReactionLane);
    }

    [Fact]
    public void NativeSendDropsOldReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.NativeSendObserved(new GenerationSerial(2), new TurnSerial(2)));

        Assert.Equal(ReactionLane.None, decision.ReactionLane);
        Assert.Equal(PetVisualState.Generating, decision.State);
    }

    [Fact]
    public void PetSwitchDropsReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.PetChanged(Pack("beta", "2.0.0"), false));

        Assert.Equal(ReactionLane.None, decision.ReactionLane);
        Assert.StartsWith("beta@2.0.0", decision.PlaybackKey, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteInvalidationDropsReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.RouteInvalidated());

        Assert.Equal(ReactionLane.None, decision.ReactionLane);
    }

    [Fact]
    public void SleepDropsReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.SleepChanged(true));

        Assert.Equal(ReactionLane.None, decision.ReactionLane);
    }

    [Fact]
    public void NewerTurnReactionReplacesPriorReaction()
    {
        var engine = Engine();
        var first = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var second = Apply(engine, T(0.5), new PetEvent.ValidatedReactionReceived(Reaction("nod", 2, 2, ["reaction_loop"])));

        Assert.NotEqual(first.PlaybackKey, second.PlaybackKey);
        Assert.Equal("nod", second.ReactionId);
    }

    [Fact]
    public void DuplicateSameTurnReactionIsIgnored()
    {
        var engine = Engine();
        var first = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var duplicate = Apply(engine, T(0.5), new PetEvent.ValidatedReactionReceived(Reaction("nod", 1, 1, ["reaction_loop"])));

        Assert.Equal(first.PlaybackKey, duplicate.PlaybackKey);
        Assert.Equal("spark", duplicate.ReactionId);
    }

    [Fact]
    public void ProvisionalGeneratingLastsAtMostThreeSeconds()
    {
        var engine = Engine();
        Apply(engine, T(1), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));

        var before = Apply(engine, T(3.999), new PetEvent.DeadlineElapsed());
        var after = Apply(engine, T(4.001), new PetEvent.DeadlineElapsed());

        Assert.Equal(PetVisualState.Generating, before.State);
        Assert.Equal(PetVisualState.Idle, after.State);
        Assert.Equal(ChatActivity.Unknown, after.ChatActivity);
    }

    [Fact]
    public void ConfirmedGeneratingPersistsWhileSignalIsGenerating()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(1), new PetEvent.GenerationObserved(new GenerationSerial(1)));

        var decision = Apply(engine, T(100), new PetEvent.DeadlineElapsed());

        Assert.Equal(PetVisualState.Generating, decision.State);
        Assert.Equal(ChatActivity.Generating, decision.ChatActivity);
    }

    [Fact]
    public void ConfirmedGenerationEndReleasesCurrentPendingReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));
        Apply(engine, T(1), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var selected = Apply(engine, T(2), new PetEvent.GenerationIdle(new GenerationSerial(1)));
        var decision = Apply(engine, T(2), new PetEvent.ReactionDisplayed(selected.PlaybackKey));

        Assert.Equal(ReactionLane.Pending, selected.ReactionLane);
        Assert.Equal(PetVisualState.Reaction, decision.State);
        Assert.Equal(ReactionLane.Playing, decision.ReactionLane);
    }

    [Fact]
    public void UnconfirmedGenerationIdleDoesNotReleasePendingReaction()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.5), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(1), new PetEvent.GenerationIdle(new GenerationSerial(1)));

        Assert.Equal(PetVisualState.Generating, decision.State);
        Assert.Equal(ChatActivity.Unknown, decision.ChatActivity);
        Assert.Equal(ReactionLane.Pending, decision.ReactionLane);
        Assert.Null(decision.LeaseDeadline);
    }

    [Fact]
    public void StalePendingReactionIsNotReleased()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));
        Apply(engine, T(0.2), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var decision = Apply(engine, T(10.201), new PetEvent.GenerationIdle(new GenerationSerial(1)));

        Assert.Equal(PetVisualState.Idle, decision.State);
        Assert.Equal(ReactionLane.None, decision.ReactionLane);
    }

    [Fact]
    public void GenerationUnknownDoesNotPretendCompletion()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));

        var decision = Apply(engine, T(1), new PetEvent.GenerationUnknown());

        Assert.Equal(ChatActivity.Unknown, decision.ChatActivity);
        Assert.NotEqual(PetVisualState.Generating, decision.State);
    }

    [Fact]
    public void LaterObservedGenerationReplacesCompletedGenerationWithoutNativeSend()
    {
        var engine = Engine();
        Apply(engine, T(0), new PetEvent.NativeSendObserved(new GenerationSerial(1), new TurnSerial(1)));
        Apply(engine, T(0.1), new PetEvent.GenerationObserved(new GenerationSerial(1)));
        Apply(engine, T(1), new PetEvent.GenerationIdle(new GenerationSerial(1)));

        var generating = Apply(engine, T(2), new PetEvent.GenerationObserved(new GenerationSerial(2)));
        var pending = Apply(engine, T(2.5), new PetEvent.ValidatedReactionReceived(Reaction("nod", 2, 2)));

        Assert.Equal(PetVisualState.Generating, generating.State);
        Assert.Equal(new GenerationSerial(2), generating.Generation);
        Assert.Equal(ReactionLane.Pending, pending.ReactionLane);
        Assert.Equal("nod", pending.ReactionId);
    }

    [Fact]
    public void RepeatedHoverEntryDoesNotCreateNewPlaybackIdentity()
    {
        var engine = Engine();
        var first = Apply(engine, T(1), new PetEvent.HoverEntered());

        var repeated = Apply(engine, T(2), new PetEvent.HoverEntered());

        Assert.Equal(first.PlaybackKey, repeated.PlaybackKey);
        Assert.Equal(first.PlaybackStartedAt, repeated.PlaybackStartedAt);
    }

    [Fact]
    public void RecomputingSameVisualDecisionDoesNotRestartPlayback()
    {
        var engine = Engine();
        var first = Apply(engine, T(1), new PetEvent.ChatVisibilityChanged(true));

        var recomputed = Apply(engine, T(2), new PetEvent.DeadlineElapsed());

        Assert.Equal(first.PlaybackKey, recomputed.PlaybackKey);
        Assert.Equal(first.PlaybackStartedAt, recomputed.PlaybackStartedAt);
    }

    [Fact]
    public void DifferentReactionProducesNewPlaybackIdentity()
    {
        var engine = Engine();
        var first = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        var second = Apply(engine, T(1), new PetEvent.ValidatedReactionReceived(Reaction("nod", 2, 2, ["reaction_loop"])));

        Assert.NotEqual(first.PlaybackKey, second.PlaybackKey);
    }

    [Fact]
    public void MissingSystemClipFallsBackToIdle()
    {
        var pack = Pack(systemOverrides: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["hover"] = ["missing"]
        });

        var decision = Apply(new AnimationStateEngine(pack, false), T(1), new PetEvent.HoverEntered());

        Assert.Equal(PetVisualState.Hover, decision.State);
        Assert.Equal("idle", decision.Clip.Id);
    }

    [Fact]
    public void MissingReactionCandidateFallsThroughToNextCandidate()
    {
        var engine = Engine();

        var decision = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(
            Reaction("spark", 1, 1, ["unavailable", "reaction_once"])));

        Assert.Equal("reaction_once", decision.Clip.Id);
    }

    [Fact]
    public void AllUnavailableReactionCandidatesFallBackToIdle()
    {
        var engine = Engine();

        var decision = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(
            Reaction("spark", 1, 1, ["unavailable", "missing"])));

        Assert.Equal("idle", decision.Clip.Id);
    }

    [Fact]
    public void FallbackDoesNotChangeSourceReactionId()
    {
        var engine = Engine();

        var decision = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(
            Reaction("source_reaction", 1, 1, ["unavailable"])));

        Assert.Equal("source_reaction", decision.ReactionId);
        Assert.Equal("idle", decision.Clip.Id);
    }

    [Fact]
    public void ReducedMotionKeepsSemanticStateAndChoosesStaticFrame()
    {
        var engine = new AnimationStateEngine(Pack(), true);

        var decision = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        Assert.Equal(PetVisualState.Reaction, decision.State);
        Assert.True(decision.ReducedMotion);
        Assert.Equal(0, decision.StaticFrameIndex);
    }

    [Fact]
    public void StaticClipRequiresNoMovingFrameTimer()
    {
        var decision = Engine().Current(T(0));

        Assert.False(PetAnimationPlayer.RequiresMovingFrameTimer(decision, true, T(0)));
    }

    [Fact]
    public void PngSheetLoopFrameMathUsesElapsedMonotonicTime()
    {
        var clip = Clip("loop", "pngSheet", "loop", frames: 4, fps: 2);

        var frame = PetAnimationPlayer.GetFrameIndex(clip, T(1.25), false);

        Assert.Equal(2, frame);
    }

    [Fact]
    public void PngSheetOneShotHoldsFinalFrame()
    {
        var clip = Clip("once", "pngSheet", "once", frames: 4, fps: 2);

        var frame = PetAnimationPlayer.GetFrameIndex(clip, T(10), false);

        Assert.Equal(3, frame);
    }

    [Fact]
    public void OverdueFrameCalculationSkipsDirectlyToCurrentLoopFrame()
    {
        var clip = Clip("loop", "pngSheet", "loop", frames: 4, fps: 4);

        var frame = PetAnimationPlayer.GetFrameIndex(clip, T(5.25), false);

        Assert.Equal(1, frame);
    }

    [Fact]
    public void DisposedRendererPreventsFurtherFrameCallbacks()
    {
        var scheduler = new FakeScheduler();
        var rendered = 0;
        var now = T(0);
        using var player = Player(scheduler, () => now, _ => rendered++);
        var pack = Pack();
        player.InstallPack(pack, Pixel(), false);
        var decision = Apply(new AnimationStateEngine(pack, false), now, new PetEvent.HoverEntered());
        player.SetVisible(true);
        player.Apply(decision);
        var beforeDispose = rendered;

        player.Dispose();
        now = T(1);
        scheduler.Fire();

        Assert.Equal(beforeDispose, rendered);
        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public void PackSwitchStopsClockAndReleasesOldDecodedResources()
    {
        var scheduler = new FakeScheduler();
        using var player = Player(scheduler, () => T(0), _ => { });
        var alpha = Pack("alpha", "1.0.0");
        player.InstallPack(alpha, Pixel(), false);
        player.SetVisible(true);
        player.Apply(Apply(new AnimationStateEngine(alpha, false), T(0), new PetEvent.HoverEntered()));
        Assert.True(player.CachedClipCount > 0);

        player.InstallPack(Pack("beta", "2.0.0"), Pixel(), false);

        Assert.Equal(0, player.CachedClipCount);
        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public void SelectedPackSwitchUsesOnlyLocalAnimationSeams()
    {
        var scheduler = new FakeScheduler();
        var decodeCount = 0;
        var renderCount = 0;
        using var player = new PetAnimationPlayer(
            _ => renderCount++,
            clip =>
            {
                decodeCount++;
                return Enumerable.Repeat(Pixel(), clip.FrameCount ?? 1).ToArray();
            },
            scheduler,
            () => T(0),
            _ => { });
        var pack = Pack("beta", "2.0.0");

        player.InstallPack(pack, Pixel(), false);
        player.SetVisible(true);
        player.Apply(new AnimationStateEngine(pack, false).Current(T(0)));

        Assert.Equal(1, decodeCount);
        Assert.True(renderCount >= 2);
    }

    [Fact]
    public void DragThresholdEmitsStartOnlyOnce()
    {
        var gate = new PetInteractionEventGate();

        var belowThreshold = gate.ObserveMove(2, 2);
        var crossed = gate.ObserveMove(3, 2);
        var repeated = gate.ObserveMove(10, 10);

        Assert.Null(belowThreshold);
        Assert.IsType<PetEvent.DragStarted>(crossed);
        Assert.Null(repeated);
    }

    [Fact]
    public void CompletingDragEmitsEndOnlyOnce()
    {
        var gate = new PetInteractionEventGate();
        gate.ObserveMove(5, 0);

        var ended = gate.Complete();
        var repeated = gate.Complete();

        Assert.IsType<PetEvent.DragEnded>(ended);
        Assert.Null(repeated);
    }

    [Fact]
    public void UniformPngSheetFramesAreSlicedInRowMajorOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PetGPT-animation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "sheet.png");
        try
        {
            var pixels = new byte[]
            {
                1, 0, 0, 255, 2, 0, 0, 255,
                3, 0, 0, 255, 4, 0, 0, 255
            };
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
                encoder.Save(stream);
            var clip = new CharacterClip(
                "sheet", "pngSheet", path, "loop", true, 2, 2, 1, 1, 2, 4, 4);

            var frames = PetAnimationPlayer.DecodeFrames(clip);

            Assert.Equal(4, frames.Count);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, frames.Select(ReadBlue).ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static byte ReadBlue(BitmapSource frame)
        {
            var pixel = new byte[4];
            frame.CopyPixels(pixel, 4, 0);
            return pixel[0];
        }
    }

    [Fact]
    public void PreparedPngSheetIdleUsesFirstFrameInsteadOfFullGrid()
    {
        var pixels = new byte[]
        {
            7, 0, 0, 255, 8, 0, 0, 255,
            9, 0, 0, 255, 10, 0, 0, 255
        };
        var sheet = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        sheet.Freeze();
        var clip = new CharacterClip(
            "idle", "pngSheet", "idle.png", "loop", true, 2, 2, 1, 1, 2, 4, 4);

        var idle = PetSelectionService.PrepareIdleFrame(sheet, clip);
        var pixel = new byte[4];
        idle.CopyPixels(pixel, 4, 0);

        Assert.Equal(1, idle.PixelWidth);
        Assert.Equal(1, idle.PixelHeight);
        Assert.Equal(7, pixel[0]);
    }

    [Fact]
    public void MissingAnimatedAssetFallsBackWithoutKeepingFrameLoopAlive()
    {
        var scheduler = new FakeScheduler();
        using var player = new PetAnimationPlayer(
            _ => { },
            _ => throw new IOException("asset disappeared"),
            scheduler,
            () => T(0),
            _ => { });
        var pack = Pack();
        player.InstallPack(pack, Pixel(), false);
        player.SetVisible(true);

        player.Apply(Apply(new AnimationStateEngine(pack, false), T(0), new PetEvent.HoverEntered()));

        Assert.False(scheduler.IsScheduled);
    }

    [Fact]
    public void HiddenRendererAcknowledgesReactionOnlyAfterFirstVisibleFrame()
    {
        var scheduler = new FakeScheduler();
        var acknowledgements = new List<string>();
        using var player = new PetAnimationPlayer(
            _ => { },
            clip => Enumerable.Repeat(Pixel(), clip.FrameCount ?? 1).ToArray(),
            scheduler,
            () => T(1),
            _ => { },
            (key, _) => acknowledgements.Add(key));
        var engine = Engine();
        var decision = Apply(engine, T(0), new PetEvent.ValidatedReactionReceived(Reaction("spark", 1, 1)));

        player.Apply(decision);
        Assert.Empty(acknowledgements);

        player.SetVisible(true);

        Assert.Equal([decision.PlaybackKey], acknowledgements);
    }

    private static AnimationStateEngine Engine() => new(Pack(), false);

    private static PlaybackDecision Apply(AnimationStateEngine engine, TimeSpan at, PetEvent petEvent) =>
        engine.Apply(petEvent, at);

    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ValidatedReaction Reaction(
        string id,
        long turn,
        long generation,
        IReadOnlyList<string>? candidates = null,
        int visibleMs = 2000) =>
        new(
            "alpha",
            id,
            50,
            visibleMs,
            candidates ?? ["reaction_once"],
            new GenerationSerial(generation),
            new TurnSerial(turn));

    private static CharacterPack Pack(
        string id = "alpha",
        string version = "1.0.0",
        IReadOnlyDictionary<string, IReadOnlyList<string>>? systemOverrides = null)
    {
        var clips = new Dictionary<string, CharacterClip>(StringComparer.Ordinal)
        {
            ["idle"] = Clip("idle", "png", "hold"),
            ["chat"] = Clip("chat", "png", "hold"),
            ["hover"] = Clip("hover", "pngSheet", "loop", 4, 4),
            ["dragging"] = Clip("dragging", "pngSheet", "loop", 4, 4),
            ["typing"] = Clip("typing", "pngSheet", "loop", 4, 4),
            ["generating"] = Clip("generating", "pngSheet", "loop", 4, 4),
            ["sleep"] = Clip("sleep", "png", "hold"),
            ["reaction_once"] = Clip("reaction_once", "pngSheet", "once", 4, 2),
            ["reaction_loop"] = Clip("reaction_loop", "pngSheet", "loop", 4, 2),
            ["unavailable"] = Clip("unavailable", "pngSheet", "loop", 4, 2, available: false)
        };
        var system = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["idle"] = ["idle"],
            ["chatOpen"] = ["chat"],
            ["hover"] = ["hover"],
            ["dragging"] = ["dragging"],
            ["userTyping"] = ["typing"],
            ["generating"] = ["generating"],
            ["sleep"] = ["sleep"]
        };
        if (systemOverrides is not null)
        {
            foreach (var pair in systemOverrides)
                system[pair.Key] = pair.Value;
        }

        return new CharacterPack(
            Path.Combine(Path.GetTempPath(), "PetGPT-animation-tests", id, version),
            CharacterPackSource.Installed,
            id,
            id,
            version,
            "2.0.0",
            null,
            new CharacterPresentation(150, 150, 0.5, 1),
            clips,
            system,
            new Dictionary<string, CharacterReaction>(StringComparer.Ordinal),
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
    }

    private static CharacterClip Clip(
        string id,
        string format,
        string playback,
        int frames = 1,
        int fps = 1,
        bool available = true) =>
        new(
            id,
            format,
            Path.Combine(Path.GetTempPath(), "PetGPT-animation-tests", $"{id}.png"),
            playback,
            available,
            frames,
            1,
            format == "pngSheet" ? 1 : null,
            format == "pngSheet" ? 1 : null,
            format == "pngSheet" ? 1 : null,
            format == "pngSheet" ? frames : null,
            format == "pngSheet" ? fps : null);

    private static BitmapSource Pixel()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static PetAnimationPlayer Player(
        FakeScheduler scheduler,
        Func<TimeSpan> clock,
        Action<BitmapSource> render) =>
        new(
            render,
            clip => Enumerable.Repeat(Pixel(), clip.FrameCount ?? 1).ToArray(),
            scheduler,
            clock,
            _ => { });

    private sealed class FakeScheduler : IAnimationFrameScheduler
    {
        private Action? _callback;

        public bool IsScheduled => _callback is not null;

        public void Schedule(TimeSpan due, Action callback) => _callback = callback;

        public void Stop() => _callback = null;

        public void Fire()
        {
            var callback = _callback;
            _callback = null;
            callback?.Invoke();
        }

        public void Dispose() => Stop();
    }
}
