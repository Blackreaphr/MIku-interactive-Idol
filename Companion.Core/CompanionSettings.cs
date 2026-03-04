namespace Companion.Core;

public sealed class CompanionSettings
{
    public const int CursorGrabDurationHardMaxMs = 1500;

    public bool MischiefEnabled { get; set; }

    public int MischiefAutoOffMinutes { get; set; } = 10;

    public bool CaptureEnabled { get; set; } = true;

    public int CaptureMinIntervalMs { get; set; } = 1000;

    public HotkeySettings Hotkey { get; set; } = new();

    public List<string> AllowedProcessesForMischief { get; set; } = new();

    public bool CursorGrabEnabled { get; set; }

    public int CursorGrabDurationMs { get; set; } = 600;

    public int CursorGrabCooldownMs { get; set; } = 5000;

    public int CursorGrabRectSizePx { get; set; } = 24;

    public bool CursorGrabRequireAllowList { get; set; } = true;

    public List<string> AllowedProcessesForCursorGrab { get; set; } = new();

    public CompanionSettings Clone()
    {
        return new CompanionSettings
        {
            MischiefEnabled = MischiefEnabled,
            MischiefAutoOffMinutes = MischiefAutoOffMinutes,
            CaptureEnabled = CaptureEnabled,
            CaptureMinIntervalMs = CaptureMinIntervalMs,
            Hotkey = Hotkey.Clone(),
            AllowedProcessesForMischief = [.. AllowedProcessesForMischief],
            CursorGrabEnabled = CursorGrabEnabled,
            CursorGrabDurationMs = CursorGrabDurationMs,
            CursorGrabCooldownMs = CursorGrabCooldownMs,
            CursorGrabRectSizePx = CursorGrabRectSizePx,
            CursorGrabRequireAllowList = CursorGrabRequireAllowList,
            AllowedProcessesForCursorGrab = [.. AllowedProcessesForCursorGrab]
        };
    }

    public bool TryValidate(out string? error)
    {
        if (MischiefAutoOffMinutes is < 1 or > 1440)
        {
            error = "Mischief auto-off minutes must be in range 1..1440.";
            return false;
        }

        if (CaptureMinIntervalMs is < 100 or > 60000)
        {
            error = "Capture minimum interval must be in range 100..60000 milliseconds.";
            return false;
        }

        if (!Hotkey.TryValidate(out error))
        {
            return false;
        }

        if (CursorGrabDurationMs is < 1 or > CursorGrabDurationHardMaxMs)
        {
            error = $"Cursor grab duration must be in range 1..{CursorGrabDurationHardMaxMs} milliseconds.";
            return false;
        }

        if (CursorGrabCooldownMs is < 0 or > 60000)
        {
            error = "Cursor grab cooldown must be in range 0..60000 milliseconds.";
            return false;
        }

        if (CursorGrabRectSizePx is < 4 or > 200)
        {
            error = "Cursor grab rectangle size must be in range 4..200 pixels.";
            return false;
        }

        AllowedProcessesForMischief = NormalizeAllowlist(AllowedProcessesForMischief);
        AllowedProcessesForCursorGrab = NormalizeAllowlist(AllowedProcessesForCursorGrab);
        error = null;
        return true;
    }

    public static string NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.EndsWith(".exe", StringComparison.Ordinal))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    public static List<string> NormalizeAllowlist(IEnumerable<string>? values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return [];
        }

        foreach (var value in values)
        {
            var normalized = NormalizeProcessName(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                set.Add(normalized);
            }
        }

        return [.. set.OrderBy(static x => x, StringComparer.Ordinal)];
    }
}
