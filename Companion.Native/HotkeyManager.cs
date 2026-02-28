using Companion.Core;

namespace Companion.Native;

public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xC011;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;
    private HotkeySettings? _current;

    public event EventHandler? HotkeyPressed;

    public bool TryRegister(IntPtr hwnd, HotkeySettings settings, out int win32Error)
    {
        if (hwnd == IntPtr.Zero)
        {
            win32Error = 0;
            return false;
        }

        if (!settings.TryValidate(out _))
        {
            win32Error = 0;
            return false;
        }

        if (_registered)
        {
            Win32.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }

        _hwnd = hwnd;
        var modifiers = GetModifierMask(settings);

        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers, (uint)settings.VirtualKey))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        _current = settings.Clone();
        _registered = true;
        win32Error = 0;
        return true;
    }

    public void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public HotkeySettings? GetCurrentHotkey()
    {
        return _current?.Clone();
    }

    public void Dispose()
    {
        Unregister();
        GC.SuppressFinalize(this);
    }

    private static uint GetModifierMask(HotkeySettings hotkey)
    {
        uint modifiers = 0;
        if (hotkey.Control)
        {
            modifiers |= Win32.MOD_CONTROL;
        }

        if (hotkey.Alt)
        {
            modifiers |= Win32.MOD_ALT;
        }

        if (hotkey.Shift)
        {
            modifiers |= Win32.MOD_SHIFT;
        }

        return modifiers;
    }
}
