using System.Diagnostics;

namespace Correntra.Transfer;

public readonly record struct BandwidthLease(TimeSpan Waited)
{
    public bool WasThrottled => Waited > TimeSpan.Zero;
}

public interface IBandwidthLimiter
{
    ValueTask<BandwidthLease> AcquireAsync(int byteCount, CancellationToken cancellationToken = default);
}

public sealed class UnlimitedBandwidthLimiter : IBandwidthLimiter
{
    public static UnlimitedBandwidthLimiter Instance { get; } = new();

    private UnlimitedBandwidthLimiter()
    {
    }

    public ValueTask<BandwidthLease> AcquireAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new BandwidthLease(TimeSpan.Zero));
    }
}

/// <summary>A thread-safe token bucket shared by all callers of an instance.</summary>
public sealed class TokenBucketBandwidthLimiter : IBandwidthLimiter
{
    private const long MinimumBurstBytes = 64 * 1024;
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private readonly long capacity;
    private readonly long bytesPerSecond;
    private double availableTokens;
    private long lastTimestamp;

    public TokenBucketBandwidthLimiter(
        long bytesPerSecond,
        long? burstCapacityBytes = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSecond, 1);
        var requestedCapacity = burstCapacityBytes ?? Math.Max(bytesPerSecond, MinimumBurstBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedCapacity, 1);

        this.bytesPerSecond = bytesPerSecond;
        capacity = requestedCapacity;
        availableTokens = capacity;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        lastTimestamp = this.timeProvider.GetTimestamp();
    }

    public async ValueTask<BandwidthLease> AcquireAsync(
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0)
        {
            return new BandwidthLease(TimeSpan.Zero);
        }

        Stopwatch? stopwatch = null;
        long remaining = byteCount;
        while (remaining > 0)
        {
            var portion = Math.Min(remaining, capacity);
            if (await AcquirePortionAsync(portion, cancellationToken).ConfigureAwait(false))
            {
                stopwatch ??= Stopwatch.StartNew();
            }

            remaining -= portion;
        }

        return new BandwidthLease(stopwatch?.Elapsed ?? TimeSpan.Zero);
    }

    private async ValueTask<bool> AcquirePortionAsync(long amount, CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            TimeSpan delay;
            lock (sync)
            {
                Refill();
                if (availableTokens >= amount)
                {
                    availableTokens -= amount;
                    return waited;
                }

                var missingTokens = amount - availableTokens;
                delay = TimeSpan.FromSeconds(missingTokens / bytesPerSecond);
            }

            if (delay < TimeSpan.FromMilliseconds(1))
            {
                delay = TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            waited = true;
        }
    }

    private void Refill()
    {
        var now = timeProvider.GetTimestamp();
        var elapsed = timeProvider.GetElapsedTime(lastTimestamp, now).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        availableTokens = Math.Min(capacity, availableTokens + (elapsed * bytesPerSecond));
        lastTimestamp = now;
    }
}
