using System.Text.Json;
using WindowMute.App.Core;
using WindowMute.App.Models;
using WindowMute.App.Services;
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

    [Fact]
    public void Startup_command_quotes_path_and_starts_in_tray()
    {
        var command = StartupService.BuildStartupCommand(@"C:\Program Files\WindowMute\WindowMute.App.exe");

        Assert.Equal(@"""C:\Program Files\WindowMute\WindowMute.App.exe"" --tray", command);
    }

    [Fact]
    public void Startup_is_enabled_only_for_current_exe_with_tray_argument()
    {
        var exePath = CreateTempExePath();
        var store = new MemoryStartupRunStore();
        var service = new StartupService(store, () => exePath);

        service.SetEnabled(true);

        Assert.True(service.IsEnabledForCurrentExe());
        Assert.Equal(StartupService.BuildStartupCommand(exePath), store.GetValue(StartupService.RunValueName));
    }

    [Fact]
    public void Startup_reconcile_removes_moved_portable_path()
    {
        var oldExePath = CreateTempExePath();
        var currentExePath = CreateTempExePath();
        var store = new MemoryStartupRunStore();
        store.SetValue(StartupService.RunValueName, StartupService.BuildStartupCommand(oldExePath));
        var service = new StartupService(store, () => currentExePath);

        service.ReconcileOnLaunch();

        Assert.Null(store.GetValue(StartupService.RunValueName));
        Assert.False(service.IsEnabledForCurrentExe());
    }

    [Fact]
    public void Startup_reconcile_removes_command_without_tray_argument()
    {
        var exePath = CreateTempExePath();
        var store = new MemoryStartupRunStore();
        store.SetValue(StartupService.RunValueName, $@"""{exePath}""");
        var service = new StartupService(store, () => exePath);

        service.ReconcileOnLaunch();

        Assert.Null(store.GetValue(StartupService.RunValueName));
    }

    [Fact]
    public void Startup_reconcile_removes_missing_executable()
    {
        var missingExePath = Path.Combine(Path.GetTempPath(), "WindowMute.Tests", Guid.NewGuid().ToString("N"), "WindowMute.App.exe");
        var currentExePath = CreateTempExePath();
        var store = new MemoryStartupRunStore();
        store.SetValue(StartupService.RunValueName, StartupService.BuildStartupCommand(missingExePath));
        var service = new StartupService(store, () => currentExePath);

        service.ReconcileOnLaunch();

        Assert.Null(store.GetValue(StartupService.RunValueName));
    }

    [Theory]
    [InlineData("--tray", true)]
    [InlineData("--TRAY", true)]
    [InlineData("", false)]
    [InlineData("--other", false)]
    public void Launch_options_parse_tray_argument(string arg, bool expected)
    {
        var args = string.IsNullOrWhiteSpace(arg) ? [] : new[] { arg };

        Assert.Equal(expected, LaunchOptions.ShouldStartInTray(args));
    }

    private static string CreateTempExePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowMute Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WindowMute.App.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class MemoryStartupRunStore : IStartupRunStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string name) => _values.GetValueOrDefault(name);

        public void SetValue(string name, string value) => _values[name] = value;

        public void DeleteValue(string name) => _values.Remove(name);
    }
}
