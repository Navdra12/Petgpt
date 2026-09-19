using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetGPT.Models;

namespace PetGPT.Services;

public sealed record BridgeDocumentIdentity(string DocumentSession, long RouteRevision);

public sealed record BridgeCapabilities(
    bool Generation,
    bool ComposerEmpty,
    bool PageSurface,
    bool SecondarySurface,
    bool ComposerSurface,
    bool Scrollbar,
    bool CompactNavigation)
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
    RateLimited,
    Disposed
}

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
    public GenerationCapabilityState GenerationState { get; private set; } = GenerationCapabilityState.Unknown;
    public ComposerDraftState ComposerDraftState { get; private set; } = ComposerDraftState.Unknown;
    public BridgeDocumentIdentity? CurrentDocument => _document;

    public event EventHandler? StatusChanged;

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
                "compactNavigation") ||
            !TryGetBoolean(values, "generation", out var generation) ||
            !TryGetBoolean(values, "composerEmpty", out var composerEmpty) ||
            !TryGetBoolean(values, "pageSurface", out var pageSurface) ||
            !TryGetBoolean(values, "secondarySurface", out var secondarySurface) ||
            !TryGetBoolean(values, "composerSurface", out var composerSurface) ||
            !TryGetBoolean(values, "scrollbar", out var scrollbar) ||
            !TryGetBoolean(values, "compactNavigation", out var compactNavigation))
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
            compactNavigation);
        var generationReset = !next.Generation &&
            GenerationState != GenerationCapabilityState.Unknown;
        var composerReset = !next.ComposerEmpty &&
            ComposerDraftState != Models.ComposerDraftState.Unknown;
        if (next != Capabilities || generationReset || composerReset)
        {
            Capabilities = next;
            if (generationReset)
                GenerationState = GenerationCapabilityState.Unknown;
            if (composerReset)
                ComposerDraftState = Models.ComposerDraftState.Unknown;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
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
            return BridgeMessageResult.Accepted;
        }

        return BridgeMessageResult.InvalidSchema;
    }

    private void ResetStatus()
    {
        var changed = Capabilities != BridgeCapabilities.None ||
            GenerationState != GenerationCapabilityState.Unknown ||
            ComposerDraftState != Models.ComposerDraftState.Unknown;
        Capabilities = BridgeCapabilities.None;
        GenerationState = GenerationCapabilityState.Unknown;
        ComposerDraftState = Models.ComposerDraftState.Unknown;
        if (changed)
            StatusChanged?.Invoke(this, EventArgs.Empty);
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
}
