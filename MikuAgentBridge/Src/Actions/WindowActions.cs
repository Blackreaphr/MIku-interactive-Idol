using System.Diagnostics;
using System.Runtime.InteropServices;
using MikuAgentBridge.Config;

namespace MikuAgentBridge.Actions;

public sealed record WindowNudgeResult(
    long Hwnd,
    string ProcessName,
    int PreviousX,
    int PreviousY,
    int NewX,
    int NewY,
    int Width,
    int Height);

public sealed class WindowActions
{
    public bool TryNudgeActiveWindow(int dx, int dy, out WindowNudgeResult? result, out string? error)
    {
        result = null;
        error = null;

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            error = "No active window found.";
            return false;
        }

        if (!GetWindowRect(hWnd, out var rect))
        {
            error = "GetWindowRect failed.";
            return false;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        var targetX = rect.Left + dx;
        var targetY = rect.Top + dy;

        var clamped = ClampToVirtualDesktop(targetX, targetY, width, height);

        var ok = SetWindowPos(
            hWnd,
            IntPtr.Zero,
            clamped.X,
            clamped.Y,
            0,
            0,
            SwpNoSize | SwpNoZorder | SwpNoActivate);

        if (!ok)
        {
            error = "SetWindowPos failed.";
            return false;
        }

        var processName = ResolveProcessName(hWnd);
        result = new WindowNudgeResult(
            Hwnd: hWnd.ToInt64(),
            ProcessName: processName,
            PreviousX: rect.Left,
            PreviousY: rect.Top,
            NewX: clamped.X,
            NewY: clamped.Y,
            Width: width,
            Height: height);

        return true;
    }

    public static string GetForegroundProcessToken()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return "unknown";
        }

        return Settings.NormalizeProcessName(ResolveProcessName(foreground));
    }

    private static (int X, int Y) ClampToVirtualDesktop(int x, int y, int width, int height)
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var virtualWidth = GetSystemMetrics(SmCxvirtualscreen);
        var virtualHeight = GetSystemMetrics(SmCyvirtualscreen);

        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            return (x, y);
        }

        var maxX = left + Math.Max(0, virtualWidth - width);
        var maxY = top + Math.Max(0, virtualHeight - height);

        var clampedX = Math.Clamp(x, left, Math.Max(left, maxX));
        var clampedY = Math.Clamp(y, top, Math.Max(top, maxY));
        return (clampedX, clampedY);
    }

    private static string ResolveProcessName(IntPtr hWnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
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

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
