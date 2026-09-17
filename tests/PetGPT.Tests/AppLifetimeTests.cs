using PetGPT.Shell;
using Xunit;

namespace PetGPT.Tests;

public sealed class AppLifetimeTests
{
    [Fact]
    public async Task ShutdownAsync_RunsTheRequiredOrderOnceForDuplicateRequests()
    {
        var calls = new List<string>();
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppShutdownCoordinator(new AppShutdownOperations(
            PrepareOwnedSurfacesForShutdown: () => calls.Add("prevent-browser-work"),
            FlushSettingsAsync: async cancellationToken =>
            {
                calls.Add("flush-settings");
                flushStarted.SetResult();
                await allowFlush.Task.WaitAsync(cancellationToken);
            },
            DisposeBrowserAsync: () =>
            {
                calls.Add("dispose-browser");
                return Task.CompletedTask;
            },
            CloseBubble: () => calls.Add("close-bubble"),
            ClosePet: () => calls.Add("close-pet"),
            DisposeTray: () => calls.Add("dispose-tray"),
            ReleaseInstanceGuard: () => calls.Add("release-instance"),
            ShutdownApplication: () => calls.Add("shutdown-application")),
            TimeSpan.FromSeconds(5));

        var first = coordinator.ShutdownAsync();
        await flushStarted.Task;
        var duplicate = coordinator.ShutdownAsync();

        Assert.Same(first, duplicate);
        Assert.False(coordinator.AcceptsIntents);

        allowFlush.SetResult();
        await Task.WhenAll(first, duplicate);

        Assert.Equal(
            [
                "prevent-browser-work",
                "flush-settings",
                "dispose-browser",
                "close-bubble",
                "close-pet",
                "dispose-tray",
                "release-instance",
                "shutdown-application"
            ],
            calls);
    }

    [Fact]
    public async Task ShutdownAsync_ContinuesCleanupWhenFlushFails()
    {
        var calls = new List<string>();
        var coordinator = new AppShutdownCoordinator(new AppShutdownOperations(
            PrepareOwnedSurfacesForShutdown: () => calls.Add("prevent-browser-work"),
            FlushSettingsAsync: _ => throw new IOException("simulated write failure"),
            DisposeBrowserAsync: () =>
            {
                calls.Add("dispose-browser");
                return Task.CompletedTask;
            },
            CloseBubble: () => calls.Add("close-bubble"),
            ClosePet: () => calls.Add("close-pet"),
            DisposeTray: () => calls.Add("dispose-tray"),
            ReleaseInstanceGuard: () => calls.Add("release-instance"),
            ShutdownApplication: () => calls.Add("shutdown-application")),
            TimeSpan.FromSeconds(1));

        await coordinator.ShutdownAsync();

        Assert.Equal(
            [
                "prevent-browser-work",
                "dispose-browser",
                "close-bubble",
                "close-pet",
                "dispose-tray",
                "release-instance",
                "shutdown-application"
            ],
            calls);
    }

    [Fact]
    public async Task ShutdownAsync_ContinuesCleanupWhenFlushIgnoresCancellation()
    {
        var calls = new List<string>();
        var coordinator = new AppShutdownCoordinator(new AppShutdownOperations(
            PrepareOwnedSurfacesForShutdown: () => calls.Add("prepare"),
            FlushSettingsAsync: _ => Task.Delay(Timeout.InfiniteTimeSpan),
            DisposeBrowserAsync: () =>
            {
                calls.Add("dispose-browser");
                return Task.CompletedTask;
            },
            CloseBubble: () => calls.Add("close-bubble"),
            ClosePet: () => calls.Add("close-pet"),
            DisposeTray: () => calls.Add("dispose-tray"),
            ReleaseInstanceGuard: () => calls.Add("release-instance"),
            ShutdownApplication: () => calls.Add("shutdown-application")),
            TimeSpan.FromMilliseconds(25));

        await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            [
                "prepare",
                "dispose-browser",
                "close-bubble",
                "close-pet",
                "dispose-tray",
                "release-instance",
                "shutdown-application"
            ],
            calls);
    }

    [Fact]
    public async Task SessionEndingCleanup_LeavesFinalShutdownToWpf()
    {
        var calls = new List<string>();
        var coordinator = new AppShutdownCoordinator(new AppShutdownOperations(
            PrepareOwnedSurfacesForShutdown: () => calls.Add("prepare"),
            FlushSettingsAsync: _ => Task.CompletedTask,
            DisposeBrowserAsync: () => Task.CompletedTask,
            CloseBubble: () => calls.Add("close-bubble"),
            ClosePet: () => calls.Add("close-pet"),
            DisposeTray: () => calls.Add("dispose-tray"),
            ReleaseInstanceGuard: () => calls.Add("release-instance"),
            ShutdownApplication: () => calls.Add("shutdown-application")),
            TimeSpan.FromSeconds(1));

        await coordinator.ShutdownAsync(shutdownApplication: false);

        Assert.Equal(
            ["prepare", "close-bubble", "close-pet", "dispose-tray", "release-instance"],
            calls);
    }

    [Fact]
    public void MutexName_IsStableAndScopedToTheUser()
    {
        var first = SingleInstanceGuard.BuildMutexName("S-1-5-21-100");
        var sameUser = SingleInstanceGuard.BuildMutexName("S-1-5-21-100");
        var otherUser = SingleInstanceGuard.BuildMutexName("S-1-5-21-200");

        Assert.Equal(first, sameUser);
        Assert.NotEqual(first, otherUser);
        Assert.StartsWith("Global\\PetGPT-v2-", first, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-100", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutexOwnership_AllowsOnlyOneOwnerAndRecoversAfterRelease()
    {
        var userScope = $"PetGPT.Tests.{Guid.NewGuid():N}";
        using var first = SingleInstanceGuard.TryAcquire(userScope);
        var duplicateWasRejected = await Task.Run(() =>
        {
            using var duplicate = SingleInstanceGuard.TryAcquire(userScope);
            return duplicate is null;
        });

        Assert.NotNull(first);
        Assert.True(duplicateWasRejected);

        first.Dispose();
        var replacementAcquired = await Task.Run(() =>
        {
            using var replacement = SingleInstanceGuard.TryAcquire(userScope);
            return replacement is not null;
        });
        Assert.True(replacementAcquired);
    }
}
