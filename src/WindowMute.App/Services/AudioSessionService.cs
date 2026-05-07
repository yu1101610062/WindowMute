using NAudio.CoreAudioApi;
using WindowMute.App.Core;
using WindowMute.App.Models;

namespace WindowMute.App.Services;

internal sealed class AudioSessionService(WindowService windows)
{
    public IReadOnlyList<AudioSessionRecord> EnumerateSessions()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        var result = new List<AudioSessionRecord>();

        for (var index = 0; index < sessions.Count; index++)
        {
            var control = sessions[index];
            var pid = unchecked((uint)control.GetProcessID);
            if (pid == 0)
            {
                continue;
            }

            var app = windows.ProcessInfo(pid);
            var displayName = string.IsNullOrWhiteSpace(app.ProcessName) ? $"Process {pid}" : app.ProcessName;

            result.Add(new AudioSessionRecord(
                $"{pid}:{index}",
                app,
                displayName,
                control.SimpleAudioVolume.Volume,
                control.SimpleAudioVolume.Mute,
                control.SimpleAudioVolume));
        }

        return result;
    }

    public void SetSessionMute(AudioSessionRecord session, bool muted)
    {
        session.VolumeControl.Mute = muted;
    }

    public int SetMuteForApp(string appKey, bool muted)
    {
        var changed = 0;
        foreach (var session in EnumerateSessions().Where(session => AppKeyMatches(session.App, appKey)))
        {
            session.VolumeControl.Mute = muted;
            changed++;
        }

        return changed;
    }

    public int SetMuteForTarget(AppInfoDto target, bool muted)
    {
        var changed = 0;
        foreach (var session in EnumerateSessions().Where(session => AppMatches(session.App, target)))
        {
            session.VolumeControl.Mute = muted;
            changed++;
        }

        return changed;
    }

    public int SetVolumeForApp(string appKey, float level)
    {
        var changed = 0;
        var clamped = Math.Clamp(level, 0.0f, 1.0f);
        foreach (var session in EnumerateSessions().Where(session => AppKeyMatches(session.App, appKey)))
        {
            session.VolumeControl.Volume = clamped;
            changed++;
        }

        return changed;
    }

    public MatchingAppState MatchingAppState(AppInfoDto target)
    {
        var apps = new List<AppInfoDto>();
        var sessionCount = 0;
        var allMuted = true;

        foreach (var session in EnumerateSessions().Where(session => AppMatches(session.App, target)))
        {
            sessionCount++;
            allMuted &= session.Muted;
            if (!apps.Any(app => app.Key.Equals(session.App.Key, StringComparison.OrdinalIgnoreCase)))
            {
                apps.Add(session.App);
            }
        }

        return new MatchingAppState(apps, sessionCount > 0 && allMuted);
    }

    public IReadOnlyList<AppInfoDto> MatchingAppsForKey(string appKey)
    {
        var apps = new List<AppInfoDto>();
        foreach (var session in EnumerateSessions().Where(session => AppKeyMatches(session.App, appKey)))
        {
            if (!apps.Any(app => app.Key.Equals(session.App.Key, StringComparison.OrdinalIgnoreCase)))
            {
                apps.Add(session.App);
            }
        }

        return apps;
    }

    private static bool AppMatches(AppInfoDto sessionApp, AppInfoDto target)
    {
        return sessionApp.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase)
            || (target.Pid != 0 && sessionApp.Pid == target.Pid)
            || (!string.IsNullOrWhiteSpace(target.ProcessName) && sessionApp.ProcessName.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase))
            || (sessionApp.ExePath is not null && target.ExePath is not null && sessionApp.ExePath.Equals(target.ExePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AppKeyMatches(AppInfoDto app, string query)
    {
        return app.Key.Equals(query, StringComparison.OrdinalIgnoreCase)
            || app.ProcessName.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (app.ExePath is not null && app.ExePath.Equals(query, StringComparison.OrdinalIgnoreCase))
            || RuleHelpers.NormalizeAppKey(app.Key).Equals(RuleHelpers.NormalizeAppKey(query), StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record AudioSessionRecord(
    string SessionId,
    AppInfoDto App,
    string DisplayName,
    float Volume,
    bool Muted,
    SimpleAudioVolume VolumeControl);

internal sealed record MatchingAppState(IReadOnlyList<AppInfoDto> Apps, bool AllMuted);
