using System.Globalization;

namespace WindowMute.App.Services;

internal sealed class LocalizationService
{
    public const string SystemLanguage = "system";
    public const string English = "en-US";
    public const string SimplifiedChinese = "zh-CN";

    private readonly UiSettingsService _settings;
    private readonly CultureInfo _systemCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _systemUiCulture = CultureInfo.CurrentUICulture;

    public LocalizationService(UiSettingsService settings)
    {
        _settings = settings;
        ApplyThreadCulture();
    }

    public string LanguageSetting => _settings.Current.Language;

    public string ResolvedLanguage => LanguageSetting == SystemLanguage
        ? ResolveSystemLanguage(_systemUiCulture)
        : NormalizeLanguage(LanguageSetting);

    public static string NormalizeLanguage(string? language)
    {
        return language switch
        {
            English => English,
            SimplifiedChinese => SimplifiedChinese,
            _ => SystemLanguage
        };
    }

    public void ApplyThreadCulture()
    {
        var culture = LanguageSetting == SystemLanguage ? _systemCulture : new CultureInfo(ResolvedLanguage);
        var uiCulture = LanguageSetting == SystemLanguage ? _systemUiCulture : new CultureInfo(ResolvedLanguage);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
    }

    public string T(string key)
    {
        var table = ResolvedLanguage == SimplifiedChinese ? ZhCn : EnUs;
        return table.TryGetValue(key, out var value)
            ? value
            : EnUs.TryGetValue(key, out var fallback)
                ? fallback
                : key;
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    public string CoreMessage(string message)
    {
        var value = message.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value switch
        {
            "WindowMute core started." => T("core.started"),
            "Auto mute enabled." => T("core.auto.enabled"),
            "Auto mute disabled." => T("core.auto.disabled"),
            "Hotkey updated." => T("core.hotkey.updated"),
            "Removed WindowMute from manual mute state." => T("core.self_unmuted"),
            "Selection mode: click a window to toggle mute, or press Esc." => T("core.selection.started"),
            "Whitelist selection mode: click a window to add it to whitelist, or press Esc." => T("core.selection.whitelist.started"),
            "Failed to enter selection mode." => T("core.selection.enter_failed"),
            "Selection mode timed out." => T("core.selection.timeout"),
            "Selection mode cancelled." => T("core.selection.cancelled"),
            "Failed to register foreground window hook." => T("core.foreground_hook.failed"),
            "Failed to register fallback keyboard hook." => T("core.hotkey_hook.failed"),
            "Native core returned a null response." => T("core.native.null_response"),
            "Native core returned an empty response." => T("core.native.empty_response"),
            "Failed to deserialize native response." => T("core.native.deserialize_failed"),
            "Failed to create WindowMute core." => T("core.native.create_failed"),
            "Failed to start WindowMute core." => T("core.native.start_failed"),
            "no foreground window" => T("core.foreground.none"),
            "hotkey can only contain one non-modifier key" => T("core.hotkey.single_key"),
            "hotkey must include at least one modifier" => T("core.hotkey.modifier_required"),
            "hotkey must include a key" => T("core.hotkey.key_required"),
            "key must be A-Z, 0-9, or F1-F24" => T("core.hotkey.invalid_key"),
            _ => CoreMessageByPattern(value)
        };
    }

    public string SessionReason(string? reason)
    {
        return reason switch
        {
            "Manual" => T("session.reason.manual"),
            "Auto inactive" => T("session.reason.auto_inactive"),
            "Whitelisted" => T("session.reason.whitelisted"),
            _ => reason ?? string.Empty
        };
    }

    private static string ResolveSystemLanguage(CultureInfo culture)
    {
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : English;
    }

    private string CoreMessageByPattern(string value)
    {
        if (value.StartsWith("Failed to register ", StringComparison.OrdinalIgnoreCase)
            && value.Contains("shortcut", StringComparison.OrdinalIgnoreCase)
            && TryExtractBetween(value, "Failed to register ", ".", out var display))
        {
            return Format("core.hotkey.conflict", display);
        }

        if (TryExtractBetween(value, "Updated volume for ", " audio session(s).", out var volumeCount))
        {
            return Format("core.volume.updated", volumeCount);
        }

        if (TryExtractBetween(value, "Added ", " to auto mute whitelist.", out var addedProcess))
        {
            return Format("core.whitelist.added", addedProcess);
        }

        if (TryExtractBetween(value, "Removed ", " from whitelist.", out var removedProcess))
        {
            return Format("core.whitelist.removed", removedProcess);
        }

        if (TryExtractManualMute(value, "Manually muted ", out var mutedCount, out var mutedLabel))
        {
            return Format("core.manual.muted", mutedCount, mutedLabel);
        }

        if (TryExtractManualMute(value, "Manually unmuted ", out var unmutedCount, out var unmutedLabel))
        {
            return Format("core.manual.unmuted", unmutedCount, unmutedLabel);
        }

        if (TryExtractPrefix(value, "Audio enumeration failed: ", out var audioError))
        {
            return Format("core.audio.enumeration_failed", audioError);
        }

        if (TryExtractPrefix(value, "Hotkey failed: ", out var hotkeyError))
        {
            return Format("core.hotkey.failed", hotkeyError);
        }

        if (TryExtractPrefix(value, "Selection failed: ", out var selectionError))
        {
            return Format("core.selection.failed", selectionError);
        }

        if (value.StartsWith("Selection ", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(" ignored WindowMute.", StringComparison.Ordinal))
        {
            return T("core.selection.ignored_self");
        }

        if (value.StartsWith("Selection ", StringComparison.OrdinalIgnoreCase)
            && TryExtractAfter(value, " matched ", ".", out var selectedProcess))
        {
            return Format("core.selection.matched", selectedProcess);
        }

        if (TryExtractPrefix(value, "Invalid command JSON: ", out var commandJsonError))
        {
            return Format("core.command.invalid_json", commandJsonError);
        }

        if (TryExtractPrefix(value, "unsupported key ", out var unsupportedKey))
        {
            return Format("core.hotkey.unsupported_key", unsupportedKey);
        }

        if (TryExtractPrefix(value, "unknown hotkey action: ", out var hotkeyAction))
        {
            return Format("core.hotkey.unknown_action", hotkeyAction);
        }

        return value;
    }

    private static bool TryExtractBetween(string value, string prefix, string suffix, out string extracted)
    {
        extracted = string.Empty;
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var start = prefix.Length;
        var end = value.IndexOf(suffix, start, StringComparison.Ordinal);
        if (end < start)
        {
            return false;
        }

        extracted = value[start..end];
        return extracted.Length > 0;
    }

    private static bool TryExtractAfter(string value, string marker, string suffix, out string extracted)
    {
        extracted = string.Empty;
        var start = value.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        start += marker.Length;
        var end = value.Length - suffix.Length;
        extracted = value[start..end];
        return extracted.Length > 0;
    }

    private static bool TryExtractPrefix(string value, string prefix, out string extracted)
    {
        extracted = string.Empty;
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        extracted = value[prefix.Length..];
        return extracted.Length > 0;
    }

    private static bool TryExtractManualMute(string value, string prefix, out string count, out string label)
    {
        const string middle = " audio session(s) for ";
        count = string.Empty;
        label = string.Empty;

        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var start = prefix.Length;
        var middleIndex = value.IndexOf(middle, start, StringComparison.Ordinal);
        if (middleIndex < start)
        {
            return false;
        }

        count = value[start..middleIndex];
        label = value[(middleIndex + middle.Length)..^1];
        return count.Length > 0 && label.Length > 0;
    }

    private static readonly IReadOnlyDictionary<string, string> EnUs = new Dictionary<string, string>
    {
        ["app.title"] = "WindowMute",
        ["nav.dashboard"] = "Dashboard",
        ["nav.muted_apps"] = "Muted Apps",
        ["nav.whitelist"] = "Whitelist",
        ["nav.sessions"] = "Sessions",
        ["nav.settings"] = "Settings",
        ["status.selection.title"] = "Selection mode",
        ["status.selection.message"] = "Click a window to toggle mute for that app. The click will pass through and activate the target window. Press Esc to cancel.",
        ["status.selection.whitelist.message"] = "Click a window to add it to the auto mute whitelist. The click will pass through and activate the target window. Press Esc to cancel.",
        ["dashboard.title"] = "Dashboard",
        ["dashboard.subtitle"] = "Fast controls for the current foreground app and automatic background muting.",
        ["dashboard.foreground.none"] = "No foreground window detected",
        ["dashboard.foreground.title"] = "Foreground app",
        ["dashboard.auto.header"] = "Auto mute inactive apps",
        ["dashboard.auto.on"] = "Enabled",
        ["dashboard.auto.off"] = "Disabled",
        ["dashboard.metric.sessions"] = "Active sessions",
        ["dashboard.metric.muted"] = "Muted apps",
        ["command.select_window"] = "Select window",
        ["command.refresh"] = "Refresh",
        ["muted.title"] = "Muted Apps",
        ["muted.subtitle"] = "Apps explicitly muted by WindowMute shortcuts or selection mode.",
        ["muted.empty"] = "No muted apps are active.",
        ["muted.status"] = "Muted",
        ["button.unmute"] = "Unmute",
        ["button.remove"] = "Remove",
        ["whitelist.title"] = "Whitelist",
        ["whitelist.subtitle"] = "Whitelisted apps are allowed to keep playing when auto mute is enabled.",
        ["whitelist.add_none"] = "No foreground app to add",
        ["whitelist.add_foreground"] = "Add foreground: {0}",
        ["whitelist.select_window"] = "Select window",
        ["whitelist.empty"] = "No whitelisted apps yet.",
        ["sessions.title"] = "Sessions",
        ["sessions.subtitle"] = "Current Windows audio sessions and their effective mute state.",
        ["sessions.empty"] = "No active audio sessions detected.",
        ["sessions.pid"] = "PID",
        ["session.muted"] = "Muted",
        ["session.sound"] = "Sound",
        ["settings.title"] = "Settings",
        ["settings.subtitle"] = "Runtime behavior and implementation details.",
        ["settings.language.title"] = "Language",
        ["settings.language.description"] = "Choose the app display language. System follows Windows display language.",
        ["settings.theme.title"] = "Theme",
        ["settings.theme.description"] = "Choose the app theme. System follows Windows light and dark mode.",
        ["settings.startup.title"] = "Start at login",
        ["settings.startup.description"] = "Run in the system tray after signing in to Windows.",
        ["settings.shortcuts.title"] = "Default shortcuts",
        ["settings.shortcuts.message"] = "Focus a shortcut box, press the desired key combination, then apply. Supported keys: A-Z, 0-9, F1-F24.",
        ["settings.shortcuts.toggle"] = "Toggle active window mute",
        ["settings.shortcuts.apply"] = "Apply",
        ["settings.close.title"] = "Close behavior",
        ["settings.close.message"] = "The window close button hides WindowMute to the system tray. Use tray menu Exit to quit.",
        ["settings.configuration.title"] = "Configuration",
        ["settings.configuration.message"] = "Core: %APPDATA%\\WindowMute\\config.json\nUI: %APPDATA%\\WindowMute\\ui-settings.json",
        ["settings.audio.title"] = "Audio model",
        ["settings.audio.message"] = "Windows exposes per-session audio control. WindowMute maps windows to processes and controls matching sessions.",
        ["language.system"] = "Follow system",
        ["language.en"] = "English",
        ["language.zh"] = "Simplified Chinese",
        ["theme.system"] = "Follow system",
        ["theme.light"] = "Light",
        ["theme.dark"] = "Dark",
        ["startup.error"] = "Failed to update startup setting: {0}",
        ["tray.show"] = "Show Window",
        ["tray.select_window"] = "Select Window",
        ["tray.toggle_auto"] = "Toggle Auto Mute",
        ["tray.exit"] = "Exit",
        ["session.reason.manual"] = "Manual",
        ["session.reason.auto_inactive"] = "Auto inactive",
        ["session.reason.whitelisted"] = "Whitelisted",
        ["error.unknown_core"] = "Unknown core error.",
        ["core.started"] = "WindowMute core started.",
        ["core.auto.enabled"] = "Auto mute enabled.",
        ["core.auto.disabled"] = "Auto mute disabled.",
        ["core.hotkey.updated"] = "Hotkey updated.",
        ["core.hotkey.conflict"] = "Failed to register {0}. The shortcut may already be used by another app or another WindowMute instance. Change it in Settings.",
        ["core.volume.updated"] = "Updated volume for {0} audio session(s).",
        ["core.whitelist.added"] = "Added {0} to auto mute whitelist.",
        ["core.whitelist.removed"] = "Removed {0} from whitelist.",
        ["core.self_unmuted"] = "Removed WindowMute from manual mute state.",
        ["core.audio.enumeration_failed"] = "Audio enumeration failed: {0}",
        ["core.foreground_hook.failed"] = "Failed to register foreground window hook.",
        ["core.hotkey_hook.failed"] = "Failed to register fallback keyboard hook.",
        ["core.hotkey.failed"] = "Hotkey failed: {0}",
        ["core.hotkey.unsupported_key"] = "Unsupported shortcut key: {0}",
        ["core.hotkey.unknown_action"] = "Unknown shortcut action: {0}",
        ["core.hotkey.single_key"] = "A shortcut can contain only one non-modifier key.",
        ["core.hotkey.modifier_required"] = "A shortcut must include at least one modifier key.",
        ["core.hotkey.key_required"] = "A shortcut must include a key.",
        ["core.hotkey.invalid_key"] = "Shortcut key must be A-Z, 0-9, or F1-F24.",
        ["core.foreground.none"] = "No foreground window detected.",
        ["core.selection.started"] = "Selection mode started.",
        ["core.selection.whitelist.started"] = "Whitelist selection mode started.",
        ["core.selection.enter_failed"] = "Failed to enter selection mode.",
        ["core.selection.timeout"] = "Selection mode timed out.",
        ["core.selection.cancelled"] = "Selection mode cancelled.",
        ["core.selection.failed"] = "Selection failed: {0}",
        ["core.selection.matched"] = "Selected {0}.",
        ["core.selection.ignored_self"] = "Ignored the WindowMute window.",
        ["core.manual.muted"] = "Manually muted {1} ({0} audio session(s)).",
        ["core.manual.unmuted"] = "Manually unmuted {1} ({0} audio session(s)).",
        ["core.command.invalid_json"] = "Internal command format error: {0}",
        ["core.native.null_response"] = "Native core returned a null response.",
        ["core.native.empty_response"] = "Native core returned an empty response.",
        ["core.native.deserialize_failed"] = "Failed to deserialize native response.",
        ["core.native.create_failed"] = "Failed to create WindowMute core.",
        ["core.native.start_failed"] = "Failed to start WindowMute core."
    };

    private static readonly IReadOnlyDictionary<string, string> ZhCn = new Dictionary<string, string>
    {
        ["app.title"] = "WindowMute",
        ["nav.dashboard"] = "仪表盘",
        ["nav.muted_apps"] = "已静音应用",
        ["nav.whitelist"] = "白名单",
        ["nav.sessions"] = "音频会话",
        ["nav.settings"] = "设置",
        ["status.selection.title"] = "选择模式",
        ["status.selection.message"] = "点击一个窗口即可切换该应用静音。点击会传递给目标窗口并激活它。按 Esc 取消。",
        ["status.selection.whitelist.message"] = "点击一个窗口即可将该应用加入自动静音白名单。点击会传递给目标窗口并激活它。按 Esc 取消。",
        ["dashboard.title"] = "仪表盘",
        ["dashboard.subtitle"] = "快速控制当前前台应用，并管理后台自动静音。",
        ["dashboard.foreground.none"] = "未检测到前台窗口",
        ["dashboard.foreground.title"] = "前台应用",
        ["dashboard.auto.header"] = "自动静音非活动应用",
        ["dashboard.auto.on"] = "已启用",
        ["dashboard.auto.off"] = "已关闭",
        ["dashboard.metric.sessions"] = "活动会话",
        ["dashboard.metric.muted"] = "已静音应用",
        ["command.select_window"] = "选择窗口",
        ["command.refresh"] = "刷新",
        ["muted.title"] = "已静音应用",
        ["muted.subtitle"] = "通过快捷键或选择模式手动静音的应用。",
        ["muted.empty"] = "当前没有活动的已静音应用。",
        ["muted.status"] = "已静音",
        ["button.unmute"] = "取消静音",
        ["button.remove"] = "移除",
        ["whitelist.title"] = "白名单",
        ["whitelist.subtitle"] = "白名单应用在自动静音启用时仍可在后台播放。",
        ["whitelist.add_none"] = "没有可添加的前台应用",
        ["whitelist.add_foreground"] = "添加前台应用：{0}",
        ["whitelist.select_window"] = "选择窗口",
        ["whitelist.empty"] = "还没有白名单应用。",
        ["sessions.title"] = "音频会话",
        ["sessions.subtitle"] = "当前 Windows 音频会话及其最终静音状态。",
        ["sessions.empty"] = "未检测到活动音频会话。",
        ["sessions.pid"] = "PID",
        ["session.muted"] = "已静音",
        ["session.sound"] = "有声",
        ["settings.title"] = "设置",
        ["settings.subtitle"] = "运行行为和实现细节。",
        ["settings.language.title"] = "语言",
        ["settings.language.description"] = "选择应用显示语言。跟随系统会使用 Windows 显示语言。",
        ["settings.theme.title"] = "主题",
        ["settings.theme.description"] = "选择应用主题。跟随系统会使用 Windows 明暗模式。",
        ["settings.startup.title"] = "开机自启",
        ["settings.startup.description"] = "登录 Windows 后自动在系统托盘中运行。",
        ["settings.shortcuts.title"] = "默认快捷键",
        ["settings.shortcuts.message"] = "聚焦快捷键输入框，直接按下目标组合键，然后点击应用。支持 A-Z、0-9、F1-F24。",
        ["settings.shortcuts.toggle"] = "切换当前窗口静音",
        ["settings.shortcuts.apply"] = "应用",
        ["settings.close.title"] = "关闭行为",
        ["settings.close.message"] = "点击窗口关闭按钮会将 WindowMute 隐藏到系统托盘。要退出请使用托盘菜单中的退出。",
        ["settings.configuration.title"] = "配置",
        ["settings.configuration.message"] = "核心：%APPDATA%\\WindowMute\\config.json\n界面：%APPDATA%\\WindowMute\\ui-settings.json",
        ["settings.audio.title"] = "音频模型",
        ["settings.audio.message"] = "Windows 提供按音频会话控制音量的能力。WindowMute 会把窗口映射到进程，并控制匹配的音频会话。",
        ["language.system"] = "跟随系统",
        ["language.en"] = "English",
        ["language.zh"] = "简体中文",
        ["theme.system"] = "跟随系统",
        ["theme.light"] = "浅色",
        ["theme.dark"] = "深色",
        ["startup.error"] = "更新开机自启设置失败：{0}",
        ["tray.show"] = "显示窗口",
        ["tray.select_window"] = "选择窗口",
        ["tray.toggle_auto"] = "切换自动静音",
        ["tray.exit"] = "退出",
        ["session.reason.manual"] = "手动",
        ["session.reason.auto_inactive"] = "自动静音",
        ["session.reason.whitelisted"] = "白名单",
        ["error.unknown_core"] = "未知核心错误。",
        ["core.started"] = "WindowMute 核心已启动。",
        ["core.auto.enabled"] = "已启用自动静音。",
        ["core.auto.disabled"] = "已关闭自动静音。",
        ["core.hotkey.updated"] = "快捷键已更新。",
        ["core.hotkey.conflict"] = "{0} 注册失败。该快捷键可能已被其它应用或另一个 WindowMute 实例占用，请在设置中修改。",
        ["core.volume.updated"] = "已更新 {0} 个音频会话的音量。",
        ["core.whitelist.added"] = "已将 {0} 添加到自动静音白名单。",
        ["core.whitelist.removed"] = "已将 {0} 从白名单移除。",
        ["core.self_unmuted"] = "已从手动静音状态中移除 WindowMute。",
        ["core.audio.enumeration_failed"] = "音频会话枚举失败：{0}",
        ["core.foreground_hook.failed"] = "前台窗口监听注册失败。",
        ["core.hotkey_hook.failed"] = "备用键盘监听注册失败。",
        ["core.hotkey.failed"] = "快捷键执行失败：{0}",
        ["core.hotkey.unsupported_key"] = "不支持的快捷键按键：{0}",
        ["core.hotkey.unknown_action"] = "未知快捷键操作：{0}",
        ["core.hotkey.single_key"] = "快捷键只能包含一个非修饰键。",
        ["core.hotkey.modifier_required"] = "快捷键必须包含至少一个修饰键。",
        ["core.hotkey.key_required"] = "快捷键必须包含一个按键。",
        ["core.hotkey.invalid_key"] = "快捷键按键必须是 A-Z、0-9 或 F1-F24。",
        ["core.foreground.none"] = "未检测到前台窗口。",
        ["core.selection.started"] = "已进入选择模式。",
        ["core.selection.whitelist.started"] = "已进入白名单选择模式。",
        ["core.selection.enter_failed"] = "无法进入选择模式。",
        ["core.selection.timeout"] = "选择模式已超时。",
        ["core.selection.cancelled"] = "已取消选择模式。",
        ["core.selection.failed"] = "选择失败：{0}",
        ["core.selection.matched"] = "已选择 {0}。",
        ["core.selection.ignored_self"] = "已忽略 WindowMute 自身窗口。",
        ["core.manual.muted"] = "已手动静音 {1}（{0} 个音频会话）。",
        ["core.manual.unmuted"] = "已取消静音 {1}（{0} 个音频会话）。",
        ["core.command.invalid_json"] = "内部命令格式错误：{0}",
        ["core.native.null_response"] = "原生核心返回了空响应。",
        ["core.native.empty_response"] = "原生核心返回了空内容。",
        ["core.native.deserialize_failed"] = "无法解析原生核心响应。",
        ["core.native.create_failed"] = "无法创建 WindowMute 核心。",
        ["core.native.start_failed"] = "无法启动 WindowMute 核心。"
    };
}
