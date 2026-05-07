using System.Text.Json;
using WindowMute.App.Core;
using WindowMute.App.Models;
using Xunit;

namespace WindowMute.App.Tests;

public sealed class CoreRuleTests
{
    [Fact]
    public void Hotkey_parser_normalizes_display()
    {
        var hotkey = HotkeyConfig.Parse("ctrl + alt + m");

        Assert.True(hotkey.Ctrl);
        Assert.True(hotkey.Alt);
        Assert.False(hotkey.Shift);
        Assert.Equal("M", hotkey.Key);
        Assert.Equal("Ctrl+Alt+M", hotkey.Display());
    }

    [Theory]
    [InlineData("M")]
    [InlineData("Ctrl+Alt+M+N")]
    [InlineData("Ctrl+Alt+PrintScreen")]
    public void Hotkey_parser_rejects_unsupported_combinations(string value)
    {
        Assert.Throws<InvalidOperationException>(() => HotkeyConfig.Parse(value));
    }

    [Fact]
    public void Manual_mute_keys_are_normalized_to_process_names()
    {
        var config = new CoreConfig();

        RuleHelpers.SetManualMuted(config, @"C:\Program Files\Google\Chrome\chrome.exe", true);
        RuleHelpers.SetManualMuted(config, "chrome.exe", true);

        Assert.Equal(["chrome.exe"], config.ManualMuted);

        RuleHelpers.SetManualMuted(config, "chrome.exe", false);
        Assert.Empty(config.ManualMuted);
    }

    [Fact]
    public void Whitelist_matches_process_name_case_insensitively()
    {
        var config = new CoreConfig { Whitelist = ["Spotify.EXE"] };
        RuleHelpers.NormalizeConfig(config);

        var app = new AppInfoDto
        {
            Key = "spotify.exe",
            ProcessName = "SPOTIFY.exe",
            Pid = 1234
        };

        Assert.True(RuleHelpers.IsWhitelisted(config, app));
    }

    [Fact]
    public void Old_config_without_hotkeys_keeps_defaults()
    {
        var config = JsonSerializer.Deserialize<CoreConfig>(
            """{"autoEnabled":true,"whitelist":["music.exe"],"manualMuted":["app.exe"]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        RuleHelpers.NormalizeConfig(config);

        Assert.True(config.AutoEnabled);
        Assert.Equal(["music.exe"], config.Whitelist);
        Assert.Equal(["app.exe"], config.ManualMuted);
        Assert.Equal("M", config.Hotkeys.ToggleForeground.Key);
    }
}
