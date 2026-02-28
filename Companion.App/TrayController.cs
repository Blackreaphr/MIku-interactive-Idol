using System.Drawing;
using System.Windows.Forms;

namespace Companion.App;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleOverlayItem;
    private readonly ToolStripMenuItem _toggleMischiefItem;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _forceOffItem;
    private readonly ToolStripMenuItem _exitItem;

    private readonly Func<bool> _isOverlayVisible;
    private readonly Action _toggleOverlay;
    private readonly Func<bool> _isMischiefEnabled;
    private readonly Action<bool> _setMischiefEnabled;
    private readonly Func<Task> _captureAsync;
    private readonly Action _openSettings;
    private readonly Action _forceOff;
    private readonly Action _exit;

    public TrayController(
        Func<bool> isOverlayVisible,
        Action toggleOverlay,
        Func<bool> isMischiefEnabled,
        Action<bool> setMischiefEnabled,
        Func<Task> captureAsync,
        Action openSettings,
        Action forceOff,
        Action exit)
    {
        _isOverlayVisible = isOverlayVisible;
        _toggleOverlay = toggleOverlay;
        _isMischiefEnabled = isMischiefEnabled;
        _setMischiefEnabled = setMischiefEnabled;
        _captureAsync = captureAsync;
        _openSettings = openSettings;
        _forceOff = forceOff;
        _exit = exit;

        _toggleOverlayItem = new ToolStripMenuItem("Hide Overlay");
        _toggleOverlayItem.Click += (_, _) => _toggleOverlay();

        _toggleMischiefItem = new ToolStripMenuItem("Mischief Enabled")
        {
            CheckOnClick = true
        };
        _toggleMischiefItem.Click += (_, _) => _setMischiefEnabled(_toggleMischiefItem.Checked);

        _captureItem = new ToolStripMenuItem("Capture Screenshot");
        _captureItem.Click += async (_, _) => await _captureAsync();

        _settingsItem = new ToolStripMenuItem("Settings");
        _settingsItem.Click += (_, _) => _openSettings();

        _forceOffItem = new ToolStripMenuItem("Force Off");
        _forceOffItem.Click += (_, _) => _forceOff();

        _exitItem = new ToolStripMenuItem("Exit");
        _exitItem.Click += (_, _) => _exit();

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RefreshMenuState();
        menu.Items.AddRange(
            new ToolStripItem[]
            {
                _toggleOverlayItem,
                _toggleMischiefItem,
                _captureItem,
                _settingsItem,
                _forceOffItem,
                new ToolStripSeparator(),
                _exitItem
            });

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Companion",
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => _toggleOverlay();
    }

    public void SetMischiefState(bool enabled)
    {
        _toggleMischiefItem.Checked = enabled;
    }

    public void SetOverlayVisibility(bool visible)
    {
        _toggleOverlayItem.Text = visible ? "Hide Overlay" : "Show Overlay";
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(1500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RefreshMenuState()
    {
        SetOverlayVisibility(_isOverlayVisible());
        SetMischiefState(_isMischiefEnabled());
    }
}
