using System.Diagnostics;
using Companion.Core;
using Companion.Native;

namespace Companion.App.Services;

internal sealed record CursorGrabAttemptResult(
    bool Success,
    string Code,
    string Message,
    int? RetryAfterMs = null,
    int? EffectiveDurationMs = null,
    int? EffectiveRectSizePx = null,
    int? AnchorX = null,
    int? AnchorY = null,
    string? ForegroundProcess = null);

internal interface ICursorGrabNativeApi
{
    bool TryGetCursorPos(out int x, out int y);

    bool TryClipCursor(Win32.RECT rect);

    bool TryReleaseCursorClip();

    string GetForegroundProcessName();
}

internal sealed class Win32CursorGrabNativeApi : ICursorGrabNativeApi
{
    public bool TryGetCursorPos(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!Win32.GetCursorPos(out var point))
        {
            return false;
        }

        x = point.X;
        y = point.Y;
        return true;
    }

    public bool TryClipCursor(Win32.RECT rect)
    {
        return Win32.ClipCursor(rect);
    }

    public bool TryReleaseCursorClip()
    {
        return Win32.ReleaseCursorClip();
    }

    public string GetForegroundProcessName()
    {
        var foreground = Win32Windows.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return "unknown";
        }

        Win32Windows.GetWindowThreadProcessId(foreground, out var pid);
        return SafeProcessName(pid);
    }

    private static string SafeProcessName(uint pid)
    {
        try
        {
            if (pid == 0)
            {
                return "unknown";
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }
}

internal sealed class CursorGrabService
{
    private readonly object _lock = new();
    private readonly MischiefGate _gate;
    private readonly Func<CompanionSettings> _getSettings;
    private readonly StructuredFileLogger? _logger;
    private readonly ICursorGrabNativeApi _nativeApi;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly int _watchdogDurationMs;

    private bool _isActive;
    private long _activeSessionId;
    private int _activeCooldownMs;
    private DateTimeOffset _activeUntilUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _cooldownUntilUtc = DateTimeOffset.MinValue;

    public CursorGrabService(
        MischiefGate gate,
        Func<CompanionSettings> getSettings,
        StructuredFileLogger? logger = null,
        ICursorGrabNativeApi? nativeApi = null,
        Func<DateTimeOffset>? utcNow = null,
        int watchdogDurationMs = CompanionSettings.CursorGrabDurationHardMaxMs)
    {
        _gate = gate;
        _getSettings = getSettings;
        _logger = logger;
        _nativeApi = nativeApi ?? new Win32CursorGrabNativeApi();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _watchdogDurationMs = Math.Clamp(watchdogDurationMs, 1, CompanionSettings.CursorGrabDurationHardMaxMs);
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _isActive;
            }
        }
    }

    public CursorGrabAttemptResult TryGrabOnce(int? requestedDurationMs = null, int? requestedRectSizePx = null)
    {
        var settings = _getSettings().Clone();
        if (!TryResolveEffectiveSettings(
                settings,
                requestedDurationMs,
                requestedRectSizePx,
                out var durationMs,
                out var rectSizePx,
                out var validationFailure))
        {
            return validationFailure!;
        }

        if (!_gate.CanExecute(CompanionActionKind.Mischief))
        {
            return Denied("Denied by MischiefGate.");
        }

        if (!settings.CursorGrabEnabled)
        {
            return Denied("Denied by CursorGrabEnabled.");
        }

        var now = _utcNow();
        lock (_lock)
        {
            if (_isActive)
            {
                var retryAfterMs = Math.Max(1, ToRetryAfterMs(_activeUntilUtc, now));
                return RateLimited("Cursor grab is already active.", retryAfterMs);
            }

            if (now < _cooldownUntilUtc)
            {
                return RateLimited("Cursor grab is cooling down.", ToRetryAfterMs(_cooldownUntilUtc, now));
            }
        }

        var processName = _nativeApi.GetForegroundProcessName();
        if (!IsForegroundAllowed(settings, processName, out var code, out var message))
        {
            return new CursorGrabAttemptResult(
                Success: false,
                Code: code,
                Message: message,
                ForegroundProcess: processName);
        }

        if (!_nativeApi.TryGetCursorPos(out var anchorX, out var anchorY))
        {
            return FailNative("GetCursorPos failed.");
        }

        var clipRect = BuildClipRect(anchorX, anchorY, rectSizePx);
        var clipApplied = false;
        var stateCommitted = false;
        long sessionId = 0;

        try
        {
            if (!_nativeApi.TryClipCursor(clipRect))
            {
                return FailNative("ClipCursor failed.");
            }

            clipApplied = true;

            lock (_lock)
            {
                var activeStart = _utcNow();
                _isActive = true;
                _activeCooldownMs = settings.CursorGrabCooldownMs;
                _activeUntilUtc = activeStart + TimeSpan.FromMilliseconds(Math.Min(durationMs, _watchdogDurationMs));
                _activeSessionId++;
                sessionId = _activeSessionId;
            }

            stateCommitted = true;

            _ = StartReleaseTimerAsync(sessionId, TimeSpan.FromMilliseconds(durationMs), "duration_elapsed");
            _ = StartReleaseTimerAsync(sessionId, TimeSpan.FromMilliseconds(_watchdogDurationMs), "watchdog_elapsed");

            _logger?.Info(
                "cursor_grab_success",
                new Dictionary<string, object?>
                {
                    ["durationMs"] = durationMs,
                    ["rectSizePx"] = rectSizePx,
                    ["anchorX"] = anchorX,
                    ["anchorY"] = anchorY,
                    ["foregroundProcess"] = processName
                });

            return new CursorGrabAttemptResult(
                Success: true,
                Code: "ok",
                Message: "Cursor grab applied.",
                EffectiveDurationMs: durationMs,
                EffectiveRectSizePx: rectSizePx,
                AnchorX: anchorX,
                AnchorY: anchorY,
                ForegroundProcess: processName);
        }
        catch (Exception ex)
        {
            _logger?.Error(
                "cursor_grab_failed",
                ex,
                new Dictionary<string, object?>
                {
                    ["durationMs"] = durationMs,
                    ["rectSizePx"] = rectSizePx
                });

            return FailNative("Cursor grab failed unexpectedly.");
        }
        finally
        {
            if (clipApplied && !stateCommitted)
            {
                TryReleaseCursorClip("startup_failure");
            }
        }
    }

    public void ForceRelease(string reason)
    {
        ReleaseInternal(reason, expectedSessionId: null);
    }

    private static bool TryResolveEffectiveSettings(
        CompanionSettings settings,
        int? requestedDurationMs,
        int? requestedRectSizePx,
        out int effectiveDurationMs,
        out int effectiveRectSizePx,
        out CursorGrabAttemptResult? failure)
    {
        effectiveDurationMs = settings.CursorGrabDurationMs;
        effectiveRectSizePx = settings.CursorGrabRectSizePx;
        failure = null;

        if (requestedDurationMs.HasValue)
        {
            var requested = requestedDurationMs.Value;
            if (requested is < 1 or > CompanionSettings.CursorGrabDurationHardMaxMs)
            {
                failure = InvalidRequest(
                    $"durationMs must be in range 1..{CompanionSettings.CursorGrabDurationHardMaxMs}.");
                return false;
            }

            if (requested > settings.CursorGrabDurationMs)
            {
                failure = InvalidRequest("durationMs cannot exceed the configured cursor grab duration.");
                return false;
            }

            effectiveDurationMs = requested;
        }

        if (requestedRectSizePx.HasValue)
        {
            var requested = requestedRectSizePx.Value;
            if (requested is < 4 or > 200)
            {
                failure = InvalidRequest("rectSizePx must be in range 4..200.");
                return false;
            }

            if (requested > settings.CursorGrabRectSizePx)
            {
                failure = InvalidRequest("rectSizePx cannot exceed the configured cursor grab rectangle size.");
                return false;
            }

            effectiveRectSizePx = requested;
        }

        return true;
    }

    private static Win32.RECT BuildClipRect(int anchorX, int anchorY, int rectSizePx)
    {
        var half = rectSizePx / 2;
        var left = (long)anchorX - half;
        var top = (long)anchorY - half;
        var right = left + rectSizePx;
        var bottom = top + rectSizePx;

        return new Win32.RECT
        {
            Left = ClampToInt32(left),
            Top = ClampToInt32(top),
            Right = ClampToInt32(right),
            Bottom = ClampToInt32(bottom)
        };
    }

    private static int ClampToInt32(long value)
    {
        if (value < int.MinValue)
        {
            return int.MinValue;
        }

        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)value;
    }

    private static int ToRetryAfterMs(DateTimeOffset untilUtc, DateTimeOffset nowUtc)
    {
        return Math.Max(0, (int)Math.Ceiling((untilUtc - nowUtc).TotalMilliseconds));
    }

    private bool IsForegroundAllowed(
        CompanionSettings settings,
        string processName,
        out string code,
        out string message)
    {
        if (!settings.CursorGrabRequireAllowList)
        {
            code = "ok";
            message = string.Empty;
            return true;
        }

        var allowlist = CompanionSettings.NormalizeAllowlist(settings.AllowedProcessesForCursorGrab);
        if (allowlist.Count == 0)
        {
            code = "action_denied";
            message = "Denied by CursorGrabAllowList. Cursor grab allowlist is empty.";
            return false;
        }

        var token = CompanionSettings.NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = "unknown";
        }

        if (!allowlist.Contains(token, StringComparer.Ordinal))
        {
            code = "not_allowed_process";
            message = $"Denied by Allowlist. Process '{token}' is not allowed.";
            return false;
        }

        code = "ok";
        message = string.Empty;
        return true;
    }

    private async Task StartReleaseTimerAsync(long sessionId, TimeSpan delay, string reason)
    {
        try
        {
            await Task.Delay(delay);
            ReleaseInternal(reason, sessionId);
        }
        catch (Exception ex)
        {
            _logger?.Error(
                "cursor_grab_timer_failed",
                ex,
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["sessionId"] = sessionId
                });
            ReleaseInternal("timer_failure", sessionId);
        }
    }

    private void ReleaseInternal(string reason, long? expectedSessionId)
    {
        var shouldRelease = false;
        int cooldownMs = 0;

        lock (_lock)
        {
            if (!_isActive)
            {
                return;
            }

            if (expectedSessionId.HasValue && expectedSessionId.Value != _activeSessionId)
            {
                return;
            }

            shouldRelease = true;
            cooldownMs = Math.Max(0, _activeCooldownMs);
            _isActive = false;
            _activeCooldownMs = 0;
            _activeUntilUtc = DateTimeOffset.MinValue;
            _cooldownUntilUtc = _utcNow() + TimeSpan.FromMilliseconds(cooldownMs);
        }

        if (!shouldRelease)
        {
            return;
        }

        TryReleaseCursorClip(reason);

        _logger?.Info(
            "cursor_grab_released",
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["cooldownMs"] = cooldownMs
            });
    }

    private void TryReleaseCursorClip(string reason)
    {
        try
        {
            if (!_nativeApi.TryReleaseCursorClip())
            {
                _logger?.Warn(
                    "cursor_grab_release_failed",
                    new Dictionary<string, object?> { ["reason"] = reason });
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(
                "cursor_grab_release_failed",
                ex,
                new Dictionary<string, object?> { ["reason"] = reason });
        }
    }

    private static CursorGrabAttemptResult InvalidRequest(string message)
    {
        return new CursorGrabAttemptResult(
            Success: false,
            Code: "invalid_request",
            Message: message);
    }

    private static CursorGrabAttemptResult Denied(string message)
    {
        return new CursorGrabAttemptResult(
            Success: false,
            Code: "action_denied",
            Message: message);
    }

    private static CursorGrabAttemptResult FailNative(string message)
    {
        return new CursorGrabAttemptResult(
            Success: false,
            Code: "native_call_failed",
            Message: message);
    }

    private static CursorGrabAttemptResult RateLimited(string message, int retryAfterMs)
    {
        return new CursorGrabAttemptResult(
            Success: false,
            Code: "rate_limited",
            Message: message,
            RetryAfterMs: Math.Max(0, retryAfterMs));
    }
}
