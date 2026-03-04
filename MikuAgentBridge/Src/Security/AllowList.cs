using System.Diagnostics;
using System.Runtime.InteropServices;
using MikuAgentBridge.Config;

namespace MikuAgentBridge.Security;

public sealed record ForegroundPolicyDecision(bool Allowed, string ProcessName, string Reason);

public sealed class AllowList
{
    public ForegroundPolicyDecision EvaluateForegroundForMischief(Settings settings)
    {
        var processName = GetForegroundProcessName();
        var processToken = Settings.NormalizeProcessName(processName);

        if (string.IsNullOrWhiteSpace(processToken))
        {
            return new ForegroundPolicyDecision(false, "unknown", "Could not resolve foreground process.");
        }

        var overrideList = Settings.NormalizeProcessList(settings.HardDenyOverrideProcesses);
        if (ContainsProcess(Settings.HardDenyProcesses, processToken) && !ContainsProcess(overrideList, processToken))
        {
            return new ForegroundPolicyDecision(
                false,
                processToken,
                $"Denied by hard deny list. Process '{processToken}' requires explicit override.");
        }

        var allowList = Settings.NormalizeProcessList(settings.AllowedProcessesForMischief);
        if (allowList.Count == 0)
        {
            if (settings.RequireAllowListForMischief)
            {
                return new ForegroundPolicyDecision(
                    false,
                    processToken,
                    "Denied by AllowList. No allowed process names configured.");
            }

            return new ForegroundPolicyDecision(true, processToken, string.Empty);
        }

        if (!ContainsProcess(allowList, processToken))
        {
            return new ForegroundPolicyDecision(
                false,
                processToken,
                $"Denied by AllowList. Process '{processToken}' is not allowed.");
        }

        return new ForegroundPolicyDecision(true, processToken, string.Empty);
    }

    public static string GetForegroundProcessName()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return "unknown";
            }

            _ = GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
            {
                return "unknown";
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool ContainsProcess(IEnumerable<string> values, string processToken)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, processToken, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
