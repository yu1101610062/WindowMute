using System.Text.Json.Serialization;

namespace WindowMute.App.Core;

internal sealed class CoreConfig
{
    public bool AutoEnabled { get; set; }

    public List<string> Whitelist { get; set; } = DefaultWhitelist();

    public List<string> ManualMuted { get; set; } = [];

    public HotkeyConfigSet Hotkeys { get; set; } = new();

    public static List<string> DefaultWhitelist() =>
    [
        "spotify.exe",
        "applemusic.exe",
        "wmplayer.exe",
        "music.ui.exe",
        "cloudmusic.exe",
        "qqmusic.exe",
        "kugou.exe",
        "kuwo.exe",
        "foobar2000.exe",
        "musicbee.exe"
    ];
}

internal sealed class HotkeyConfigSet
{
    public HotkeyConfig ToggleForeground { get; set; } = HotkeyConfig.DefaultToggle();
}

internal sealed class HotkeyConfig
{
    public bool Ctrl { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public bool Win { get; set; }

    public string Key { get; set; } = "M";

    [JsonIgnore]
    public bool HasModifier => Ctrl || Alt || Shift || Win;

    public static HotkeyConfig DefaultToggle() => new()
    {
        Ctrl = true,
        Alt = true,
        Shift = false,
        Win = false,
        Key = "M"
    };

    public static HotkeyConfig Parse(string value)
    {
        var hotkey = new HotkeyConfig { Key = "" };
        string? key = null;

        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    hotkey.Ctrl = true;
                    break;
                case "alt":
                    hotkey.Alt = true;
                    break;
                case "shift":
                    hotkey.Shift = true;
                    break;
                case "win":
                case "windows":
                case "meta":
                    hotkey.Win = true;
                    break;
                default:
                    if (key is not null)
                    {
                        throw new InvalidOperationException("hotkey can only contain one non-modifier key");
                    }

                    key = NormalizeKey(rawPart);
                    break;
            }
        }

        if (!hotkey.HasModifier)
        {
            throw new InvalidOperationException("hotkey must include at least one modifier");
        }

        hotkey.Key = key ?? throw new InvalidOperationException("hotkey must include a key");
        return hotkey;
    }

    public string Display()
    {
        var parts = new List<string>();
        if (Ctrl)
        {
            parts.Add("Ctrl");
        }
        if (Alt)
        {
            parts.Add("Alt");
        }
        if (Shift)
        {
            parts.Add("Shift");
        }
        if (Win)
        {
            parts.Add("Win");
        }

        parts.Add(Key.ToUpperInvariant());
        return string.Join("+", parts);
    }

    public uint VirtualKey()
    {
        var key = Key.ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key[0];
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return (uint)(0x70 + functionKey - 1);
        }

        throw new InvalidOperationException($"unsupported key {Key}");
    }

    public uint ModifierFlags()
    {
        var modifiers = 0u;
        if (Alt)
        {
            modifiers |= 0x0001;
        }
        if (Ctrl)
        {
            modifiers |= 0x0002;
        }
        if (Shift)
        {
            modifiers |= 0x0004;
        }
        if (Win)
        {
            modifiers |= 0x0008;
        }

        return modifiers | 0x4000;
    }

    private static string NormalizeKey(string value)
    {
        var key = value.Trim().ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key;
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return $"F{functionKey}";
        }

        throw new InvalidOperationException("key must be A-Z, 0-9, or F1-F24");
    }
}

internal enum SelectionAction
{
    ToggleMute,
    AddWhitelist
}
