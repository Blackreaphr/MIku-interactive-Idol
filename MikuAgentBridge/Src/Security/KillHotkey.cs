using System.Runtime.InteropServices;
using MikuAgentBridge.Config;

namespace MikuAgentBridge.Security;

public sealed class KillHotkey : IDisposable
{
    private const int HotkeyId = 0xB7A1;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private readonly Action _onHotkeyPressed;
    private readonly Action<string>? _log;
    private readonly object _sync = new();

    private Thread? _thread;
    private uint _threadId;
    private bool _running;

    public KillHotkey(Action onHotkeyPressed, Action<string>? log = null)
    {
        _onHotkeyPressed = onHotkeyPressed;
        _log = log;
    }

    public bool Start(HotkeyBinding binding)
    {
        if (!binding.TryValidate(out _))
        {
            return false;
        }

        lock (_sync)
        {
            Stop_NoLock();

            var started = new ManualResetEventSlim(false);
            var startedOk = false;

            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _ = PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);

                var modifiers = BuildModifierMask(binding);
                if (!RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers, (uint)binding.VirtualKey))
                {
                    _log?.Invoke($"Kill hotkey registration failed. Win32Error={Marshal.GetLastWin32Error()}");
                    started.Set();
                    return;
                }

                _running = true;
                startedOk = true;
                started.Set();

                while (true)
                {
                    var result = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                    if (result == 0)
                    {
                        break;
                    }

                    if (result == -1)
                    {
                        _log?.Invoke($"Kill hotkey message loop error. Win32Error={Marshal.GetLastWin32Error()}");
                        break;
                    }

                    if (msg.message == WmHotkey && msg.wParam == (UIntPtr)HotkeyId)
                    {
                        try
                        {
                            _onHotkeyPressed();
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"Kill hotkey callback threw: {ex.Message}");
                        }
                    }
                }

                _ = UnregisterHotKey(IntPtr.Zero, HotkeyId);
                _running = false;
            })
            {
                IsBackground = true,
                Name = "MikuAgentBridge.KillHotkey"
            };

            _thread.Start();
            _ = started.Wait(TimeSpan.FromSeconds(2));

            if (!startedOk)
            {
                Stop_NoLock();
                return false;
            }

            return true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            Stop_NoLock();
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Stop_NoLock()
    {
        if (_thread is null)
        {
            return;
        }

        if (_threadId != 0)
        {
            _ = PostThreadMessageW(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        if (!_thread.Join(1000))
        {
            _log?.Invoke("Kill hotkey thread did not exit cleanly in 1s.");
        }

        _thread = null;
        _threadId = 0;
        _running = false;
    }

    private static uint BuildModifierMask(HotkeyBinding binding)
    {
        var modifiers = 0u;

        if (binding.Alt)
        {
            modifiers |= 0x0001;
        }

        if (binding.Control)
        {
            modifiers |= 0x0002;
        }

        if (binding.Shift)
        {
            modifiers |= 0x0004;
        }

        return modifiers;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }
}
