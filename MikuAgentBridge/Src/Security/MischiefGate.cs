namespace MikuAgentBridge.Security;

public sealed class MischiefGate
{
    private readonly object _sync = new();
    private bool _enabled;
    private DateTimeOffset? _autoOffAtUtc;

    public event EventHandler? StateChanged;

    public bool Enabled
    {
        get
        {
            lock (_sync)
            {
                return _enabled;
            }
        }
    }

    public void SetEnabled(bool enabled, int autoOffMinutes)
    {
        if (autoOffMinutes < 1)
        {
            autoOffMinutes = 1;
        }

        var changed = false;
        lock (_sync)
        {
            if (_enabled != enabled)
            {
                changed = true;
            }

            _enabled = enabled;
            _autoOffAtUtc = enabled
                ? DateTimeOffset.UtcNow.AddMinutes(autoOffMinutes)
                : null;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ForceOff()
    {
        var changed = false;
        lock (_sync)
        {
            if (_enabled)
            {
                _enabled = false;
                _autoOffAtUtc = null;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanExecuteMischief(out string? deniedReason)
    {
        if (EnsureEnabledAndNotExpired())
        {
            deniedReason = null;
            return true;
        }

        deniedReason = "Denied by MischiefGate.";
        return false;
    }

    public int GetAutoOffRemainingSeconds()
    {
        var shouldNotify = false;
        int remainingSeconds;

        lock (_sync)
        {
            if (!_enabled || !_autoOffAtUtc.HasValue)
            {
                return 0;
            }

            var remaining = _autoOffAtUtc.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _enabled = false;
                _autoOffAtUtc = null;
                shouldNotify = true;
                remainingSeconds = 0;
            }
            else
            {
                remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
            }
        }

        if (shouldNotify)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return remainingSeconds;
    }

    private bool EnsureEnabledAndNotExpired()
    {
        var shouldNotify = false;
        var allowed = false;

        lock (_sync)
        {
            if (!_enabled)
            {
                return false;
            }

            if (!_autoOffAtUtc.HasValue)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow < _autoOffAtUtc.Value)
            {
                allowed = true;
            }
            else
            {
                _enabled = false;
                _autoOffAtUtc = null;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return allowed;
    }
}
