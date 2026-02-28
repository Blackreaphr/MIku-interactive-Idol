using System.Diagnostics;
using Companion.Core;
using Companion.Native;

namespace Companion.App.Services;

internal sealed class WindowQueryService : IWindowQueryService
{
    private readonly Func<IntPtr> _getOverlayHwnd;

    public WindowQueryService(Func<IntPtr> getOverlayHwnd)
    {
        _getOverlayHwnd = getOverlayHwnd;
    }

    public IReadOnlyList<WindowInfo> ListTopLevelWindows()
    {
        var windows = new List<WindowInfo>();
        var overlay = _getOverlayHwnd();

        Win32Windows.EnumWindows(
            (hWnd, _) =>
            {
                var info = BuildInfo(hWnd, overlay);
                if (info is not null)
                {
                    windows.Add(info);
                }

                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    public WindowInfo? GetActiveWindow()
    {
        var hWnd = Win32Windows.NormalizeToTopLevelWindow(Win32Windows.GetForegroundWindow());
        return BuildInfo(hWnd, _getOverlayHwnd());
    }

    public WindowInfo? GetWindowUnderCursor()
    {
        if (!Win32Windows.GetCursorPos(out var point))
        {
            return null;
        }

        var hWnd = Win32Windows.NormalizeToTopLevelWindow(Win32Windows.WindowFromPoint(point));
        return BuildInfo(hWnd, _getOverlayHwnd());
    }

    public WindowInfo? GetWindowByHandle(long hwnd)
    {
        var hWnd = Win32Windows.NormalizeToTopLevelWindow(new IntPtr(hwnd));
        return BuildInfo(hWnd, _getOverlayHwnd());
    }

    private static WindowInfo? BuildInfo(IntPtr hWnd, IntPtr overlayHwnd)
    {
        if (hWnd == IntPtr.Zero || hWnd == overlayHwnd)
        {
            return null;
        }

        if (!Win32Windows.IsWindowVisible(hWnd))
        {
            return null;
        }

        var title = Win32Windows.GetTitle(hWnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (!Win32Windows.GetWindowRect(hWnd, out var rect))
        {
            return null;
        }

        Win32Windows.GetWindowThreadProcessId(hWnd, out var pid);
        var processName = SafeProcessName(pid);

        return new WindowInfo(
            Hwnd: hWnd.ToInt64(),
            Title: title,
            ProcessName: processName,
            X: rect.Left,
            Y: rect.Top,
            Width: rect.Width,
            Height: rect.Height);
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
