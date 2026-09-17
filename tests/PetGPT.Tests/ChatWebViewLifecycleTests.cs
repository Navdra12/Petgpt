using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class ChatWebViewLifecycleTests
{
    [Fact]
    public async Task InitializeAsync_SharesOneInFlightOperation()
    {
        var lifecycle = new ChatWebViewLifecycle();
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var first = lifecycle.InitializeAsync(_ =>
        {
            calls++;
            return initialization.Task;
        });
        var second = lifecycle.InitializeAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        Assert.Same(first, second);
        Assert.Equal(ChatWebViewLifecycleState.Initializing, lifecycle.State);
        Assert.Equal(1, calls);

        initialization.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(ChatWebViewLifecycleState.Ready, lifecycle.State);
        Assert.True(lifecycle.CanUseBrowser);
    }

    [Fact]
    public async Task InitializeAsync_FailureIsRememberedAndCannotRetry()
    {
        var lifecycle = new ChatWebViewLifecycle();
        var failure = new InvalidOperationException("browser unavailable");
        var calls = 0;

        var first = lifecycle.InitializeAsync(_ =>
        {
            calls++;
            return Task.FromException(failure);
        });

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var second = lifecycle.InitializeAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        Assert.Same(failure, observed);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(ChatWebViewLifecycleState.Failed, lifecycle.State);
        Assert.False(lifecycle.CanUseBrowser);
    }

    [Fact]
    public async Task DisposeBeforeInitializationCompletes_CannotReturnToReady()
    {
        var lifecycle = new ChatWebViewLifecycle();
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializeTask = lifecycle.InitializeAsync(_ => initialization.Task);

        Assert.True(lifecycle.BeginDisposal());
        lifecycle.CompleteDisposal();
        initialization.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializeTask);
        Assert.Equal(ChatWebViewLifecycleState.Disposed, lifecycle.State);
        Assert.False(lifecycle.CanUseBrowser);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => lifecycle.InitializeAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public void Disposal_IsIdempotent()
    {
        var lifecycle = new ChatWebViewLifecycle();

        Assert.True(lifecycle.BeginDisposal());
        lifecycle.CompleteDisposal();

        Assert.False(lifecycle.BeginDisposal());
        lifecycle.CompleteDisposal();
        Assert.Equal(ChatWebViewLifecycleState.Disposed, lifecycle.State);
    }
}
