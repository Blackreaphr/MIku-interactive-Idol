using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Companion.Core;
using Companion.Native;

namespace Companion.App;

public partial class OverlayWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly DispatcherTimer _hoverTimer;
    private readonly MischiefGate _gate;
    private bool _clickThroughEnabled;
    private bool _allowClose;

    public OverlayWindow(MischiefGate gate)
    {
        InitializeComponent();
        _gate = gate;

        _hoverTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _hoverTimer.Tick += (_, _) => TickHover();

        Loaded += (_, _) =>
        {
            UpdateMischiefIndicator();
            _hoverTimer.Start();
        };

        _gate.Changed += (_, _) => Dispatcher.Invoke(UpdateMischiefIndicator);
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyBaseStyles();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _hoverTimer.Stop();
        base.OnClosing(e);
    }

    private void ApplyBaseStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_LAYERED;
        ex |= Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        Win32.SetWindowPos(
            _hwnd,
            Win32.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        SetClickThrough(true);
    }

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero || _clickThroughEnabled == enabled)
        {
            return;
        }

        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        if (enabled)
        {
            ex |= Win32.WS_EX_TRANSPARENT;
        }
        else
        {
            ex &= ~Win32.WS_EX_TRANSPARENT;
        }

        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        _clickThroughEnabled = enabled;
    }

    private void TickHover()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        if (!Win32.GetCursorPos(out var cursor))
        {
            return;
        }

        if (CharacterHitbox.ActualWidth <= 0 || CharacterHitbox.ActualHeight <= 0)
        {
            SetClickThrough(true);
            return;
        }

        var topLeft = CharacterHitbox.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = CharacterHitbox.PointToScreen(
            new System.Windows.Point(CharacterHitbox.ActualWidth, CharacterHitbox.ActualHeight));
        var rect = new Rect(topLeft, bottomRight);
        var isOver = rect.Contains(new System.Windows.Point(cursor.X, cursor.Y));
        SetClickThrough(!isOver);
    }

    private void UpdateMischiefIndicator()
    {
        MischiefDot.Visibility = _gate.Enabled ? Visibility.Visible : Visibility.Collapsed;
    }
}
