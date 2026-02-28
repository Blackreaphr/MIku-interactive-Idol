using System.Diagnostics;
using Companion.Core;
using Companion.Native;

namespace Companion.App.Services;

internal interface IWindowNativeApi
{
    IntPtr NormalizeToTopLevelWindow(IntPtr hWnd);

    bool IsWindowVisible(IntPtr hWnd);

    bool SetForegroundWindow(IntPtr hWnd);

    bool GetWindowRect(IntPtr hWnd, out Win32Windows.RECT rect);

    bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    bool ShowWindow(IntPtr hWnd, int cmdShow);

    bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

internal sealed class Win32WindowNativeApi : IWindowNativeApi
{
    public IntPtr NormalizeToTopLevelWindow(IntPtr hWnd) => Win32Windows.NormalizeToTopLevelWindow(hWnd);

    public bool IsWindowVisible(IntPtr hWnd) => Win32Windows.IsWindowVisible(hWnd);

    public bool SetForegroundWindow(IntPtr hWnd) => Win32Windows.SetForegroundWindow(hWnd);

    public bool GetWindowRect(IntPtr hWnd, out Win32Windows.RECT rect) => Win32Windows.GetWindowRect(hWnd, out rect);

    public bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        => Win32Windows.SetWindowPos(hWnd, insertAfter, x, y, width, height, flags);

    public bool ShowWindow(IntPtr hWnd, int cmdShow) => Win32Windows.ShowWindow(hWnd, cmdShow);

    public bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => Win32Windows.PostMessageW(hWnd, msg, wParam, lParam);

    public uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid) => Win32Windows.GetWindowThreadProcessId(hWnd, out pid);
}

internal sealed class WindowActionService : IWindowActionService
{
    private readonly MischiefGate _gate;
    private readonly Func<IReadOnlyCollection<string>> _getAllowedProcesses;
    private readonly Func<IntPtr> _getOverlayHwnd;
    private readonly IWindowNativeApi _nativeApi;

    public WindowActionService(
        MischiefGate gate,
        Func<IReadOnlyCollection<string>> getAllowedProcesses,
        Func<IntPtr> getOverlayHwnd,
        IWindowNativeApi? nativeApi = null)
    {
        _gate = gate;
        _getAllowedProcesses = getAllowedProcesses;
        _getOverlayHwnd = getOverlayHwnd;
        _nativeApi = nativeApi ?? new Win32WindowNativeApi();
    }

    public bool Focus(long hwnd, out string? error)
    {
        if (!TryPrepareTarget(hwnd, requiresMischief: false, out var hWnd, out error))
        {
            return false;
        }

        if (!_nativeApi.SetForegroundWindow(hWnd))
        {
            error = "Focus failed. Some windows block focus, and elevated apps cannot be controlled from a non-elevated process.";
            return false;
        }

        error = null;
        return true;
    }

    public bool MoveResize(long hwnd, int x, int y, int width, int height, out string? error)
    {
        if (!TryPrepareTarget(hwnd, requiresMischief: true, out var hWnd, out error))
        {
            return false;
        }

        var ok = _nativeApi.SetWindowPos(
            hWnd,
            IntPtr.Zero,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            Win32Windows.SWP_NOZORDER | Win32Windows.SWP_NOACTIVATE);

        if (!ok)
        {
            error = "SetWindowPos failed.";
            return false;
        }

        error = null;
        return true;
    }

    public bool Nudge(long hwnd, int dx, int dy, out string? error)
    {
        if (!TryPrepareTarget(hwnd, requiresMischief: true, out var hWnd, out error))
        {
            return false;
        }

        if (!_nativeApi.GetWindowRect(hWnd, out var rect))
        {
            error = "GetWindowRect failed.";
            return false;
        }

        var ok = _nativeApi.SetWindowPos(
            hWnd,
            IntPtr.Zero,
            rect.Left + dx,
            rect.Top + dy,
            Math.Max(1, rect.Width),
            Math.Max(1, rect.Height),
            Win32Windows.SWP_NOZORDER | Win32Windows.SWP_NOACTIVATE);

        if (!ok)
        {
            error = "SetWindowPos failed.";
            return false;
        }

        error = null;
        return true;
    }

    public bool Minimize(long hwnd, out string? error)
    {
        return ExecuteShowWindow(hwnd, Win32Windows.SW_MINIMIZE, out error);
    }

    public bool Maximize(long hwnd, out string? error)
    {
        return ExecuteShowWindow(hwnd, Win32Windows.SW_MAXIMIZE, out error);
    }

    public bool Restore(long hwnd, out string? error)
    {
        return ExecuteShowWindow(hwnd, Win32Windows.SW_RESTORE, out error);
    }

    public bool CloseRequest(long hwnd, out string? error)
    {
        if (!TryPrepareTarget(hwnd, requiresMischief: true, out var hWnd, out error))
        {
            return false;
        }

        var ok = _nativeApi.PostMessageW(hWnd, Win32Windows.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!ok)
        {
            error = "WM_CLOSE post failed.";
            return false;
        }

        error = null;
        return true;
    }

    private bool ExecuteShowWindow(long hwnd, int command, out string? error)
    {
        if (!TryPrepareTarget(hwnd, requiresMischief: true, out var hWnd, out error))
        {
            return false;
        }

        var ok = _nativeApi.ShowWindow(hWnd, command);
        if (!ok)
        {
            error = "ShowWindow failed.";
            return false;
        }

        error = null;
        return true;
    }

    private bool TryPrepareTarget(long hwnd, bool requiresMischief, out IntPtr target, out string? error)
    {
        target = IntPtr.Zero;
        error = null;

        if (hwnd == 0)
        {
            error = "Invalid window handle.";
            return false;
        }

        target = _nativeApi.NormalizeToTopLevelWindow(new IntPtr(hwnd));
        if (target == IntPtr.Zero)
        {
            error = "Invalid window handle.";
            return false;
        }

        if (target == _getOverlayHwnd())
        {
            error = "Denied by OverlayProtection.";
            return false;
        }

        if (!_nativeApi.IsWindowVisible(target))
        {
            error = "Target window is not visible.";
            return false;
        }

        if (!requiresMischief)
        {
            return true;
        }

        if (!_gate.CanExecute(CompanionActionKind.Mischief))
        {
            error = "Denied by MischiefGate.";
            return false;
        }

        if (!IsProcessAllowed(target, out var processName))
        {
            error = $"Denied by Allowlist. Process '{processName}' is not allowed.";
            return false;
        }

        return true;
    }

    private bool IsProcessAllowed(IntPtr hWnd, out string processName)
    {
        _nativeApi.GetWindowThreadProcessId(hWnd, out var pid);
        processName = SafeProcessName(pid);
        var processToken = CompanionSettings.NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(processToken))
        {
            processToken = "unknown";
        }

        var allowlist = CompanionSettings.NormalizeAllowlist(_getAllowedProcesses());
        if (allowlist.Count == 0)
        {
            return true;
        }

        return allowlist.Contains(processToken, StringComparer.Ordinal);
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
