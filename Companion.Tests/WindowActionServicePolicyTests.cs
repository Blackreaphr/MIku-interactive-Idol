using System.Diagnostics;
using Companion.App.Services;
using Companion.Core;
using Companion.Native;

namespace Companion.Tests;

public sealed class WindowActionServicePolicyTests
{
    private static readonly long TestHwnd = 101;

    [Fact]
    public void MoveResize_DeniesWhenMischiefGateIsOff()
    {
        var gate = new MischiefGate();
        var native = new FakeWindowNativeApi();
        var service = new WindowActionService(
            gate,
            getAllowedProcesses: () => Array.Empty<string>(),
            getOverlayHwnd: static () => IntPtr.Zero,
            nativeApi: native);

        var ok = service.MoveResize(TestHwnd, 10, 10, 640, 480, out var error);

        Assert.False(ok);
        Assert.Contains("MischiefGate", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(native.SetWindowPosCalled);
    }

    [Fact]
    public void Focus_IsAssistiveAndNotGateBlocked()
    {
        var gate = new MischiefGate();
        var native = new FakeWindowNativeApi
        {
            SetForegroundWindowResult = true
        };
        var service = new WindowActionService(
            gate,
            getAllowedProcesses: () => ["notepad"],
            getOverlayHwnd: static () => IntPtr.Zero,
            nativeApi: native);

        var ok = service.Focus(TestHwnd, out var error);

        Assert.True(ok, error);
        Assert.True(native.SetForegroundCalled);
    }

    [Fact]
    public void MischiefAction_DeniesWhenProcessNotAllowlisted()
    {
        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var native = new FakeWindowNativeApi
        {
            ProcessId = (uint)Process.GetCurrentProcess().Id
        };
        var service = new WindowActionService(
            gate,
            getAllowedProcesses: () => ["notepad"],
            getOverlayHwnd: static () => IntPtr.Zero,
            nativeApi: native);

        var ok = service.Minimize(TestHwnd, out var error);

        Assert.False(ok);
        Assert.Contains("Allowlist", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(native.ShowWindowCalled);
    }

    [Fact]
    public void MischiefAction_AllowsWhenProcessAllowlisted()
    {
        var gate = new MischiefGate();
        gate.SetEnabled(true, TimeSpan.FromMinutes(10));

        var processName = Process.GetCurrentProcess().ProcessName;
        var native = new FakeWindowNativeApi
        {
            ProcessId = (uint)Process.GetCurrentProcess().Id,
            Rect = new Win32Windows.RECT
            {
                Left = 100,
                Top = 100,
                Right = 500,
                Bottom = 400
            }
        };
        var service = new WindowActionService(
            gate,
            getAllowedProcesses: () => [$"  {processName}.EXE  "],
            getOverlayHwnd: static () => IntPtr.Zero,
            nativeApi: native);

        var ok = service.Nudge(TestHwnd, 50, 0, out var error);

        Assert.True(ok, error);
        Assert.True(native.SetWindowPosCalled);
    }

    private sealed class FakeWindowNativeApi : IWindowNativeApi
    {
        public bool SetForegroundWindowResult { get; set; } = true;

        public bool SetWindowPosResult { get; set; } = true;

        public bool ShowWindowResult { get; set; } = true;

        public bool PostMessageResult { get; set; } = true;

        public bool IsWindowVisibleResult { get; set; } = true;

        public Win32Windows.RECT Rect { get; set; } = new()
        {
            Left = 0,
            Top = 0,
            Right = 300,
            Bottom = 200
        };

        public uint ProcessId { get; set; } = (uint)Process.GetCurrentProcess().Id;

        public bool SetForegroundCalled { get; private set; }

        public bool SetWindowPosCalled { get; private set; }

        public bool ShowWindowCalled { get; private set; }

        public IntPtr NormalizeToTopLevelWindow(IntPtr hWnd) => hWnd;

        public bool IsWindowVisible(IntPtr hWnd) => IsWindowVisibleResult;

        public bool SetForegroundWindow(IntPtr hWnd)
        {
            SetForegroundCalled = true;
            return SetForegroundWindowResult;
        }

        public bool GetWindowRect(IntPtr hWnd, out Win32Windows.RECT rect)
        {
            rect = Rect;
            return true;
        }

        public bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        {
            SetWindowPosCalled = true;
            return SetWindowPosResult;
        }

        public bool ShowWindow(IntPtr hWnd, int cmdShow)
        {
            ShowWindowCalled = true;
            return ShowWindowResult;
        }

        public bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return PostMessageResult;
        }

        public uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid)
        {
            pid = ProcessId;
            return 1;
        }
    }
}
