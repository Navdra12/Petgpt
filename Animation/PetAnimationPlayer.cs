using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PetGPT.Characters;
using PetGPT.Models;
using WpfImage = System.Windows.Controls.Image;

namespace PetGPT.Animation;

internal interface IAnimationFrameScheduler : IDisposable
{
    bool IsScheduled { get; }
    void Schedule(TimeSpan due, Action callback);
    void Stop();
}

public sealed class PetAnimationPlayer : IDisposable
{
    private static readonly TimeSpan MinimumDue = TimeSpan.FromMilliseconds(1);

    private readonly Action<BitmapSource> _render;
    private readonly Func<CharacterClip, IReadOnlyList<BitmapSource>> _decode;
    private readonly IAnimationFrameScheduler _scheduler;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly Action<TimeSpan> _deadlineElapsed;
    private readonly Action<string, TimeSpan> _reactionDisplayed;
    private readonly Dictionary<string, IReadOnlyList<BitmapSource>> _frames = new(StringComparer.Ordinal);
    private CharacterPack? _pack;
    private BitmapSource? _fallbackIdle;
    private PlaybackDecision? _decision;
    private bool _reducedMotion;
    private bool _visible;
    private bool _disposed;
    private string? _renderedPlaybackKey;
    private int _renderedFrame = -1;

    public PetAnimationPlayer(
        WpfImage target,
        Func<TimeSpan> monotonicNow,
        Action<TimeSpan> deadlineElapsed,
        Action<string, TimeSpan>? reactionDisplayed = null)
        : this(
            frame => target.Source = frame,
            DecodeFrames,
            new DispatcherAnimationFrameScheduler(target.Dispatcher),
            monotonicNow,
            deadlineElapsed,
            reactionDisplayed)
    {
        ArgumentNullException.ThrowIfNull(target);
    }

    internal PetAnimationPlayer(
        Action<BitmapSource> render,
        Func<CharacterClip, IReadOnlyList<BitmapSource>> decode,
        IAnimationFrameScheduler scheduler,
        Func<TimeSpan> monotonicNow,
        Action<TimeSpan> deadlineElapsed,
        Action<string, TimeSpan>? reactionDisplayed = null)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _monotonicNow = monotonicNow ?? throw new ArgumentNullException(nameof(monotonicNow));
        _deadlineElapsed = deadlineElapsed ?? throw new ArgumentNullException(nameof(deadlineElapsed));
        _reactionDisplayed = reactionDisplayed ?? ((_, _) => { });
    }

    internal int CachedClipCount => _frames.Count;
    internal bool HasScheduledCallback => _scheduler.IsScheduled;

    public void InstallPack(CharacterPack pack, BitmapSource fallbackIdle, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(fallbackIdle);
        if (_disposed)
            return;

        _scheduler.Stop();
        _frames.Clear();
        _pack = pack;
        _fallbackIdle = fallbackIdle;
        _reducedMotion = reducedMotion;
        _decision = null;
        _renderedPlaybackKey = null;
        _renderedFrame = -1;
        _render(fallbackIdle);
    }

    public void SetVisible(bool visible)
    {
        if (_disposed)
            return;

        _visible = visible;
        if (!visible)
        {
            _scheduler.Stop();
            return;
        }

        if (_decision is not null)
        {
            if (_decision.NextDeadline.HasValue && _monotonicNow() >= _decision.NextDeadline.Value)
                _deadlineElapsed(_monotonicNow());
            else
                RenderAndSchedule();
        }
    }

    public void Apply(PlaybackDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (_disposed)
            return;

        _decision = decision with { ReducedMotion = decision.ReducedMotion || _reducedMotion };
        if (!decision.RendererEnabled)
        {
            _scheduler.Stop();
            _frames.Clear();
            _pack = null;
            _fallbackIdle = null;
            return;
        }

        if (!_visible)
        {
            _scheduler.Stop();
            return;
        }

        RenderAndSchedule();
    }

    public static int GetFrameIndex(CharacterClip clip, TimeSpan elapsed, bool reducedMotion)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var frameCount = clip.FrameCount.GetValueOrDefault(1);
        if (clip.Format.Equals("png", StringComparison.Ordinal) || frameCount <= 1 || reducedMotion)
            return 0;

        var fps = clip.Fps.GetValueOrDefault(1);
        var elapsedTicks = Math.Max(0, elapsed.Ticks);
        var rawFrame = (long)Math.Floor(elapsedTicks / (double)TimeSpan.TicksPerSecond * fps);
        return clip.Playback.Equals("once", StringComparison.Ordinal)
            ? (int)Math.Min(frameCount - 1L, rawFrame)
            : (int)(rawFrame % frameCount);
    }

    public static bool RequiresMovingFrameTimer(
        PlaybackDecision decision,
        bool rendererVisible,
        TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!rendererVisible || !decision.RendererEnabled || decision.ReducedMotion ||
            decision.Clip.Format.Equals("png", StringComparison.Ordinal) ||
            decision.Clip.FrameCount.GetValueOrDefault(1) <= 1)
        {
            return false;
        }

        if (!decision.Clip.Playback.Equals("once", StringComparison.Ordinal))
            return true;

        var elapsed = monotonicNow > decision.PlaybackStartedAt
            ? monotonicNow - decision.PlaybackStartedAt
            : TimeSpan.Zero;
        return GetFrameIndex(decision.Clip, elapsed, false) < decision.Clip.FrameCount.GetValueOrDefault(1) - 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _scheduler.Stop();
        _scheduler.Dispose();
        _frames.Clear();
        _pack = null;
        _fallbackIdle = null;
        _decision = null;
    }

    private void RenderAndSchedule()
    {
        if (_disposed || !_visible || _decision is null || !_decision.RendererEnabled)
            return;

        var now = _monotonicNow();
        var frames = GetFrames(_decision.Clip);
        var elapsed = _decision.ReactionLane == ReactionLane.Pending
            ? TimeSpan.Zero
            : now > _decision.PlaybackStartedAt
                ? now - _decision.PlaybackStartedAt
                : TimeSpan.Zero;
        var frameIndex = Math.Min(
            frames.Count - 1,
            GetFrameIndex(_decision.Clip, elapsed, _decision.ReducedMotion));
        if (_renderedPlaybackKey != _decision.PlaybackKey || _renderedFrame != frameIndex)
        {
            _render(frames[frameIndex]);
            _renderedPlaybackKey = _decision.PlaybackKey;
            _renderedFrame = frameIndex;
            if (_decision.State == PetVisualState.Reaction &&
                _decision.ReactionLane == ReactionLane.Pending)
            {
                _reactionDisplayed(_decision.PlaybackKey, now);
            }
        }

        ScheduleNext(now, frames.Count);
    }

    private IReadOnlyList<BitmapSource> GetFrames(CharacterClip clip)
    {
        if (_frames.TryGetValue(clip.Id, out var cached))
            return cached;

        try
        {
            var decoded = _decode(clip);
            if (decoded.Count > 0)
            {
                _frames[clip.Id] = decoded;
                return decoded;
            }
        }
        catch
        {
            // T6's already committed idle remains the safe presentation fallback.
        }

        var fallback = _fallbackIdle ?? throw new InvalidOperationException("No safe idle image is installed.");
        IReadOnlyList<BitmapSource> fallbackFrames = [fallback];
        _frames[clip.Id] = fallbackFrames;
        return fallbackFrames;
    }

    private void ScheduleNext(TimeSpan now, int decodedFrameCount)
    {
        _scheduler.Stop();
        if (_disposed || !_visible || _decision is null || !_decision.RendererEnabled)
            return;

        TimeSpan? next = _decision.NextDeadline;
        if (decodedFrameCount > 1 && RequiresMovingFrameTimer(_decision, true, now))
        {
            var fps = _decision.Clip.Fps.GetValueOrDefault(1);
            var elapsed = now > _decision.PlaybackStartedAt
                ? now - _decision.PlaybackStartedAt
                : TimeSpan.Zero;
            var rawFrame = (long)Math.Floor(elapsed.Ticks / (double)TimeSpan.TicksPerSecond * fps);
            var nextFrame = _decision.PlaybackStartedAt +
                TimeSpan.FromSeconds((rawFrame + 1d) / fps);
            if (!next.HasValue || nextFrame < next.Value)
                next = nextFrame;
        }

        if (!next.HasValue)
            return;

        var due = next.Value - now;
        _scheduler.Schedule(due > TimeSpan.Zero ? due : MinimumDue, OnScheduled);
    }

    private void OnScheduled()
    {
        if (_disposed || !_visible || _decision is null)
            return;

        var now = _monotonicNow();
        if (_decision.NextDeadline.HasValue && now >= _decision.NextDeadline.Value)
        {
            _deadlineElapsed(now);
            return;
        }

        RenderAndSchedule();
    }

    internal static IReadOnlyList<BitmapSource> DecodeFrames(CharacterClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        using var stream = new FileStream(
            clip.AssetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.StreamSource = stream;
        sheet.EndInit();
        sheet.Freeze();

        if (clip.Format.Equals("png", StringComparison.Ordinal))
            return [sheet];

        var width = clip.FrameWidth ?? throw new InvalidOperationException("Validated sheet width is missing.");
        var height = clip.FrameHeight ?? throw new InvalidOperationException("Validated sheet height is missing.");
        var columns = clip.Columns ?? throw new InvalidOperationException("Validated sheet columns are missing.");
        var frameCount = clip.FrameCount ?? throw new InvalidOperationException("Validated sheet frame count is missing.");
        var frames = new BitmapSource[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var crop = new CroppedBitmap(
                sheet,
                new Int32Rect((index % columns) * width, (index / columns) * height, width, height));
            crop.Freeze();
            frames[index] = crop;
        }

        return frames;
    }
}

internal sealed class DispatcherAnimationFrameScheduler(Dispatcher dispatcher) : IAnimationFrameScheduler
{
    private DispatcherTimer? _timer;
    private Action? _callback;

    public bool IsScheduled => _timer?.IsEnabled == true;

    public void Schedule(TimeSpan due, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Stop();
        _callback = callback;
        _timer ??= CreateTimer();
        _timer.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _callback = null;
    }

    public void Dispose()
    {
        Stop();
        if (_timer is not null)
            _timer.Tick -= OnTick;
        _timer = null;
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
        timer.Tick += OnTick;
        return timer;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var callback = _callback;
        Stop();
        callback?.Invoke();
    }
}
