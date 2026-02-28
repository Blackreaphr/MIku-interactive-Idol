using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Companion.App.Services;
using Companion.Core;
using Companion.Native;

namespace Companion.App;

public partial class App : System.Windows.Application
{
    private readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Companion");

    private CompanionSettings _settings = new();
    private JsonSettingsStore<CompanionSettings>? _settingsStore;
    private StructuredFileLogger? _logger;
    private MischiefGate? _gate;
    private TimeGateLimiter? _captureLimiter;
    private IScreenshotService? _screenshotService;
    private IWindowQueryService? _windowQueryService;
    private IWindowActionService? _windowActionService;
    private OverlayWindow? _overlay;
    private TrayController? _trayController;
    private PipeControlServer? _pipeServer;
    private HotkeyManager? _hotkeyManager;
    private HwndSource? _hotkeySource;
    private SettingsWindow? _settingsWindow;
    private string _token = string.Empty;
    private string _pipeName = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            InitializeRuntime();
        }
        catch (Exception ex)
        {
            _logger?.Error("startup_failed", ex);
            System.Windows.MessageBox.Show(
                $"Companion failed to start:{Environment.NewLine}{ex.Message}",
                "Companion Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _pipeServer?.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown exceptions.
        }

        try
        {
            if (_hotkeySource is not null)
            {
                _hotkeySource.RemoveHook(HotkeyHook);
            }
        }
        catch
        {
            // Ignore shutdown exceptions.
        }

        _hotkeyManager?.Dispose();
        _trayController?.Dispose();

        if (_overlay is not null)
        {
            _overlay.AllowClose();
            _overlay.Close();
        }

        _logger?.Info("shutdown");
        base.OnExit(e);
    }

    private void InitializeRuntime()
    {
        Directory.CreateDirectory(_basePath);

        _logger = new StructuredFileLogger(Path.Combine(_basePath, "logs"), retentionDays: 7);
        _logger.Info("startup_begin");

        _settingsStore = new JsonSettingsStore<CompanionSettings>(Path.Combine(_basePath, "settings.json"));
        _settings = _settingsStore.LoadOrCreate();
        if (!_settings.TryValidate(out var validationError))
        {
            _logger.Warn(
                "settings_invalid_reset",
                new Dictionary<string, object?> { ["reason"] = validationError });
            _settings = new CompanionSettings();
            _settingsStore.Save(_settings);
        }

        var tokenStore = new SecretTokenStore(Path.Combine(_basePath, "token.txt"));
        _token = tokenStore.LoadOrCreate();

        _gate = new MischiefGate();
        _gate.Changed += OnGateChanged;
        _gate.SetEnabled(
            _settings.MischiefEnabled,
            _settings.MischiefEnabled ? TimeSpan.FromMinutes(_settings.MischiefAutoOffMinutes) : null);

        _captureLimiter = new TimeGateLimiter(TimeSpan.FromMilliseconds(_settings.CaptureMinIntervalMs));
        _screenshotService = new CopyFromScreenScreenshotService(
            _captureLimiter,
            () => _settings.CaptureEnabled);

        _overlay = new OverlayWindow(_gate);
        _overlay.SourceInitialized += OverlayOnSourceInitialized;
        _overlay.Show();

        _windowQueryService = new WindowQueryService(GetOverlayHwnd);
        _windowActionService = new WindowActionService(
            _gate,
            getAllowedProcesses: () => _settings.AllowedProcessesForMischief,
            getOverlayHwnd: GetOverlayHwnd);

        _hotkeyManager = new HotkeyManager();
        _hotkeyManager.HotkeyPressed += (_, _) => ForceOff("hotkey");

        _trayController = new TrayController(
            isOverlayVisible: () => _overlay?.IsVisible == true,
            toggleOverlay: ToggleOverlayVisibility,
            isMischiefEnabled: () => _gate?.Enabled == true,
            setMischiefEnabled: enabled => SetMischiefEnabled(enabled, "tray"),
            captureAsync: CaptureFromTrayAsync,
            openSettings: OpenSettingsWindow,
            forceOff: () => ForceOff("tray_force_off"),
            exit: ShutdownFromTray);
        _trayController.SetMischiefState(_settings.MischiefEnabled);
        _trayController.SetOverlayVisibility(_overlay.IsVisible);

        _pipeName = BuildPipeName();
        _pipeServer = new PipeControlServer(_pipeName, _token, HandlePipeRequestAsync, _logger);
        _pipeServer.Start();

        _logger.Info("startup_complete", new Dictionary<string, object?> { ["pipeName"] = _pipeName });
    }

    private void OverlayOnSourceInitialized(object? sender, EventArgs e)
    {
        if (_overlay is null || _hotkeyManager is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_overlay).Handle;
        _hotkeySource = HwndSource.FromHwnd(hwnd);
        _hotkeySource?.AddHook(HotkeyHook);

        RegisterHotkey(_settings.Hotkey);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkeyManager is null)
        {
            return IntPtr.Zero;
        }

        return _hotkeyManager.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void RegisterHotkey(HotkeySettings settings)
    {
        if (_hotkeyManager is null || _overlay is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_overlay).Handle;
        if (_hotkeyManager.TryRegister(hwnd, settings, out var win32Error))
        {
            _logger?.Info(
                "hotkey_registered",
                new Dictionary<string, object?>
                {
                    ["control"] = settings.Control,
                    ["alt"] = settings.Alt,
                    ["shift"] = settings.Shift,
                    ["virtualKey"] = settings.VirtualKey
                });
            return;
        }

        _logger?.Warn(
            "hotkey_register_failed",
            new Dictionary<string, object?> { ["win32Error"] = win32Error });
        _trayController?.ShowNotification(
            "Companion",
            "Global kill hotkey could not be registered.",
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var enabled = _gate?.Enabled == true;
            _settings.MischiefEnabled = enabled;
            SaveSettings();
            _trayController?.SetMischiefState(enabled);
            _logger?.Info(
                "mischief_state_changed",
                new Dictionary<string, object?> { ["enabled"] = enabled });
        });
    }

    private void SetMischiefEnabled(bool enabled, string source, int? autoOffMinutes = null)
    {
        if (_gate is null)
        {
            return;
        }

        if (autoOffMinutes.HasValue)
        {
            _settings.MischiefAutoOffMinutes = autoOffMinutes.Value;
        }

        var effectiveAutoOff = _settings.MischiefAutoOffMinutes;
        _settings.MischiefEnabled = enabled;
        _gate.SetEnabled(
            enabled,
            enabled ? TimeSpan.FromMinutes(effectiveAutoOff) : null);
        SaveSettings();

        _logger?.Info(
            "mischief_set",
            new Dictionary<string, object?>
            {
                ["enabled"] = enabled,
                ["source"] = source,
                ["autoOffMinutes"] = effectiveAutoOff
            });
    }

    private void ForceOff(string source)
    {
        Dispatcher.Invoke(() =>
        {
            SetMischiefEnabled(false, source);
            _trayController?.ShowNotification("Companion", "Mischief disabled.");
        });

        _logger?.Warn("force_off", new Dictionary<string, object?> { ["source"] = source });
    }

    private void ToggleOverlayVisibility()
    {
        Dispatcher.Invoke(() =>
        {
            if (_overlay is null)
            {
                return;
            }

            if (_overlay.IsVisible)
            {
                _overlay.Hide();
            }
            else
            {
                _overlay.Show();
            }

            _trayController?.SetOverlayVisibility(_overlay.IsVisible);
        });
    }

    private void OpenSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings.Clone())
            {
                Owner = _overlay
            };

            var result = _settingsWindow.ShowDialog();
            if (result == true && _settingsWindow.UpdatedSettings is not null)
            {
                if (!ApplySettings(_settingsWindow.UpdatedSettings, "settings_ui", out var error))
                {
                    System.Windows.MessageBox.Show(
                        error ?? "Invalid settings.",
                        "Companion Settings",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            _settingsWindow = null;
        });
    }

    private bool ApplySettings(CompanionSettings nextSettings, string source, out string? error)
    {
        if (!nextSettings.TryValidate(out error))
        {
            return false;
        }

        _settings = nextSettings.Clone();
        _captureLimiter?.SetMinInterval(TimeSpan.FromMilliseconds(_settings.CaptureMinIntervalMs));
        RegisterHotkey(_settings.Hotkey);
        SetMischiefEnabled(_settings.MischiefEnabled, source);
        SaveSettings();

        _logger?.Info("settings_applied", new Dictionary<string, object?> { ["source"] = source });
        return true;
    }

    private async Task CaptureFromTrayAsync()
    {
        try
        {
            var path = await CaptureAndStoreAsync(CancellationToken.None);
            _trayController?.ShowNotification(
                "Companion",
                $"Screenshot saved: {Path.GetFileName(path)}");
        }
        catch (CaptureDisabledException)
        {
            _trayController?.ShowNotification(
                "Companion",
                "Capture is disabled in settings.",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (CaptureRateLimitedException ex)
        {
            _trayController?.ShowNotification(
                "Companion",
                $"Capture rate-limited. Retry in {ex.RetryAfter.TotalMilliseconds:0} ms.",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _logger?.Error("capture_tray_failed", ex);
            _trayController?.ShowNotification(
                "Companion",
                "Capture failed.",
                System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private async Task<string> CaptureAndStoreAsync(CancellationToken ct)
    {
        var bytes = await CapturePngAsync(ct);
        var capturesPath = Path.Combine(_basePath, "captures");
        Directory.CreateDirectory(capturesPath);

        var filePath = Path.Combine(capturesPath, $"capture-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
        await File.WriteAllBytesAsync(filePath, bytes, ct);

        _logger?.Info(
            "capture_saved",
            new Dictionary<string, object?>
            {
                ["path"] = filePath,
                ["byteLength"] = bytes.Length
            });

        return filePath;
    }

    private async Task<byte[]> CapturePngAsync(CancellationToken ct)
    {
        if (_screenshotService is null)
        {
            throw new InvalidOperationException("Screenshot service unavailable.");
        }

        return await _screenshotService.CapturePngAsync(CaptureTarget.VirtualDesktop, ct);
    }

    private async Task<PipeControlResponse> HandlePipeRequestAsync(PipeControlRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Method)
            {
                case "ping":
                    return PipeControlResponse.Success(
                        request.Id,
                        new Dictionary<string, object?>
                        {
                            ["pong"] = true,
                            ["utcNow"] = DateTimeOffset.UtcNow
                        });

                case "get_status":
                {
                    var status = await Dispatcher.InvokeAsync(BuildStatus).Task;
                    return PipeControlResponse.Success(request.Id, status);
                }

                case "set_mischief_enabled":
                {
                    if (!TryGetBoolean(request.Params, "enabled", out var enabled))
                    {
                        return PipeControlResponse.Failure(
                            request.Id,
                            "invalid_request",
                            "params.enabled must be a boolean.");
                    }

                    int? autoOff = null;
                    if (TryGetProperty(request.Params, "autoOffMinutes", out var autoOffElement))
                    {
                        if (!autoOffElement.TryGetInt32(out var parsedAutoOff))
                        {
                            return PipeControlResponse.Failure(
                                request.Id,
                                "invalid_request",
                                "params.autoOffMinutes must be an integer.");
                        }

                        autoOff = parsedAutoOff;
                    }

                    var response = await Dispatcher.InvokeAsync(() =>
                    {
                        if (autoOff.HasValue && autoOff.Value is < 1 or > 1440)
                        {
                            return PipeControlResponse.Failure(
                                request.Id,
                                "invalid_request",
                                "autoOffMinutes must be in range 1..1440.");
                        }

                        SetMischiefEnabled(enabled, "pipe_set_mischief", autoOff);
                        return PipeControlResponse.Success(request.Id, BuildStatus());
                    }).Task;

                    return response;
                }

                case "force_off":
                {
                    await Dispatcher.InvokeAsync(() => ForceOff("pipe_force_off")).Task;
                    return PipeControlResponse.Success(request.Id, BuildStatus());
                }

                case "capture_virtual_desktop_png":
                {
                    try
                    {
                        var bytes = await CapturePngAsync(ct);
                        return PipeControlResponse.Success(
                            request.Id,
                            new Dictionary<string, object?>
                            {
                                ["imageBase64"] = Convert.ToBase64String(bytes)
                            });
                    }
                    catch (CaptureDisabledException)
                    {
                        return PipeControlResponse.Failure(
                            request.Id,
                            "capture_disabled",
                            "Capture is disabled.");
                    }
                    catch (CaptureRateLimitedException ex)
                    {
                        return PipeControlResponse.Failure(
                            request.Id,
                            "rate_limited",
                            "Capture is rate-limited.",
                            new Dictionary<string, object?>
                            {
                                ["retryAfterMs"] = Math.Ceiling(ex.RetryAfter.TotalMilliseconds)
                            });
                    }
                }

                case "windows.list":
                    return HandleWindowsList(request);

                case "windows.active":
                    return HandleActiveWindow(request);

                case "windows.under_cursor":
                    return HandleWindowUnderCursor(request);

                case "window.focus":
                    return HandleWindowFocus(request);

                case "window.move_resize":
                    return HandleWindowMoveResize(request);

                case "window.nudge":
                    return HandleWindowNudge(request);

                case "window.minimize":
                    return HandleWindowShowCommand(request, "minimize", static (IWindowActionService svc, long hwnd, out string? err) => svc.Minimize(hwnd, out err));

                case "window.maximize":
                    return HandleWindowShowCommand(request, "maximize", static (IWindowActionService svc, long hwnd, out string? err) => svc.Maximize(hwnd, out err));

                case "window.restore":
                    return HandleWindowShowCommand(request, "restore", static (IWindowActionService svc, long hwnd, out string? err) => svc.Restore(hwnd, out err));

                case "window.close_request":
                    return HandleWindowShowCommand(request, "close_request", static (IWindowActionService svc, long hwnd, out string? err) => svc.CloseRequest(hwnd, out err));

                case "get_settings":
                    return PipeControlResponse.Success(request.Id, _settings.Clone());

                case "update_settings":
                {
                    var response = await Dispatcher.InvokeAsync(() =>
                    {
                        var updated = _settings.Clone();
                        if (!ApplyPartialSettings(updated, request.Params, out var parseError))
                        {
                            return PipeControlResponse.Failure(
                                request.Id,
                                "invalid_request",
                                parseError ?? "Unable to parse settings update.");
                        }

                        if (!ApplySettings(updated, "pipe_update_settings", out var validationError))
                        {
                            return PipeControlResponse.Failure(
                                request.Id,
                                "invalid_request",
                                validationError ?? "Invalid settings update.");
                        }

                        return PipeControlResponse.Success(request.Id, _settings.Clone());
                    }).Task;

                    return response;
                }

                default:
                    return PipeControlResponse.Failure(
                        request.Id,
                        "unknown_method",
                        $"Unknown method '{request.Method}'.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("pipe_method_failed", ex, new Dictionary<string, object?> { ["method"] = request.Method });
            return PipeControlResponse.Failure(
                request.Id,
                "internal_error",
                "Internal server error.");
        }
    }

    private PipeControlResponse HandleWindowsList(PipeControlRequest request)
    {
        if (_windowQueryService is null)
        {
            return PipeControlResponse.Failure(request.Id, "internal_error", "Window query service unavailable.");
        }

        var windows = _windowQueryService.ListTopLevelWindows();
        _logger?.Info(
            "window_query",
            new Dictionary<string, object?>
            {
                ["query"] = "list",
                ["count"] = windows.Count
            });

        return PipeControlResponse.Success(
            request.Id,
            new Dictionary<string, object?>
            {
                ["windows"] = windows
            });
    }

    private PipeControlResponse HandleActiveWindow(PipeControlRequest request)
    {
        if (_windowQueryService is null)
        {
            return PipeControlResponse.Failure(request.Id, "internal_error", "Window query service unavailable.");
        }

        var window = _windowQueryService.GetActiveWindow();
        _logger?.Info(
            "window_query",
            new Dictionary<string, object?>
            {
                ["query"] = "active",
                ["hwnd"] = window?.Hwnd
            });

        return PipeControlResponse.Success(
            request.Id,
            new Dictionary<string, object?>
            {
                ["window"] = window
            });
    }

    private PipeControlResponse HandleWindowUnderCursor(PipeControlRequest request)
    {
        if (_windowQueryService is null)
        {
            return PipeControlResponse.Failure(request.Id, "internal_error", "Window query service unavailable.");
        }

        var window = _windowQueryService.GetWindowUnderCursor();
        _logger?.Info(
            "window_query",
            new Dictionary<string, object?>
            {
                ["query"] = "under_cursor",
                ["hwnd"] = window?.Hwnd
            });

        return PipeControlResponse.Success(
            request.Id,
            new Dictionary<string, object?>
            {
                ["window"] = window
            });
    }

    private PipeControlResponse HandleWindowFocus(PipeControlRequest request)
    {
        if (!WindowPipeParsing.TryGetInt64Parameter(request.Params, "hwnd", out var hwnd))
        {
            return PipeControlResponse.Failure(request.Id, "invalid_request", "params.hwnd must be an integer.");
        }

        return ExecuteWindowAction(
            request,
            action: "focus",
            hwnd,
            includeWindowInResult: false,
            execute: static (IWindowActionService service, long handle, out string? error) => service.Focus(handle, out error));
    }

    private PipeControlResponse HandleWindowMoveResize(PipeControlRequest request)
    {
        if (!WindowPipeParsing.TryGetInt64Parameter(request.Params, "hwnd", out var hwnd))
        {
            return PipeControlResponse.Failure(request.Id, "invalid_request", "params.hwnd must be an integer.");
        }

        if (!WindowPipeParsing.TryGetInt32Parameter(request.Params, "x", out var x) ||
            !WindowPipeParsing.TryGetInt32Parameter(request.Params, "y", out var y) ||
            !WindowPipeParsing.TryGetInt32Parameter(request.Params, "width", out var width) ||
            !WindowPipeParsing.TryGetInt32Parameter(request.Params, "height", out var height))
        {
            return PipeControlResponse.Failure(
                request.Id,
                "invalid_request",
                "params.x, params.y, params.width, and params.height must be integers.");
        }

        return ExecuteWindowAction(
            request,
            action: "move_resize",
            hwnd,
            includeWindowInResult: true,
            execute: (IWindowActionService service, long handle, out string? error) => service.MoveResize(handle, x, y, width, height, out error));
    }

    private PipeControlResponse HandleWindowNudge(PipeControlRequest request)
    {
        if (!WindowPipeParsing.TryGetInt64Parameter(request.Params, "hwnd", out var hwnd))
        {
            return PipeControlResponse.Failure(request.Id, "invalid_request", "params.hwnd must be an integer.");
        }

        if (!WindowPipeParsing.TryGetInt32Parameter(request.Params, "dx", out var dx) ||
            !WindowPipeParsing.TryGetInt32Parameter(request.Params, "dy", out var dy))
        {
            return PipeControlResponse.Failure(
                request.Id,
                "invalid_request",
                "params.dx and params.dy must be integers.");
        }

        return ExecuteWindowAction(
            request,
            action: "nudge",
            hwnd,
            includeWindowInResult: true,
            execute: (IWindowActionService service, long handle, out string? error) => service.Nudge(handle, dx, dy, out error));
    }

    private PipeControlResponse HandleWindowShowCommand(
        PipeControlRequest request,
        string action,
        WindowActionExecutor execute)
    {
        if (!WindowPipeParsing.TryGetInt64Parameter(request.Params, "hwnd", out var hwnd))
        {
            return PipeControlResponse.Failure(request.Id, "invalid_request", "params.hwnd must be an integer.");
        }

        return ExecuteWindowAction(
            request,
            action,
            hwnd,
            includeWindowInResult: true,
            execute);
    }

    private PipeControlResponse ExecuteWindowAction(
        PipeControlRequest request,
        string action,
        long hwnd,
        bool includeWindowInResult,
        WindowActionExecutor execute)
    {
        if (_windowActionService is null)
        {
            return PipeControlResponse.Failure(request.Id, "internal_error", "Window action service unavailable.");
        }

        _logger?.Info(
            "window_action_attempt",
            new Dictionary<string, object?>
            {
                ["action"] = action,
                ["hwnd"] = hwnd
            });

        if (!execute(_windowActionService, hwnd, out var error))
        {
            var code = WindowPipeParsing.MapWindowActionErrorCode(error);
            var eventName = code is "action_denied" or "not_allowed_process"
                ? "window_action_denied"
                : "window_action_failed";

            _logger?.Warn(
                eventName,
                new Dictionary<string, object?>
                {
                    ["action"] = action,
                    ["hwnd"] = hwnd,
                    ["code"] = code,
                    ["error"] = error
                });

            return PipeControlResponse.Failure(
                request.Id,
                code,
                error ?? "Window action failed.");
        }

        var result = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["hwnd"] = hwnd
        };

        if (includeWindowInResult)
        {
            var refreshed = GetWindowByHandle(hwnd);
            if (refreshed is not null)
            {
                result["window"] = refreshed;
            }
        }

        _logger?.Info(
            "window_action_success",
            new Dictionary<string, object?>
            {
                ["action"] = action,
                ["hwnd"] = hwnd
            });

        return PipeControlResponse.Success(request.Id, result);
    }

    private WindowInfo? GetWindowByHandle(long hwnd)
    {
        if (_windowQueryService is WindowQueryService concrete)
        {
            return concrete.GetWindowByHandle(hwnd);
        }

        return null;
    }

    private delegate bool WindowActionExecutor(IWindowActionService service, long hwnd, out string? error);

    private object BuildStatus()
    {
        return new Dictionary<string, object?>
        {
            ["mischiefEnabled"] = _gate?.Enabled == true,
            ["captureEnabled"] = _settings.CaptureEnabled,
            ["captureMinIntervalMs"] = _settings.CaptureMinIntervalMs,
            ["allowedProcessesForMischief"] = _settings.AllowedProcessesForMischief,
            ["overlayVisible"] = _overlay?.IsVisible == true,
            ["pipeName"] = _pipeName
        };
    }

    private bool ApplyPartialSettings(CompanionSettings settings, JsonElement parameters, out string? error)
    {
        error = null;

        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "params object is required.";
            return false;
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = "params must be an object.";
            return false;
        }

        if (TryGetProperty(parameters, "mischiefEnabled", out var mischiefEnabledElement))
        {
            if (mischiefEnabledElement.ValueKind != JsonValueKind.True &&
                mischiefEnabledElement.ValueKind != JsonValueKind.False)
            {
                error = "mischiefEnabled must be a boolean.";
                return false;
            }

            settings.MischiefEnabled = mischiefEnabledElement.GetBoolean();
        }

        if (TryGetProperty(parameters, "mischiefAutoOffMinutes", out var autoOffElement))
        {
            if (!autoOffElement.TryGetInt32(out var autoOff))
            {
                error = "mischiefAutoOffMinutes must be an integer.";
                return false;
            }

            settings.MischiefAutoOffMinutes = autoOff;
        }

        if (TryGetProperty(parameters, "captureEnabled", out var captureEnabledElement))
        {
            if (captureEnabledElement.ValueKind != JsonValueKind.True &&
                captureEnabledElement.ValueKind != JsonValueKind.False)
            {
                error = "captureEnabled must be a boolean.";
                return false;
            }

            settings.CaptureEnabled = captureEnabledElement.GetBoolean();
        }

        if (TryGetProperty(parameters, "captureMinIntervalMs", out var intervalElement))
        {
            if (!intervalElement.TryGetInt32(out var interval))
            {
                error = "captureMinIntervalMs must be an integer.";
                return false;
            }

            settings.CaptureMinIntervalMs = interval;
        }

        if (TryGetProperty(parameters, "allowedProcessesForMischief", out var allowlistElement))
        {
            if (allowlistElement.ValueKind != JsonValueKind.Array)
            {
                error = "allowedProcessesForMischief must be an array of strings.";
                return false;
            }

            var entries = new List<string>();
            foreach (var item in allowlistElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = "allowedProcessesForMischief must contain only string values.";
                    return false;
                }

                entries.Add(item.GetString() ?? string.Empty);
            }

            settings.AllowedProcessesForMischief = entries;
        }

        if (TryGetProperty(parameters, "hotkey", out var hotkeyElement))
        {
            if (hotkeyElement.ValueKind != JsonValueKind.Object)
            {
                error = "hotkey must be an object.";
                return false;
            }

            var currentHotkey = settings.Hotkey.Clone();

            if (TryGetProperty(hotkeyElement, "control", out var controlElement))
            {
                if (controlElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "hotkey.control must be a boolean.";
                    return false;
                }

                currentHotkey.Control = controlElement.GetBoolean();
            }

            if (TryGetProperty(hotkeyElement, "alt", out var altElement))
            {
                if (altElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "hotkey.alt must be a boolean.";
                    return false;
                }

                currentHotkey.Alt = altElement.GetBoolean();
            }

            if (TryGetProperty(hotkeyElement, "shift", out var shiftElement))
            {
                if (shiftElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "hotkey.shift must be a boolean.";
                    return false;
                }

                currentHotkey.Shift = shiftElement.GetBoolean();
            }

            if (TryGetProperty(hotkeyElement, "virtualKey", out var keyElement))
            {
                if (!keyElement.TryGetInt32(out var vk))
                {
                    error = "hotkey.virtualKey must be an integer.";
                    return false;
                }

                currentHotkey.VirtualKey = vk;
            }

            settings.Hotkey = currentHotkey;
        }

        return true;
    }

    private void SaveSettings()
    {
        _settingsStore?.Save(_settings);
    }

    private IntPtr GetOverlayHwnd()
    {
        if (_overlay is null)
        {
            return IntPtr.Zero;
        }

        return new WindowInteropHelper(_overlay).Handle;
    }

    private void ShutdownFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Shutdown();
        });
    }

    private static bool TryGetBoolean(JsonElement container, string property, out bool value)
    {
        value = false;
        if (!TryGetProperty(container, property, out var element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetProperty(JsonElement container, string property, out JsonElement element)
    {
        element = default;
        if (container.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var child in container.EnumerateObject())
        {
            if (string.Equals(child.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                element = child.Value;
                return true;
            }
        }

        return false;
    }

    private static string BuildPipeName()
    {
        var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? Environment.UserName;
        var safe = new string(
            sid.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_').ToArray());
        return $"companion.control.v1.{safe}";
    }
}
