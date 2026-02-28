using Companion.Core;

namespace Companion.Tests;

public sealed class CompanionSettingsTests
{
    [Fact]
    public void TryValidate_FailsOnOutOfRangeIntervals()
    {
        var settings = new CompanionSettings
        {
            MischiefAutoOffMinutes = 0
        };

        Assert.False(settings.TryValidate(out var error));
        Assert.Contains("auto-off", error, StringComparison.OrdinalIgnoreCase);

        settings.MischiefAutoOffMinutes = 10;
        settings.CaptureMinIntervalMs = 50;
        Assert.False(settings.TryValidate(out error));
        Assert.Contains("Capture minimum interval", error);
    }

    [Fact]
    public void TryValidate_FailsOnInvalidHotkey()
    {
        var settings = new CompanionSettings
        {
            Hotkey = new HotkeySettings
            {
                Control = false,
                Alt = false,
                Shift = false,
                VirtualKey = 0x7B
            }
        };

        Assert.False(settings.TryValidate(out var error));
        Assert.Contains("modifier", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_NormalizesAndDeduplicatesAllowlist()
    {
        var settings = new CompanionSettings
        {
            AllowedProcessesForMischief =
            [
                "  Notepad.exe ",
                "NOTEPAD",
                " explorer.EXE",
                "",
                "   "
            ]
        };

        Assert.True(settings.TryValidate(out var error), error);
        Assert.Equal(["explorer", "notepad"], settings.AllowedProcessesForMischief);
    }

    [Fact]
    public void NormalizeProcessName_StripsExeAndLowercases()
    {
        Assert.Equal("chrome", CompanionSettings.NormalizeProcessName("Chrome.EXE"));
        Assert.Equal("explorer", CompanionSettings.NormalizeProcessName(" explorer "));
        Assert.Equal(string.Empty, CompanionSettings.NormalizeProcessName("   "));
    }
}
