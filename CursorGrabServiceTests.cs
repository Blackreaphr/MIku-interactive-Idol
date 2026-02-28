using Companion.App.Services;
using Companion.Core;
using Companion.Native;

namespace Companion.Tests;

public sealed class CursorGrabServiceTests
{
    [Fact]
    public void TryGrabOnce_DeniesWhenMischiefGateIsOff()
    {
        var settings = BuildSettings();
        var gate = new MischiefGate();
        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();

        Assert.False(result.Success);
        Assert.Equal("action_denied", result.Code);
        Assert.Equal(0, native.ClipCalls);
    }

    [Fact]
    public void TryGrabOnce_DeniesWhenFeatureDisabled()
    {
        var settings = BuildSettings();
        settings.CursorGrabEnabled = false;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();

        Assert.False(result.Success);
        Assert.Equal("action_denied", result.Code);
        Assert.Equal(0, native.ClipCalls);
    }

    [Fact]
    public void TryGrabOnce_DeniesWhenAllowlistRequiredAndEmpty()
    {
        var settings = BuildSettings();
        settings.CursorGrabRequireAllowList = true;
        settings.AllowedProcessesForCursorGrab = [];

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();

        Assert.False(result.Success);
        Assert.Equal("action_denied", result.Code);
        Assert.Equal(0, native.ClipCalls);
    }

    [Fact]
    public void TryGrabOnce_DeniesWhenForegroundProcessNotAllowlisted()
    {
        var settings = BuildSettings();
        settings.CursorGrabRequireAllowList = true;
        settings.AllowedProcessesForCursorGrab = ["notepad"];

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi
        {
            ForegroundProcessName = "chrome"
        };
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();

        Assert.False(result.Success);
        Assert.Equal("not_allowed_process", result.Code);
        Assert.Equal(0, native.ClipCalls);
    }

    [Fact]
    public void TryGrabOnce_FailsWhenNativeCallsFail()
    {
        var settings = BuildSettings();
        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi
        {
            GetCursorPosResult = false
        };
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var getCursorFailure = service.TryGrabOnce();
        Assert.False(getCursorFailure.Success);
        Assert.Equal("native_call_failed", getCursorFailure.Code);

        native.GetCursorPosResult = true;
        native.ClipCursorResult = false;
        var clipFailure = service.TryGrabOnce();
        Assert.False(clipFailure.Success);
        Assert.Equal("native_call_failed", clipFailure.Code);
    }

    [Fact]
    public async Task TryGrabOnce_ReleasesAfterDuration()
    {
        var settings = BuildSettings();
        settings.CursorGrabDurationMs = 30;
        settings.CursorGrabCooldownMs = 20;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();

        Assert.True(result.Success, result.Message);
        Assert.True(service.IsActive);
        Assert.Equal(1, native.ClipCalls);

        await Task.Delay(120);

        Assert.False(service.IsActive);
        Assert.Equal(1, native.ReleaseCalls);
    }

    [Fact]
    public async Task TryGrabOnce_AppliesActiveAndCooldownRateLimits()
    {
        var settings = BuildSettings();
        settings.CursorGrabDurationMs = 40;
        settings.CursorGrabCooldownMs = 120;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var first = service.TryGrabOnce();
        Assert.True(first.Success, first.Message);

        var activeRetry = service.TryGrabOnce();
        Assert.False(activeRetry.Success);
        Assert.Equal("rate_limited", activeRetry.Code);
        Assert.True(activeRetry.RetryAfterMs > 0);

        await Task.Delay(60);

        var cooldownRetry = service.TryGrabOnce();
        Assert.False(cooldownRetry.Success);
        Assert.Equal("rate_limited", cooldownRetry.Code);
        Assert.True(cooldownRetry.RetryAfterMs > 0);

        await Task.Delay(140);

        var afterCooldown = service.TryGrabOnce();
        Assert.True(afterCooldown.Success, afterCooldown.Message);
    }

    [Fact]
    public void TryGrabOnce_AppliesOnlyTightenParamPolicy()
    {
        var settings = BuildSettings();
        settings.CursorGrabDurationMs = 600;
        settings.CursorGrabRectSizePx = 24;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var tightened = service.TryGrabOnce(500, 12);
        Assert.True(tightened.Success, tightened.Message);
        Assert.Equal(500, tightened.EffectiveDurationMs);
        Assert.Equal(12, tightened.EffectiveRectSizePx);
        service.ForceRelease("test_cleanup");

        var durationTooLarge = service.TryGrabOnce(700, null);
        Assert.False(durationTooLarge.Success);
        Assert.Equal("invalid_request", durationTooLarge.Code);

        var rectTooLarge = service.TryGrabOnce(null, 30);
        Assert.False(rectTooLarge.Success);
        Assert.Equal("invalid_request", rectTooLarge.Code);
    }

    [Fact]
    public async Task Watchdog_ReleasesBeforeLongDuration()
    {
        var settings = BuildSettings();
        settings.CursorGrabDurationMs = 1000;
        settings.CursorGrabCooldownMs = 0;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(
            gate,
            () => settings,
            nativeApi: native,
            watchdogDurationMs: 50);

        var result = service.TryGrabOnce();
        Assert.True(result.Success, result.Message);

        await Task.Delay(120);

        Assert.False(service.IsActive);
        Assert.Equal(1, native.ReleaseCalls);
    }

    [Fact]
    public void ForceRelease_IsIdempotent()
    {
        var settings = BuildSettings();
        settings.CursorGrabDurationMs = 500;

        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeCursorGrabNativeApi();
        var service = new CursorGrabService(gate, () => settings, nativeApi: native);

        var result = service.TryGrabOnce();
        Assert.True(result.Success, result.Message);

        service.ForceRelease("manual");
        service.ForceRelease("manual_again");

        Assert.False(service.IsActive);
        Assert.Equal(1, native.ReleaseCalls);
    }

    private static CompanionSettings BuildSettings()
    {
        return new CompanionSettings
        {
            CursorGrabEnabled = true,
            CursorGrabDurationMs = 80,
            CursorGrabCooldownMs = 80,
            CursorGrabRectSizePx = 24,
            CursorGrabRequireAllowList = false
        };
    }

    private sealed class FakeCursorGrabNativeApi : ICursorGrabNativeApi
    {
        public bool GetCursorPosResult { get; set; } = true;

        public bool ClipCursorResult { get; set; } = true;

        public bool ReleaseCursorClipResult { get; set; } = true;

        public string ForegroundProcessName { get; set; } = "chrome";

        public int CursorX { get; set; } = 500;

        public int CursorY { get; set; } = 400;

        public int ClipCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Win32.RECT LastClipRect { get; private set; }

        public bool TryGetCursorPos(out int x, out int y)
        {
            x = CursorX;
            y = CursorY;
            return GetCursorPosResult;
        }

        public bool TryClipCursor(Win32.RECT rect)
        {
            ClipCalls++;
            LastClipRect = rect;
            return ClipCursorResult;
        }

        public bool TryReleaseCursorClip()
        {
            ReleaseCalls++;
            return ReleaseCursorClipResult;
        }

        public string GetForegroundProcessName()
        {
            return ForegroundProcessName;
        }
    }
}
