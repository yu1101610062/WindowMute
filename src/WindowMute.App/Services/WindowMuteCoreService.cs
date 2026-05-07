using WindowMute.App.Core;
using WindowMute.App.Models;

namespace WindowMute.App.Services;

internal sealed class WindowMuteCoreService : IDisposable
{
    private readonly object _stateLock = new();
    private readonly nint _hwnd;
    private readonly CoreConfigurationService _configService = new();
    private readonly WindowService _windows = new();
    private readonly Queue<string> _messages = new();
    private readonly Dictionary<string, bool> _autoBaseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly AudioSessionService _audio;
    private readonly SelectionService _selection;
    private HotkeyService? _hotkeys;
    private AutoMuteService? _autoMute;
    private CoreConfig _config;
    private AppInfoDto? _lastTargetApp;
    private bool _selectionActive;
    private SelectionAction _selectionAction = SelectionAction.ToggleMute;
    private long _version = 1;
    private bool _started;
    private bool _disposed;

    public WindowMuteCoreService(nint hwnd)
    {
        _hwnd = hwnd;
        _config = _configService.Load();
        _audio = new AudioSessionService(_windows);
        _selection = new SelectionService(
            _windows,
            action => IsSelectionActive(action),
            CancelSelection,
            FailSelection,
            TimeoutSelection,
            CompleteSelection);
    }

    public event EventHandler? StateChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _configService.AppendDiagnostic("core start");
        RemoveSelfFromManualMute();
        _hotkeys = new HotkeyService(_hwnd, OnHotkeyToggleForeground);
        RegisterConfiguredHotkeys();
        _autoMute = new AutoMuteService(OnForegroundChanged);
        _autoMute.Start();
        PushMessage("WindowMute core started.");
        MarkChanged();
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _selection.Dispose();
        _autoMute?.Dispose();
        _hotkeys?.Dispose();
    }

    public void Dispose() => Stop();

    public ulong GetStateVersion() => unchecked((ulong)Interlocked.Read(ref _version));

    public SnapshotDto GetSnapshot() => ApplyRulesAndSnapshot(drainMessages: true);

    public SnapshotDto Refresh() => ApplyRulesAndSnapshot(drainMessages: true);

    public void ToggleForegroundMute()
    {
        var app = ToggleTargetApp() ?? throw new InvalidOperationException("no foreground window");
        _configService.AppendDiagnostic($"hotkey target process={app.ProcessName} key={app.Key} pid={app.Pid} title={app.Title}");
        ToggleAppMute(app);
    }

    public void ToggleAutoMute(bool? enabled = null)
    {
        lock (_stateLock)
        {
            _config.AutoEnabled = enabled ?? !_config.AutoEnabled;
            _configService.Save(_config);
            PushMessage(_config.AutoEnabled ? "Auto mute enabled." : "Auto mute disabled.");
        }

        MarkChanged();
    }

    public void SetHotkey(string action, string hotkey)
    {
        var parsed = HotkeyConfig.Parse(hotkey);
        lock (_stateLock)
        {
            if (action is not ("toggle_foreground" or "toggleForeground"))
            {
                throw new InvalidOperationException($"unknown hotkey action: {action}");
            }

            _config.Hotkeys.ToggleForeground = parsed;
            _configService.Save(_config);
            PushMessage("Hotkey updated.");
        }

        RegisterConfiguredHotkeys();
        MarkChanged();
    }

    public void SetAppMute(string appKey, bool muted)
    {
        var (manualKeys, audioKeys) = AppKeysForQuery(appKey);
        SetAppKeysMute(manualKeys, audioKeys, appKey, muted);
    }

    public void ToggleAppMute(string appKey)
    {
        var (manualKeys, audioKeys) = AppKeysForQuery(appKey);
        bool muted;
        lock (_stateLock)
        {
            muted = !manualKeys.Any(key => RuleHelpers.IsManualMuted(_config, key));
        }

        SetAppKeysMute(manualKeys, audioKeys, appKey, muted);
    }

    public void SetAppVolume(string appKey, float volume)
    {
        var changed = _audio.SetVolumeForApp(appKey, volume);
        lock (_stateLock)
        {
            PushMessage($"Updated volume for {changed} audio session(s).");
        }

        MarkChanged();
    }

    public void AddWhitelist(string processName)
    {
        AddProcessToWhitelist(processName);
    }

    public void RemoveWhitelist(string processName)
    {
        lock (_stateLock)
        {
            RuleHelpers.RemoveWhitelist(_config, processName);
            _configService.Save(_config);
            PushMessage($"Removed {processName} from whitelist.");
        }

        MarkChanged();
    }

    public void EnterSelectionMode()
    {
        StartSelectionMode(SelectionAction.ToggleMute);
    }

    public void EnterWhitelistSelectionMode()
    {
        StartSelectionMode(SelectionAction.AddWhitelist);
    }

    private SnapshotDto ApplyRulesAndSnapshot(bool drainMessages)
    {
        var foreground = _windows.ForegroundApp();
        IReadOnlyList<AudioSessionRecord> rawSessions;
        try
        {
            rawSessions = _audio.EnumerateSessions();
        }
        catch (Exception error)
        {
            lock (_stateLock)
            {
                PushMessage($"Audio enumeration failed: {error.Message}");
            }
            MarkChanged();
            rawSessions = [];
        }

        lock (_stateLock)
        {
            if (foreground is not null && !_windows.IsCurrentProcessApp(foreground))
            {
                _lastTargetApp = foreground;
            }

            var mutedApps = new List<AppInfoDto>();
            var seenMuted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decorated = new List<SessionInfoDto>();
            var baselineChanged = false;

            foreach (var raw in rawSessions)
            {
                var manual = RuleHelpers.IsManualMutedApp(_config, raw.App);
                var whitelist = RuleHelpers.IsWhitelisted(_config, raw.App);
                var isForeground = foreground?.Key.Equals(raw.App.Key, StringComparison.OrdinalIgnoreCase) ?? false;
                var auto = _config.AutoEnabled && !isForeground && !whitelist && !manual;

                if ((manual || auto) && seenMuted.Add(raw.App.Key))
                {
                    mutedApps.Add(raw.App);
                }

                if (manual)
                {
                    _audio.SetSessionMute(raw, true);
                    baselineChanged |= _autoBaseline.Remove(raw.App.Key);
                }
                else if (auto)
                {
                    if (!_autoBaseline.ContainsKey(raw.App.Key))
                    {
                        _autoBaseline[raw.App.Key] = raw.Muted;
                        baselineChanged = true;
                    }
                    _audio.SetSessionMute(raw, true);
                }
                else if (_autoBaseline.Remove(raw.App.Key, out var previous))
                {
                    baselineChanged = true;
                    _audio.SetSessionMute(raw, previous);
                }

                decorated.Add(new SessionInfoDto
                {
                    SessionId = raw.SessionId,
                    App = raw.App,
                    DisplayName = raw.DisplayName,
                    Volume = raw.Volume,
                    Muted = manual || auto || raw.Muted,
                    ManualMuted = manual,
                    AutoMuted = auto,
                    Whitelisted = whitelist,
                    IsForeground = isForeground,
                    EffectiveReason = EffectiveReason(_config.AutoEnabled, whitelist, manual, auto)
                });
            }

            var snapshot = new SnapshotDto
            {
                Version = GetStateVersion(),
                AutoEnabled = _config.AutoEnabled,
                SelectionActive = _selectionActive,
                SelectionMode = _selectionActive ? SelectionMode(_selectionAction) : null,
                Hotkeys = new HotkeySnapshotDto { ToggleForeground = _config.Hotkeys.ToggleForeground.Display() },
                Foreground = foreground,
                Sessions = decorated,
                Whitelist = [.. _config.Whitelist],
                MutedApps = mutedApps,
                Messages = drainMessages ? DrainMessages() : [.. _messages]
            };

            if (baselineChanged)
            {
                MarkChanged();
                snapshot.Version = GetStateVersion();
            }

            return snapshot;
        }
    }

    private void RefreshState()
    {
        _ = ApplyRulesAndSnapshot(drainMessages: false);
    }

    private void RestoreForegroundAutoMuteFast()
    {
        var app = _windows.ForegroundApp();
        if (app is null)
        {
            return;
        }

        bool? previous = null;
        lock (_stateLock)
        {
            if (_autoBaseline.Remove(app.Key, out var baseline))
            {
                previous = baseline;
            }
        }

        if (previous is bool muted)
        {
            _audio.SetMuteForApp(app.Key, muted);
            MarkChanged();
        }
    }

    private void RemoveSelfFromManualMute()
    {
        var selfKey = RuleHelpers.NormalizeAppKey(_windows.CurrentProcessApp().ProcessName);
        lock (_stateLock)
        {
            var previous = _config.ManualMuted.Count;
            _config.ManualMuted.RemoveAll(entry => entry.Equals(selfKey, StringComparison.OrdinalIgnoreCase));
            if (_config.ManualMuted.Count == previous)
            {
                return;
            }

            _configService.Save(_config);
            PushMessage("Removed WindowMute from manual mute state.");
        }

        MarkChanged();
    }

    private AppInfoDto? ToggleTargetApp()
    {
        var foreground = _windows.ForegroundApp();
        if (foreground is not null && !_windows.IsCurrentProcessApp(foreground))
        {
            return foreground;
        }

        lock (_stateLock)
        {
            return _lastTargetApp;
        }
    }

    private void ToggleAppMute(AppInfoDto app)
    {
        var manualKeys = ManualKeysForApp(app);
        MatchingAppState? sessionState = null;
        try
        {
            sessionState = _audio.MatchingAppState(app);
            foreach (var sessionApp in sessionState.Apps)
            {
                ExtendUniqueKeys(manualKeys, ManualKeysForApp(sessionApp));
            }
        }
        catch (Exception error)
        {
            _configService.AppendDiagnostic($"matching app state failed: {error}");
        }

        bool targetMuted;
        bool manuallyMuted;
        lock (_stateLock)
        {
            manuallyMuted = manualKeys.Any(key => RuleHelpers.IsManualMuted(_config, key));
            targetMuted = manuallyMuted ? false : !(sessionState?.AllMuted ?? false);
        }

        _configService.AppendDiagnostic($"toggle decision process={app.ProcessName} mute={targetMuted} manual_before={manuallyMuted} manual_keys=[{string.Join(",", manualKeys)}]");

        var audioKeys = new List<string>();
        if (sessionState is not null)
        {
            foreach (var sessionApp in sessionState.Apps)
            {
                ExtendUniqueKeys(audioKeys, [sessionApp.Key]);
            }
        }

        var resolvedAudioKeys = audioKeys.Count == 0 ? manualKeys : audioKeys;
        SetManualKeysMute(manualKeys, targetMuted);

        var changed = _audio.SetMuteForTarget(app, targetMuted);
        if (changed == 0)
        {
            changed = resolvedAudioKeys.Sum(appKey => _audio.SetMuteForApp(appKey, targetMuted));
        }

        PushMuteResultMessage(app.ProcessName, targetMuted, changed);
        _configService.AppendDiagnostic($"toggle result process={app.ProcessName} mute={targetMuted} changed={changed} config_manual_keys=[{string.Join(",", _config.ManualMuted)}]");
        MarkChanged();
    }

    private void SetAppKeysMute(IReadOnlyList<string> manualKeys, IReadOnlyList<string> audioKeys, string label, bool muted)
    {
        SetManualKeysMute(manualKeys, muted);
        var changed = audioKeys.Sum(appKey => _audio.SetMuteForApp(appKey, muted));
        PushMuteResultMessage(label, muted, changed);
        MarkChanged();
    }

    private void SetManualKeysMute(IEnumerable<string> manualKeys, bool muted)
    {
        lock (_stateLock)
        {
            foreach (var appKey in manualKeys)
            {
                RuleHelpers.SetManualMuted(_config, appKey, muted);
            }
            _configService.Save(_config);
        }

        MarkChanged();
    }

    private void AddProcessToWhitelist(string processName)
    {
        lock (_stateLock)
        {
            RuleHelpers.AddWhitelist(_config, processName);
            _configService.Save(_config);
            PushMessage($"Added {processName} to auto mute whitelist.");
        }

        MarkChanged();
    }

    private void PushMuteResultMessage(string label, bool muted, int changed)
    {
        lock (_stateLock)
        {
            PushMessage($"Manually {(muted ? "muted" : "unmuted")} {changed} audio session(s) for {label}.");
        }
    }

    private void StartSelectionMode(SelectionAction action)
    {
        lock (_stateLock)
        {
            if (_selectionActive)
            {
                return;
            }

            _selectionActive = true;
            _selectionAction = action;
            PushMessage(action == SelectionAction.ToggleMute
                ? "Selection mode: click a window to toggle mute, or press Esc."
                : "Whitelist selection mode: click a window to add it to whitelist, or press Esc.");
        }

        _configService.AppendDiagnostic("selection mode started");
        MarkChanged();
        _selection.Start(action);
    }

    private bool IsSelectionActive(SelectionAction action)
    {
        lock (_stateLock)
        {
            return _selectionActive && _selectionAction == action;
        }
    }

    private void CancelSelection(SelectionAction action)
    {
        lock (_stateLock)
        {
            if (!_selectionActive || _selectionAction != action)
            {
                return;
            }

            _selectionActive = false;
            _selectionAction = SelectionAction.ToggleMute;
            PushMessage("Selection mode cancelled.");
        }

        MarkChanged();
    }

    private void TimeoutSelection(SelectionAction action, string message)
    {
        lock (_stateLock)
        {
            if (!_selectionActive || _selectionAction != action)
            {
                return;
            }

            _selectionActive = false;
            _selectionAction = SelectionAction.ToggleMute;
            PushMessage(message);
        }

        MarkChanged();
    }

    private void FailSelection(SelectionAction action, string message)
    {
        lock (_stateLock)
        {
            if (!_selectionActive || _selectionAction != action)
            {
                return;
            }

            _selectionActive = false;
            _selectionAction = SelectionAction.ToggleMute;
            PushMessage(message);
        }

        MarkChanged();
    }

    private bool TryCompleteSelectionFromForeground(AppInfoDto app)
    {
        return CompleteSelectionCore(app, _selectionAction, "Selection foreground");
    }

    private void CompleteSelection(AppInfoDto? app, SelectionAction action, string source)
    {
        _ = CompleteSelectionCore(app, action, source);
    }

    private bool CompleteSelectionCore(AppInfoDto? app, SelectionAction action, string source)
    {
        if (app is null)
        {
            lock (_stateLock)
            {
                if (!_selectionActive || _selectionAction != action)
                {
                    return false;
                }

                _selectionActive = false;
                _selectionAction = SelectionAction.ToggleMute;
                PushMessage("Selection failed: no window handle");
            }

            MarkChanged();
            return true;
        }

        SelectionAction selectedAction;
        lock (_stateLock)
        {
            if (!_selectionActive || _selectionAction != action)
            {
                return false;
            }

            if (_windows.IsCurrentProcessApp(app))
            {
                PushMessage($"{source} ignored WindowMute.");
                _configService.AppendDiagnostic($"{source} ignored WindowMute");
                MarkChanged();
                return false;
            }

            selectedAction = _selectionAction;
            _selectionActive = false;
            _selectionAction = SelectionAction.ToggleMute;
            _lastTargetApp = app;
            PushMessage($"{source} matched {app.ProcessName}.");
        }

        _configService.AppendDiagnostic($"{source} target process={app.ProcessName} key={app.Key} pid={app.Pid} title={app.Title}");
        MarkChanged();

        try
        {
            if (selectedAction == SelectionAction.ToggleMute)
            {
                ToggleAppMute(app);
            }
            else
            {
                AddProcessToWhitelist(app.ProcessName);
            }

            return true;
        }
        catch (Exception error)
        {
            lock (_stateLock)
            {
                PushMessage($"Selection failed: {error.Message}");
            }

            MarkChanged();
            return true;
        }
    }

    private void OnHotkeyToggleForeground()
    {
        try
        {
            ToggleForegroundMute();
        }
        catch (Exception error)
        {
            _configService.AppendDiagnostic($"hotkey failed: {error}");
            lock (_stateLock)
            {
                PushMessage($"Hotkey failed: {error.Message}");
            }
            MarkChanged();
        }
    }

    private void OnForegroundChanged()
    {
        try
        {
            var app = _windows.ForegroundApp();
            if (app is not null && TryCompleteSelectionFromForeground(app))
            {
                return;
            }

            RestoreForegroundAutoMuteFast();
            RefreshState();
        }
        catch (Exception error)
        {
            _configService.AppendDiagnostic($"foreground failed: {error}");
        }
    }

    private void RegisterConfiguredHotkeys()
    {
        var hotkey = _config.Hotkeys.ToggleForeground;
        var error = _hotkeys?.Register(hotkey);
        if (error is null)
        {
            return;
        }

        var display = hotkey.Display();
        lock (_stateLock)
        {
            PushMessage($"Failed to register {display}. The shortcut may already be used by another app or another WindowMute instance. Change it in Settings. Details: {error}.");
        }
        MarkChanged();
    }

    private (List<string> ManualKeys, List<string> AudioKeys) AppKeysForQuery(string appKey)
    {
        var matchingApps = Array.Empty<AppInfoDto>() as IReadOnlyList<AppInfoDto>;
        try
        {
            matchingApps = _audio.MatchingAppsForKey(appKey);
        }
        catch (Exception error)
        {
            _configService.AppendDiagnostic($"matching apps for key failed: {error}");
        }

        var manualKeys = ManualKeysForRawQuery(appKey);
        foreach (var app in matchingApps)
        {
            ExtendUniqueKeys(manualKeys, ManualKeysForApp(app));
        }

        var audioKeys = new List<string>();
        foreach (var app in matchingApps)
        {
            ExtendUniqueKeys(audioKeys, [app.Key]);
        }

        if (audioKeys.Count == 0)
        {
            audioKeys = [.. manualKeys];
        }

        return (manualKeys, audioKeys);
    }

    private static List<string> ManualKeysForApp(AppInfoDto app)
    {
        var keys = new List<string>();
        ExtendUniqueKeys(keys, [app.Key, app.ProcessName]);
        if (app.ExePath is not null)
        {
            ExtendUniqueKeys(keys, [app.ExePath]);
        }

        return keys;
    }

    private static List<string> ManualKeysForRawQuery(string appKey)
    {
        var keys = new List<string>();
        ExtendUniqueKeys(keys, [appKey]);
        return keys;
    }

    private static void ExtendUniqueKeys(List<string> keys, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var normalized = RuleHelpers.NormalizeAppKey(value);
            if (normalized.Length > 0 && !keys.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(normalized);
            }
        }
    }

    private static string? EffectiveReason(bool autoEnabled, bool whitelist, bool manual, bool auto)
    {
        if (manual)
        {
            return "Manual";
        }
        if (auto)
        {
            return "Auto inactive";
        }
        if (autoEnabled && whitelist)
        {
            return "Whitelisted";
        }

        return null;
    }

    private static string SelectionMode(SelectionAction action)
    {
        return action == SelectionAction.AddWhitelist ? "add_whitelist" : "toggle_mute";
    }

    private void PushMessage(string message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > 12)
        {
            _messages.Dequeue();
        }
    }

    private List<string> DrainMessages()
    {
        var messages = new List<string>(_messages);
        _messages.Clear();
        return messages;
    }

    private void MarkChanged()
    {
        Interlocked.Increment(ref _version);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
