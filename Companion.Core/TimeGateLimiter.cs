namespace Companion.Core;

public sealed class TimeGateLimiter
{
    private TimeSpan _minInterval;
    private readonly object _lock = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public TimeGateLimiter(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public bool TryAcquire(DateTimeOffset now, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            if (now >= _nextAllowed)
            {
                _nextAllowed = now + _minInterval;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = _nextAllowed - now;
            return false;
        }
    }

    public void SetMinInterval(TimeSpan minInterval)
    {
        lock (_lock)
        {
            _minInterval = minInterval;
            _nextAllowed = DateTimeOffset.MinValue;
        }
    }
}
