using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetGPT.Models;
using PetGPT.Personas;

namespace PetGPT.Services;

public sealed record BridgeDocumentIdentity(string DocumentSession, long RouteRevision);

public sealed record BridgeCapabilities(
    bool Generation,
    bool ComposerEmpty,
    bool PageSurface,
    bool SecondarySurface,
    bool ComposerSurface,
    bool Scrollbar,
    bool CompactNavigation,
    bool Submission = false)
{
    public static BridgeCapabilities None { get; } = new(false, false, false, false, false, false, false);
}

public enum BridgeMessageResult
{
    Accepted,
    InvalidOrigin,
    Oversized,
    InvalidSchema,
    StaleDocument,
    UnsupportedKind,
    CapabilityUnavailable,
    Unsolicited,
    RateLimited,
    Disposed
}

public delegate void BridgeSubmissionObservedEventHandler(
    object? sender,
    BridgeSubmissionObservation observation);

public sealed class WebViewBridge : IDisposable
{
    public const int MaximumMessageBytes = 2048;
    private static readonly Regex SessionPattern = new(
        "\\A[0-9a-f]{16}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private BridgeDocumentIdentity? _document;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly Stopwatch? _clock;
    private double _messageTokens = 40;
    private TimeSpan _lastMessageTime;
    private readonly object _stageGate = new();
    private PendingStage? _pendingStage;
    private bool _disposed;

    public WebViewBridge()
    {
        _clock = Stopwatch.StartNew();
        _monotonicNow = () => _clock.Elapsed;
        _lastMessageTime = _monotonicNow();
    }

    internal WebViewBridge(Func<TimeSpan> monotonicNow)
    {
        _monotonicNow = monotonicNow ?? throw new ArgumentNullException(nameof(monotonicNow));
        _lastMessageTime = _monotonicNow();
    }

    public BridgeCapabilities Capabilities { get; private set; } = BridgeCapabilities.None;
    public bool IsAdapterReady { get; private set; }
    public GenerationCapabilityState GenerationState { get; private set; } = GenerationCapabilityState.Unknown;
    public ComposerDraftState ComposerDraftState { get; private set; } = ComposerDraftState.Unknown;
    public BridgeDocumentIdentity? CurrentDocument => _document;

    public event EventHandler? StatusChanged;
    public event BridgeSubmissionObservedEventHandler? SubmissionObserved;

    public BridgeDocumentIdentity BeginDocument(Uri topLevelUri, string? documentSession = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ChatNavigationUrlPolicy.IsEnhancementEligible(topLevelUri))
            throw new ArgumentException("Document is not eligible for enhancement.", nameof(topLevelUri));

        documentSession ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        if (!SessionPattern.IsMatch(documentSession))
            throw new ArgumentException("Document session must be sixteen lowercase hexadecimal characters.", nameof(documentSession));
        _document = new BridgeDocumentIdentity(documentSession, 0);
        ResetRateLimit();
        ResetStatus();
        return _document;
    }

    public BridgeDocumentIdentity AdvanceRoute(Uri topLevelUri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_document is null || !ChatNavigationUrlPolicy.IsEnhancementEligible(topLevelUri))
            throw new InvalidOperationException("No eligible document is active.");

        _document = _document with { RouteRevision = checked(_document.RouteRevision + 1) };
        ResetRateLimit();
        ResetStatus();
        return _document;
    }

    public void InvalidateDocument()
    {
        if (_disposed)
            return;
        _document = null;
        ResetStatus();
    }

    public Task<StagePersonaResult> StagePersonaAsync(
        PersonaContext context,
        Action<string> postMessage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(postMessage);
        cancellationToken.ThrowIfCancellationRequested();

        if (_document is null)
            return Task.FromResult(StagePersonaResult.RouteChanged);
        if (!Capabilities.ComposerEmpty || ComposerDraftState == Models.ComposerDraftState.Unknown)
            return Task.FromResult(StagePersonaResult.Unsupported);
        if (ComposerDraftState == Models.ComposerDraftState.NonEmpty)
            return Task.FromResult(StagePersonaResult.ComposerNotEmpty);
        if (GenerationState == GenerationCapabilityState.Generating)
            return Task.FromResult(StagePersonaResult.Generating);
        if (context.Utf8ByteCount > PersonaAssembler.MaximumContextUtf8Bytes)
            return Task.FromResult(StagePersonaResult.Unsupported);

        var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var completion = new TaskCompletionSource<StagePersonaResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingStage(requestId, _document, completion);
        lock (_stageGate)
        {
            if (_pendingStage is not null)
                return Task.FromResult(StagePersonaResult.Unsupported);
            _pendingStage = pending;
        }

        var operation = JsonSerializer.Serialize(new
        {
            v = 1,
            op = "stagePersona",
            documentSession = pending.Document.DocumentSession,
            routeRevision = pending.Document.RouteRevision,
            requestId,
            text = context.Text
        });
        try
        {
            postMessage(operation);
        }
        catch
        {
            CompleteStage(pending, StagePersonaResult.RouteChanged);
        }

        return AwaitStageAsync(pending, cancellationToken);
    }

    public BridgeMessageResult AcceptMessage(
        string source,
        Uri? currentTopLevelUri,
        string json)
    {
        if (_disposed)
            return BridgeMessageResult.Disposed;
        if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
            return BridgeMessageResult.Oversized;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            !ChatNavigationUrlPolicy.IsEnhancementEligible(sourceUri) ||
            !ChatNavigationUrlPolicy.IsEnhancementEligible(currentTopLevelUri))
        {
            return BridgeMessageResult.InvalidOrigin;
        }
        if (!TryConsumeMessageToken())
            return BridgeMessageResult.RateLimited;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException)
        {
            return BridgeMessageResult.InvalidSchema;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasExactly(root, "v", "kind", "documentSession", "routeRevision", "payload") ||
                !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
                !TryGetBoundedString(root, "kind", 16, out var kind) ||
                !TryGetBoundedString(root, "documentSession", 16, out var session) || !SessionPattern.IsMatch(session) ||
                !root.TryGetProperty("routeRevision", out var revisionElement) || revisionElement.ValueKind != JsonValueKind.Number || !revisionElement.TryGetInt64(out var revision) || revision < 0 ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return BridgeMessageResult.InvalidSchema;
            }

            if (_document is null ||
                !_document.DocumentSession.Equals(session, StringComparison.Ordinal) ||
                _document.RouteRevision != revision)
            {
                return BridgeMessageResult.StaleDocument;
            }

            return kind switch
            {
                "ready" => AcceptReady(payload),
                "activity" => AcceptActivity(payload),
                _ => BridgeMessageResult.UnsupportedKind
            };
        }
    }

    private BridgeMessageResult AcceptReady(JsonElement payload)
    {
        if (!HasExactly(payload, "adapterVersion", "capabilities") ||
            !TryGetBoundedString(payload, "adapterVersion", 16, out var adapterVersion) ||
            adapterVersion != "1" ||
            !payload.TryGetProperty("capabilities", out var values) ||
            values.ValueKind != JsonValueKind.Object ||
            !HasExactly(
                values,
                "generation",
                "composerEmpty",
                "pageSurface",
                "secondarySurface",
                "composerSurface",
                "scrollbar",
                "compactNavigation",
                "submission") ||
            !TryGetBoolean(values, "generation", out var generation) ||
            !TryGetBoolean(values, "composerEmpty", out var composerEmpty) ||
            !TryGetBoolean(values, "pageSurface", out var pageSurface) ||
            !TryGetBoolean(values, "secondarySurface", out var secondarySurface) ||
            !TryGetBoolean(values, "composerSurface", out var composerSurface) ||
            !TryGetBoolean(values, "scrollbar", out var scrollbar) ||
            !TryGetBoolean(values, "compactNavigation", out var compactNavigation) ||
            !TryGetBoolean(values, "submission", out var submission))
        {
            return BridgeMessageResult.InvalidSchema;
        }

        var next = new BridgeCapabilities(
            generation,
            composerEmpty,
            pageSurface,
            secondarySurface,
            composerSurface,
            scrollbar,
            compactNavigation,
            submission);
        var readyChanged = !IsAdapterReady;
        IsAdapterReady = true;
        var generationReset = !next.Generation &&
            GenerationState != GenerationCapabilityState.Unknown;
        var composerReset = !next.ComposerEmpty &&
            ComposerDraftState != Models.ComposerDraftState.Unknown;
        if (readyChanged || next != Capabilities || generationReset || composerReset)
        {
            Capabilities = next;
            if (generationReset)
                GenerationState = GenerationCapabilityState.Unknown;
            if (composerReset)
                ComposerDraftState = Models.ComposerDraftState.Unknown;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        if (!next.ComposerEmpty)
            CompleteCurrentStage(StagePersonaResult.Unsupported);
        return BridgeMessageResult.Accepted;
    }

    private BridgeMessageResult AcceptActivity(JsonElement payload)
    {
        if (!TryGetBoundedString(payload, "event", 16, out var eventName))
            return BridgeMessageResult.InvalidSchema;

        if (eventName == "generation")
        {
            if (!Capabilities.Generation)
                return BridgeMessageResult.CapabilityUnavailable;
            if (!HasExactly(payload, "event", "state") ||
                !TryGetBoundedString(payload, "state", 16, out var state))
            {
                return BridgeMessageResult.InvalidSchema;
            }

            var next = state switch
            {
                "unknown" => GenerationCapabilityState.Unknown,
                "idle" => GenerationCapabilityState.Idle,
                "generating" => GenerationCapabilityState.Generating,
                _ => (GenerationCapabilityState?)null
            };
            if (next is null)
                return BridgeMessageResult.InvalidSchema;
            if (next.Value != GenerationState)
            {
                GenerationState = next.Value;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            if (next.Value == GenerationCapabilityState.Generating)
                CompleteCurrentStage(StagePersonaResult.Generating);
            return BridgeMessageResult.Accepted;
        }

        if (eventName == "composer")
        {
            if (!Capabilities.ComposerEmpty)
                return BridgeMessageResult.CapabilityUnavailable;
            if (!HasExactly(payload, "event", "empty") ||
                !TryGetBoolean(payload, "empty", out var empty))
            {
                return BridgeMessageResult.InvalidSchema;
            }

            var next = empty ? Models.ComposerDraftState.Empty : Models.ComposerDraftState.NonEmpty;
            if (next != ComposerDraftState)
            {
                ComposerDraftState = next;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            if (!empty)
                CompleteCurrentStage(StagePersonaResult.ComposerNotEmpty);
            return BridgeMessageResult.Accepted;
        }

        if (eventName is "submit" or "regenerate")
        {
            if (!HasExactly(payload, "event", "generationSerial") ||
                !payload.TryGetProperty("generationSerial", out var serialElement) ||
                serialElement.ValueKind != JsonValueKind.Number ||
                !serialElement.TryGetInt64(out var serial) || serial <= 0)
            {
                return BridgeMessageResult.InvalidSchema;
            }

            SubmissionObserved?.Invoke(
                this,
                new BridgeSubmissionObservation(
                    _document!,
                    new GenerationSerial(serial),
                    eventName == "regenerate"));
            return BridgeMessageResult.Accepted;
        }

        if (eventName == "stageResult")
        {
            if (!HasExactly(payload, "event", "requestId", "result") ||
                !TryGetBoundedString(payload, "requestId", 16, out var requestId) ||
                !SessionPattern.IsMatch(requestId) ||
                !TryGetBoundedString(payload, "result", 24, out var resultName))
            {
                return BridgeMessageResult.InvalidSchema;
            }

            var result = resultName switch
            {
                "staged" => StagePersonaResult.Staged,
                "composerNotEmpty" => StagePersonaResult.ComposerNotEmpty,
                "generating" => StagePersonaResult.Generating,
                "unsupported" => StagePersonaResult.Unsupported,
                "routeChanged" => StagePersonaResult.RouteChanged,
                _ => (StagePersonaResult?)null
            };
            if (result is null)
                return BridgeMessageResult.InvalidSchema;

            PendingStage? pending;
            lock (_stageGate)
                pending = _pendingStage;
            if (pending is null ||
                !pending.RequestId.Equals(requestId, StringComparison.Ordinal) ||
                pending.Document != _document)
            {
                return BridgeMessageResult.Unsolicited;
            }

            CompleteStage(pending, result.Value);
            return BridgeMessageResult.Accepted;
        }

        return BridgeMessageResult.InvalidSchema;
    }

    private void ResetStatus()
    {
        CompleteCurrentStage(StagePersonaResult.RouteChanged);
        var changed = IsAdapterReady ||
            Capabilities != BridgeCapabilities.None ||
            GenerationState != GenerationCapabilityState.Unknown ||
            ComposerDraftState != Models.ComposerDraftState.Unknown;
        IsAdapterReady = false;
        Capabilities = BridgeCapabilities.None;
        GenerationState = GenerationCapabilityState.Unknown;
        ComposerDraftState = Models.ComposerDraftState.Unknown;
        if (changed)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<StagePersonaResult> AwaitStageAsync(
        PendingStage pending,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() =>
        {
            if (ClearPendingStage(pending))
                pending.Completion.TrySetCanceled(cancellationToken);
        });
        return await pending.Completion.Task;
    }

    private void CompleteCurrentStage(StagePersonaResult result)
    {
        PendingStage? pending;
        lock (_stageGate)
            pending = _pendingStage;
        if (pending is not null)
            CompleteStage(pending, result);
    }

    private void CompleteStage(PendingStage pending, StagePersonaResult result)
    {
        if (ClearPendingStage(pending))
            pending.Completion.TrySetResult(result);
    }

    private bool ClearPendingStage(PendingStage pending)
    {
        lock (_stageGate)
        {
            if (!ReferenceEquals(_pendingStage, pending))
                return false;
            _pendingStage = null;
            return true;
        }
    }

    private bool TryConsumeMessageToken()
    {
        var now = _monotonicNow();
        var elapsed = now >= _lastMessageTime ? now - _lastMessageTime : TimeSpan.Zero;
        _lastMessageTime = now;
        _messageTokens = Math.Min(40, _messageTokens + (elapsed.TotalSeconds * 20));
        if (_messageTokens < 1)
            return false;
        _messageTokens -= 1;
        return true;
    }

    private void ResetRateLimit()
    {
        _messageTokens = 40;
        _lastMessageTime = _monotonicNow();
    }

    private static bool HasExactly(JsonElement element, params string[] names)
    {
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (!expected.Remove(property.Name))
                return false;
        }
        return count == names.Length && expected.Count == 0;
    }

    private static bool TryGetBoundedString(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text &&
            text.Length <= maximumLength &&
            !text.Any(char.IsControl) &&
            (value = text) is not null;
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        InvalidateDocument();
        _disposed = true;
    }

    private sealed record PendingStage(
        string RequestId,
        BridgeDocumentIdentity Document,
        TaskCompletionSource<StagePersonaResult> Completion);
}
