namespace Companion.Core;

public sealed class HotkeySettings
{
    public bool Control { get; set; } = true;

    public bool Alt { get; set; } = true;

    public bool Shift { get; set; } = true;

    public int VirtualKey { get; set; } = 0x7B; // F12

    public HotkeySettings Clone()
    {
        return new HotkeySettings
        {
            Control = Control,
            Alt = Alt,
            Shift = Shift,
            VirtualKey = VirtualKey
        };
    }

    public bool TryValidate(out string? error)
    {
        if (!Control && !Alt && !Shift)
        {
            error = "At least one modifier must be selected.";
            return false;
        }

        if (VirtualKey < 1 || VirtualKey > 254)
        {
            error = "Virtual key must be between 1 and 254.";
            return false;
        }

        error = null;
        return true;
    }
}
