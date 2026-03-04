using System.Diagnostics;
using System.Text.Json;
using MikuAgentBridge.Actions;
using MikuAgentBridge.Config;
using MikuAgentBridge.Security;

namespace MikuAgentBridge.IPC;

public sealed class CommandRouter : ICommandRouter
{
    private readonly Func<Settings> _getSettings;
    private readonly MischiefGate _mischiefGate;
    private readonly AllowList _allowList;
    private readonly ScreenshotService _screenshotService;
    private readonly WindowActions _windowActions;
    private readonly Func<(bool Found, string? CharacterName)> _getCharacter;
    private readonly Action _queuePushAnimation;
    private readonly Func<string?> _getLastError;
    private readonly Action<string?> _setLastError;
    private readonly string _modVersion;
    private readonly int _desktopMatePid;
    private readonly string? _melonLoaderVersion;

    public CommandRouter(
        Func<Settings> getSettings,
        MischiefGate mischiefGate,
        AllowList allowList,
        ScreenshotService screenshotService,
        WindowActions windowActions,
        Func<(bool Found, string? CharacterName)> getCharacter,
        Action queuePushAnimation,
        Func<string?> getLastError,
        Action<string?> setLastError,
        string modVersion)
    {
        _getSettings = getSettings;
        _mischiefGate = mischiefGate;
        _allowList = allowList;
        _screenshotService = screenshotService;
        _windowActions = windowActions;
        _getCharacter = getCharacter;
        _queuePushAnimation = queuePushAnimation;
        _getLastError = getLastError;
        _setLastError = setLastError;
        _modVersion = modVersion;
        _desktopMatePid = Process.GetCurrentProcess().Id;
        _melonLoaderVersion = typeof(MelonLoader.MelonMod).Assembly.GetName().Version?.ToString();
    }

    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var cmd = request.Cmd.Trim();
            return cmd switch
            {
                "status.get" => HandleStatusGet(request),
                "screen.capture" => await HandleScreenCaptureAsync(request, cancellationToken).ConfigureAwait(false),
                "window.nudge" => HandleWindowNudge(request),
                _ => Failure(request.Id, "unknown_command", $"Unsupported command '{cmd}'.")
            };
        }
        catch (Exception ex)
        {
            return Failure(request.Id, "internal_error", $"Unhandled command error: {ex.Message}");
        }
    }

    private BridgeResponse HandleStatusGet(BridgeRequest request)
    {
        var settings = _getSettings();
        var character = _getCharacter();

        var data = new Dictionary<string, object?>
        {
            ["modVersion"] = _modVersion,
            ["desktopMatePid"] = _desktopMatePid,
            ["melonLoaderVersion"] = _melonLoaderVersion,
            ["characterFound"] = character.Found,
            ["characterName"] = character.CharacterName,
            ["mischiefEnabled"] = _mischiefGate.Enabled,
            ["autoOffRemainingSeconds"] = _mischiefGate.GetAutoOffRemainingSeconds(),
            ["allowList"] = settings.AllowedProcessesForMischief,
            ["captureEnabled"] = settings.CaptureEnabled,
            ["captureMinIntervalMs"] = settings.CaptureMinIntervalMs,
            ["lastError"] = _getLastError()
        };

        return BridgeResponse.Success(request.Id, data);
    }

    private async Task<BridgeResponse> HandleScreenCaptureAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        var target = GetString(request.Args, "target") ?? "virtual_desktop";
        if (!string.Equals(target, "virtual_desktop", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(request.Id, "invalid_request", "screen.capture args.target must be 'virtual_desktop'.");
        }

        var format = GetString(request.Args, "format") ?? "png";
        if (!string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(request.Id, "invalid_request", "screen.capture args.format must be 'png'.");
        }

        var returnMode = GetString(request.Args, "return") ?? "path";
        if (!string.Equals(returnMode, "path", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(returnMode, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(request.Id, "invalid_request", "screen.capture args.return must be 'path' or 'base64'.");
        }

        try
        {
            var capture = await _screenshotService.CaptureVirtualDesktopAsync(returnMode, cancellationToken)
                .ConfigureAwait(false);

            var data = new Dictionary<string, object?>
            {
                ["target"] = "virtual_desktop",
                ["format"] = "png",
                ["return"] = capture.ReturnMode,
                ["byteLength"] = capture.ByteLength,
                ["fallbackToPath"] = capture.FallbackToPath,
                ["path"] = capture.Path,
                ["base64"] = capture.Base64
            };

            return BridgeResponse.Success(request.Id, data);
        }
        catch (CaptureDisabledException)
        {
            return Failure(request.Id, "capture_disabled", "Capture is disabled in settings.");
        }
        catch (CaptureRateLimitedException ex)
        {
            return Failure(
                request.Id,
                "rate_limited",
                "Capture is rate-limited.",
                new Dictionary<string, object?>
                {
                    ["retryAfterMs"] = ex.RetryAfterMs
                });
        }
    }

    private BridgeResponse HandleWindowNudge(BridgeRequest request)
    {
        var target = GetString(request.Args, "target") ?? "active";
        if (!string.Equals(target, "active", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(request.Id, "invalid_request", "window.nudge args.target must be 'active'.");
        }

        if (!TryGetInt32(request.Args, "dx", out var dx) || !TryGetInt32(request.Args, "dy", out var dy))
        {
            return Failure(request.Id, "invalid_request", "window.nudge requires integer args.dx and args.dy.");
        }

        var animate = GetBoolean(request.Args, "animate") ?? true;

        if (!_mischiefGate.CanExecuteMischief(out var gateDeniedReason))
        {
            return Failure(request.Id, "action_denied", gateDeniedReason ?? "Denied by MischiefGate.");
        }

        var settings = _getSettings();
        var policy = _allowList.EvaluateForegroundForMischief(settings);
        if (!policy.Allowed)
        {
            return Failure(
                request.Id,
                "not_allowed_process",
                policy.Reason,
                new Dictionary<string, object?>
                {
                    ["foregroundProcess"] = policy.ProcessName
                });
        }

        if (!_windowActions.TryNudgeActiveWindow(dx, dy, out var result, out var error))
        {
            return Failure(request.Id, "window_action_failed", error ?? "window.nudge failed.");
        }

        if (animate)
        {
            _queuePushAnimation();
        }

        var data = new Dictionary<string, object?>
        {
            ["target"] = "active",
            ["process"] = result!.ProcessName,
            ["hwnd"] = result.Hwnd,
            ["previousX"] = result.PreviousX,
            ["previousY"] = result.PreviousY,
            ["newX"] = result.NewX,
            ["newY"] = result.NewY,
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["animate"] = animate
        };

        return BridgeResponse.Success(request.Id, data);
    }

    private BridgeResponse Failure(string id, string code, string message, object? details = null)
    {
        _setLastError(message);
        return BridgeResponse.Failure(id, code, message, details);
    }

    private static string? GetString(JsonElement args, string property)
    {
        if (!TryGetProperty(args, property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static bool? GetBoolean(JsonElement args, string property)
    {
        if (!TryGetProperty(args, property, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return element.GetBoolean();
    }

    private static bool TryGetInt32(JsonElement args, string property, out int value)
    {
        value = 0;
        if (!TryGetProperty(args, property, out var element))
        {
            return false;
        }

        return element.TryGetInt32(out value);
    }

    private static bool TryGetProperty(JsonElement args, string property, out JsonElement element)
    {
        element = default;
        if (args.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var candidate in args.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                element = candidate.Value;
                return true;
            }
        }

        return false;
    }
}
