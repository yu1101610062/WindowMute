namespace WindowMute.App.Models;

public sealed class SnapshotDto
{
    public ulong Version { get; set; }
    public bool AutoEnabled { get; set; }
    public bool SelectionActive { get; set; }
    public string? SelectionMode { get; set; }
    public HotkeySnapshotDto Hotkeys { get; set; } = new();
    public AppInfoDto? Foreground { get; set; }
    public List<SessionInfoDto> Sessions { get; set; } = [];
    public List<string> Whitelist { get; set; } = [];
    public List<AppInfoDto> MutedApps { get; set; } = [];
    public List<string> Messages { get; set; } = [];
}

public sealed class HotkeySnapshotDto
{
    public string ToggleForeground { get; set; } = "Ctrl+Alt+M";
}

public sealed class SessionInfoDto
{
    public string SessionId { get; set; } = "";
    public AppInfoDto App { get; set; } = new();
    public string DisplayName { get; set; } = "";
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public bool ManualMuted { get; set; }
    public bool AutoMuted { get; set; }
    public bool Whitelisted { get; set; }
    public bool IsForeground { get; set; }
    public string? EffectiveReason { get; set; }
}

public sealed class AppInfoDto
{
    public string Key { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string? ExePath { get; set; }
    public uint Pid { get; set; }
    public string? Title { get; set; }
    public string? ClassName { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title!;
}
