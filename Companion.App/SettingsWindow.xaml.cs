using System.Windows;
using Companion.Core;
using Keys = System.Windows.Forms.Keys;

namespace Companion.App;

public partial class SettingsWindow : Window
{
    public CompanionSettings? UpdatedSettings { get; private set; }

    public SettingsWindow(CompanionSettings current)
    {
        InitializeComponent();
        PopulateKeyOptions();
        LoadFrom(current);
    }

    private void PopulateKeyOptions()
    {
        for (var vk = (int)Keys.F1; vk <= (int)Keys.F24; vk++)
        {
            KeyCombo.Items.Add(new KeyOption(vk, ((Keys)vk).ToString()));
        }
    }

    private void LoadFrom(CompanionSettings settings)
    {
        MischiefEnabledCheck.IsChecked = settings.MischiefEnabled;
        MischiefAutoOffBox.Text = settings.MischiefAutoOffMinutes.ToString();

        CaptureEnabledCheck.IsChecked = settings.CaptureEnabled;
        CaptureIntervalBox.Text = settings.CaptureMinIntervalMs.ToString();

        CtrlCheck.IsChecked = settings.Hotkey.Control;
        AltCheck.IsChecked = settings.Hotkey.Alt;
        ShiftCheck.IsChecked = settings.Hotkey.Shift;

        var selected = KeyCombo.Items
            .OfType<KeyOption>()
            .FirstOrDefault(item => item.VirtualKey == settings.Hotkey.VirtualKey);
        KeyCombo.SelectedItem = selected ?? KeyCombo.Items.Cast<object>().FirstOrDefault();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MischiefAutoOffBox.Text, out var autoOffMinutes))
        {
            ShowValidationError("Auto-off minutes must be a valid integer.");
            return;
        }

        if (!int.TryParse(CaptureIntervalBox.Text, out var captureMinInterval))
        {
            ShowValidationError("Capture interval must be a valid integer.");
            return;
        }

        if (KeyCombo.SelectedItem is not KeyOption selectedKey)
        {
            ShowValidationError("Please select a hotkey key.");
            return;
        }

        var next = new CompanionSettings
        {
            MischiefEnabled = MischiefEnabledCheck.IsChecked == true,
            MischiefAutoOffMinutes = autoOffMinutes,
            CaptureEnabled = CaptureEnabledCheck.IsChecked == true,
            CaptureMinIntervalMs = captureMinInterval,
            Hotkey = new HotkeySettings
            {
                Control = CtrlCheck.IsChecked == true,
                Alt = AltCheck.IsChecked == true,
                Shift = ShiftCheck.IsChecked == true,
                VirtualKey = selectedKey.VirtualKey
            }
        };

        if (!next.TryValidate(out var error))
        {
            ShowValidationError(error ?? "Invalid settings.");
            return;
        }

        UpdatedSettings = next;
        DialogResult = true;
        Close();
    }

    private void ShowValidationError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "Companion Settings",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private sealed record KeyOption(int VirtualKey, string Label)
    {
        public override string ToString() => Label;
    }
}
