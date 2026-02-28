namespace Companion.Core;

public sealed class MischiefGate
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _enabled;
    private DateTimeOffset? _autoOffAt;

    public MischiefGate(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
    }

    public event EventHandler? Changed;

    public void SetEnabled(bool enabled, TimeSpan? autoOff = null)
    {
        lock (_lock)
        {
            _enabled = enabled;
            _autoOffAt = enabled && autoOff.HasValue ? _utcNow() + autoOff.Value : null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ForceOff()
    {
        SetEnabled(false);
    }

    public bool CanExecute(CompanionActionKind kind)
    {
        if (kind != CompanionActionKind.Mischief)
        {
            return true;
        }

        var changed = false;
        lock (_lock)
        {
            if (!_enabled)
            {
                return false;
            }

            if (_autoOffAt.HasValue && _utcNow() > _autoOffAt.Value)
            {
                _enabled = false;
                _autoOffAt = null;
                changed = true;
            }
            else
            {
                return true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return false;
    }
}
