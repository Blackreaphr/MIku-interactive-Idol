using Companion.Core;

namespace Companion.Tests;

public sealed class TimeGateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsThenRateLimitsUntilIntervalElapses()
    {
        var limiter = new TimeGateLimiter(TimeSpan.FromSeconds(1));
        var now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(limiter.TryAcquire(now, out var firstRetry));
        Assert.Equal(TimeSpan.Zero, firstRetry);

        Assert.False(limiter.TryAcquire(now.AddMilliseconds(500), out var retryAfter));
        Assert.Equal(TimeSpan.FromMilliseconds(500), retryAfter);

        Assert.True(limiter.TryAcquire(now.AddSeconds(1), out var secondRetry));
        Assert.Equal(TimeSpan.Zero, secondRetry);
    }

    [Fact]
    public void SetMinInterval_ResetsLimiterWindow()
    {
        var limiter = new TimeGateLimiter(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UtcNow;
        Assert.True(limiter.TryAcquire(now, out _));

        limiter.SetMinInterval(TimeSpan.FromMilliseconds(100));

        Assert.True(limiter.TryAcquire(now, out _));
    }
}
