using System.Collections.Concurrent;
using MikuAgentBridge.Actions;
using MikuAgentBridge.Character;
using MikuAgentBridge.Config;
using MikuAgentBridge.IPC;
using MikuAgentBridge.Security;
using MelonLoader;

[assembly: MelonInfo(typeof(MikuAgentBridge.MikuAgentBridgeMod), "MikuAgentBridge", "0.1.0", "Codex")]

namespace MikuAgentBridge;

public sealed class MikuAgentBridgeMod : MelonMod
{
    private const string PipeName = "miku_agent_bridge_v1";

    private readonly object _settingsSync = new();
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

    private readonly string _roamingBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MikuAgentBridge");

    private readonly string _localBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MikuAgentBridge");

    private SettingsStore? _settingsStore;
    private Settings _settings = new();

    private TokenStore? _tokenStore;
    private string _token = string.Empty;

    private readonly MischiefGate _mischiefGate = new();
    private readonly AllowList _allowList = new();
    private KillHotkey? _killHotkey;

    private ScreenshotService? _screenshotService;
    private readonly WindowActions _windowActions = new();

    private readonly CharacterLocator _characterLocator = new();
    private readonly AnimationDriver _animationDriver = new();

    private CommandRouter? _commandRouter;
    private PipeServer? _pipeServer;

    private string? _lastRoutedError;

    public override void OnInitializeMelon()
    {
        Directory.CreateDirectory(_roamingBasePath);
        Directory.CreateDirectory(_localBasePath);
        Directory.CreateDirectory(CapturesPath);

        _settingsStore = new SettingsStore(Path.Combine(_roamingBasePath, "settings.json"));
        _settings = _settingsStore.LoadOrCreate();
        if (!_settings.TryValidateAndNormalize(out var settingsError))
        {
            MelonLogger.Warning($"settings.json invalid: {settingsError}. Resetting to defaults.");
            _settings = new Settings();
            _ = _settings.TryValidateAndNormalize(out _);
            _settingsStore.Save(_settings);
        }

        _tokenStore = new TokenStore(Path.Combine(_roamingBasePath, "secret.token"));
        _token = _tokenStore.LoadOrCreate();

        _mischiefGate.StateChanged += OnMischiefGateChanged;
        _mischiefGate.SetEnabled(_settings.MischiefEnabled, _settings.MischiefAutoOffMinutes);

        _screenshotService = new ScreenshotService(GetSettingsSnapshot, CapturesPath);

        _commandRouter = new CommandRouter(
            getSettings: GetSettingsSnapshot,
            mischiefGate: _mischiefGate,
            allowList: _allowList,
            screenshotService: _screenshotService,
            windowActions: _windowActions,
            getCharacter: GetCharacterSnapshot,
            queuePushAnimation: QueuePushAnimation,
            getLastError: GetLastRoutedError,
            setLastError: SetLastRoutedError,
            modVersion: "0.1.0");

        _pipeServer = new PipeServer(
            pipeName: PipeName,
            expectedToken: _token,
            router: _commandRouter,
            logInfo: message => MelonLogger.Msg(message),
            logWarn: message => MelonLogger.Warning(message),
            logError: (ex, context) => MelonLogger.Error($"{context}: {ex}"));

        _pipeServer.Start();

        _killHotkey = new KillHotkey(
            onHotkeyPressed: () => ForceOff("kill_hotkey"),
            log: message => MelonLogger.Warning(message));

        var registered = _killHotkey.Start(_settings.KillHotkey);
        if (!registered)
        {
            MelonLogger.Warning("Kill hotkey registration failed.");
        }

        MelonLogger.Msg("MikuAgentBridge initialized.");
        MelonLogger.Msg($"Named pipe: {PipeName}");
        MelonLogger.Msg($"Settings: {Path.Combine(_roamingBasePath, "settings.json")}");
        MelonLogger.Msg($"Token: {Path.Combine(_roamingBasePath, "secret.token")}");
        MelonLogger.Msg($"Captures: {CapturesPath}");
    }

    public override void OnUpdate()
    {
        DrainMainThreadQueue();
        _characterLocator.Tick();

        // Keep auto-off timing active even when no mischief command is received.
        _ = _mischiefGate.GetAutoOffRemainingSeconds();
    }

    public override void OnDeinitializeMelon()
    {
        _mischiefGate.StateChanged -= OnMischiefGateChanged;

        _killHotkey?.Dispose();
        _killHotkey = null;

        if (_pipeServer is not null)
        {
            try
            {
                _pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"pipe shutdown failed: {ex}");
            }
        }

        _pipeServer = null;
        _commandRouter = null;
    }

    private string CapturesPath => Path.Combine(_localBasePath, "captures");

    private void ForceOff(string source)
    {
        _mischiefGate.ForceOff();
        SetLastRoutedError($"Mischief disabled by {source}.");
        MelonLogger.Warning($"Mischief force-off triggered by {source}.");
    }

    private void QueuePushAnimation()
    {
        _mainThreadQueue.Enqueue(() =>
        {
            var animator = _characterLocator.CurrentAnimator;
            _ = _animationDriver.TryTriggerPush(animator);
        });
    }

    private void DrainMainThreadQueue()
    {
        while (_mainThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"main-thread action failed: {ex}");
            }
        }
    }

    private void OnMischiefGateChanged(object? sender, EventArgs e)
    {
        lock (_settingsSync)
        {
            var current = _mischiefGate.Enabled;
            if (_settings.MischiefEnabled == current)
            {
                return;
            }

            _settings.MischiefEnabled = current;
            PersistSettings_NoLock();
        }
    }

    private Settings GetSettingsSnapshot()
    {
        lock (_settingsSync)
        {
            return _settings.Clone();
        }
    }

    private (bool Found, string? CharacterName) GetCharacterSnapshot()
    {
        return (_characterLocator.CharacterFound, _characterLocator.CharacterName);
    }

    private void PersistSettings_NoLock()
    {
        _settingsStore?.Save(_settings);
    }

    private string? GetLastRoutedError()
    {
        return Volatile.Read(ref _lastRoutedError);
    }

    private void SetLastRoutedError(string? message)
    {
        Interlocked.Exchange(ref _lastRoutedError, message);
    }
}
