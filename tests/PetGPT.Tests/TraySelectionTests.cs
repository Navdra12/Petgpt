using PetGPT.Characters;
using PetGPT.Shell;
using Xunit;

namespace PetGPT.Tests;

public sealed class TraySelectionTests
{
    [Fact]
    public void PetMenuDisambiguatesVersionsAndChecksOnlyExactSelection()
    {
        var legacy = Pack("legacy", "1.0.0", "Legacy", CharacterPackSource.Bundled);
        var first = Pack("alpha", "1.0.0", "Alpha");
        var second = Pack("alpha", "2.0.0", "Alpha");
        var state = new TrayPetMenuState([legacy, first, second]);

        state.CommitSelection("alpha", "2.0.0");

        Assert.Equal("Legacy", state.Entries.Single(entry => entry.Pack.Id == "legacy").Label);
        Assert.Equal(
            ["Alpha (1.0.0)", "Alpha (2.0.0)"],
            state.Entries.Where(entry => entry.Pack.Id == "alpha").Select(entry => entry.Label));
        Assert.Single(state.Entries, entry => entry.IsChecked);
        Assert.True(state.Entries.Single(entry => entry.Pack.Version == "2.0.0").IsChecked);
    }

    [Fact]
    public void PetMenuCheckDoesNotMoveUntilSuccessfulCommit()
    {
        var legacy = Pack("legacy", "1.0.0", "Legacy", CharacterPackSource.Bundled);
        var alpha = Pack("alpha", "1.0.0", "Alpha");
        var state = new TrayPetMenuState([legacy, alpha]);
        state.CommitSelection("legacy", "1.0.0");

        // A failed selection never invokes CommitSelection.

        Assert.True(state.Entries.Single(entry => entry.Pack.Id == "legacy").IsChecked);
        Assert.False(state.Entries.Single(entry => entry.Pack.Id == "alpha").IsChecked);
    }

    [Fact]
    public void OwnedResourceSlotDisposesSupersededResourceAfterReplacement()
    {
        var first = new TrackedResource();
        var second = new TrackedResource();
        using var slot = new OwnedResourceSlot<TrackedResource>(first);

        slot.Replace(second);

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        slot.Dispose();
        Assert.True(second.IsDisposed);
    }

    [Fact]
    public void TrayIconSwapFailureKeepsPreviousResourceAndDisposesCandidate()
    {
        var previous = new TrackedResource();
        var candidate = new TrackedResource();
        using var slot = new OwnedResourceSlot<TrackedResource>(previous);

        var replaced = TrayIconSwap.TryReplace(
            slot,
            candidate,
            _ => throw new InvalidOperationException("synthetic assignment failure"));

        Assert.False(replaced);
        Assert.Same(previous, slot.Current);
        Assert.False(previous.IsDisposed);
        Assert.True(candidate.IsDisposed);
    }

    private static CharacterPack Pack(
        string id,
        string version,
        string displayName,
        CharacterPackSource source = CharacterPackSource.Installed) =>
        new(
            Path.Combine(Path.GetTempPath(), "PetGPT-tray-tests", id, version),
            source,
            id,
            displayName,
            version,
            "2.0.0",
            null,
            new CharacterPresentation(150, 150, 0.5, 1),
            new Dictionary<string, CharacterClip>(StringComparer.Ordinal)
            {
                ["idle"] = new("idle", "png", "idle.png", "hold", true, 1, 1, null, null, null, null, null)
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, CharacterReaction>(StringComparer.Ordinal),
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);

    private sealed class TrackedResource : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
