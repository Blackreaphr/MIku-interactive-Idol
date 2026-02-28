namespace Companion.Core;

public sealed class CompanionSettings
{
    public bool MischiefEnabled { get; set; }

    public int MischiefAutoOffMinutes { get; set; } = 10;

    public bool CaptureEnabled { get; set; } = true;

    public int CaptureMinIntervalMs { get; set; } = 1000;

    public HotkeySettings Hotkey { get; set; } = new();

    public List<string> AllowedProcessesForMischief { get; set; } = new();

    public CompanionSettings Clone()
    {
        return new CompanionSettings
        {
            MischiefEnabled = MischiefEnabled,
            MischiefAutoOffMinutes = MischiefAutoOffMinutes,
            CaptureEnabled = CaptureEnabled,
            CaptureMinIntervalMs = CaptureMinIntervalMs,
            Hotkey = Hotkey.Clone(),
            AllowedProcessesForMischief = [.. AllowedProcessesForMischief]
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

        AllowedProcessesForMischief = NormalizeAllowlist(AllowedProcessesForMischief);
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
