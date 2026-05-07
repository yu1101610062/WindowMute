using WindowMute.App.Models;

namespace WindowMute.App.Core;

internal static class RuleHelpers
{
    public static string NormalizeName(string value) => value.Trim().ToLowerInvariant();

    public static string NormalizeAppKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName) || fileName == trimmed)
        {
            var slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            if (slash >= 0 && slash + 1 < trimmed.Length)
            {
                fileName = trimmed[(slash + 1)..];
            }
        }

        return NormalizeName(string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName);
    }

    public static void NormalizeConfig(CoreConfig config)
    {
        config.Whitelist = config.Whitelist
            .Select(NormalizeName)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.ManualMuted = config.ManualMuted
            .Select(NormalizeAppKey)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.Hotkeys ??= new HotkeyConfigSet();
        config.Hotkeys.ToggleForeground ??= HotkeyConfig.DefaultToggle();
    }

    public static bool IsWhitelisted(CoreConfig config, AppInfoDto app)
    {
        var names = config.Whitelist;
        return names.Any(entry => entry.Equals(NormalizeName(app.ProcessName), StringComparison.OrdinalIgnoreCase))
            || (app.ExePath is not null && names.Any(entry => entry.Equals(NormalizeName(app.ExePath), StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsManualMuted(CoreConfig config, string appKey)
    {
        var key = NormalizeAppKey(appKey);
        return config.ManualMuted.Any(entry => entry.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsManualMutedApp(CoreConfig config, AppInfoDto app)
    {
        return IsManualMuted(config, app.Key)
            || IsManualMuted(config, app.ProcessName)
            || (app.ExePath is not null && IsManualMuted(config, app.ExePath));
    }

    public static void SetManualMuted(CoreConfig config, string appKey, bool muted)
    {
        var key = NormalizeAppKey(appKey);
        config.ManualMuted.RemoveAll(entry => entry.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (muted && key.Length > 0)
        {
            config.ManualMuted.Add(key);
            config.ManualMuted.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void AddWhitelist(CoreConfig config, string processName)
    {
        var name = NormalizeName(processName);
        if (name.Length > 0 && !config.Whitelist.Any(entry => entry.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            config.Whitelist.Add(name);
            config.Whitelist.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void RemoveWhitelist(CoreConfig config, string processName)
    {
        var name = NormalizeName(processName);
        config.Whitelist.RemoveAll(entry => entry.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
