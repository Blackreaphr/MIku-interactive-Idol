namespace MikuAgentBridge.Config;

public sealed class Settings
{
    public bool MischiefEnabled { get; set; }

    public int MischiefAutoOffMinutes { get; set; } = 10;

    public List<string> AllowedProcessesForMischief { get; set; } = new();

    public bool RequireAllowListForMischief { get; set; } = true;

    public bool CaptureEnabled { get; set; } = true;

    public int CaptureMinIntervalMs { get; set; } = 1000;

    public HotkeyBinding KillHotkey { get; set; } = HotkeyBinding.Default();

    public List<string> HardDenyOverrideProcesses { get; set; } = new();

    public int MaxInlineCaptureBytes { get; set; } = 4 * 1024 * 1024;

    public static IReadOnlyCollection<string> HardDenyProcesses { get; } = new[]
    {
        "taskmgr",
        "processhacker",
        "msconfig",
        "regedit"
    };

    public Settings Clone()
    {
        return new Settings
        {
            MischiefEnabled = MischiefEnabled,
            MischiefAutoOffMinutes = MischiefAutoOffMinutes,
            AllowedProcessesForMischief = [..AllowedProcessesForMischief],
            RequireAllowListForMischief = RequireAllowListForMischief,
            CaptureEnabled = CaptureEnabled,
            CaptureMinIntervalMs = CaptureMinIntervalMs,
            KillHotkey = KillHotkey.Clone(),
            HardDenyOverrideProcesses = [..HardDenyOverrideProcesses],
            MaxInlineCaptureBytes = MaxInlineCaptureBytes
        };
    }

    public bool TryValidateAndNormalize(out string? error)
    {
        if (MischiefAutoOffMinutes is < 1 or > 1440)
        {
            error = "MischiefAutoOffMinutes must be in range 1..1440.";
            return false;
        }

        if (CaptureMinIntervalMs is < 100 or > 60000)
        {
            error = "CaptureMinIntervalMs must be in range 100..60000.";
            return false;
        }

        if (MaxInlineCaptureBytes is < 64 * 1024 or > 16 * 1024 * 1024)
        {
            error = "MaxInlineCaptureBytes must be in range 65536..16777216.";
            return false;
        }

        if (!KillHotkey.TryValidate(out error))
        {
            return false;
        }

        AllowedProcessesForMischief = NormalizeProcessList(AllowedProcessesForMischief);
        HardDenyOverrideProcesses = NormalizeProcessList(HardDenyOverrideProcesses);
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

    public static List<string> NormalizeProcessList(IEnumerable<string>? values)
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

        return [..set.OrderBy(static p => p, StringComparer.Ordinal)];
    }
}

public sealed class HotkeyBinding
{
    public bool Control { get; set; } = true;

    public bool Alt { get; set; } = true;

    public bool Shift { get; set; } = true;

    public int VirtualKey { get; set; } = 0x7B;

    public HotkeyBinding Clone()
    {
        return new HotkeyBinding
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
            error = "KillHotkey must include at least one modifier.";
            return false;
        }

        if (VirtualKey is < 1 or > 254)
        {
            error = "KillHotkey.VirtualKey must be in range 1..254.";
            return false;
        }

        error = null;
        return true;
    }

    public static HotkeyBinding Default()
    {
        return new HotkeyBinding();
    }
}
