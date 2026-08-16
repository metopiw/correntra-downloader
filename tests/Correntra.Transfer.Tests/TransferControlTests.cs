#pragma warning disable xUnit1030 // These tests exercise context-free async primitives.

namespace Correntra.Transfer.Tests;

public sealed class TransferControlTests
{
    [Fact]
    public async Task PauseController_BlocksUntilResumeAndHonorsCancellation()
    {
        var controller = new PauseController();
        controller.Pause();
        var wait = controller.Token.WaitWhilePausedAsync();

        Assert.False(wait.IsCompleted);
        controller.Resume();
        await wait.ConfigureAwait(false);

        controller.Pause();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.Token.WaitWhilePausedAsync(cancellation.Token)).ConfigureAwait(false);
    }

    [Fact]
    public async Task TokenBucketLimiter_ThrottlesAfterBurstIsConsumed()
    {
        var limiter = new TokenBucketBandwidthLimiter(2_000, 10);

        var lease = await limiter.AcquireAsync(20).ConfigureAwait(false);

        Assert.True(lease.WasThrottled);
    }
}

#pragma warning restore xUnit1030
