using Microsoft.Win32;
using System.Diagnostics;

namespace WindowMute.App.Services;

internal sealed class StartupService
{
    public const string TrayArgument = "--tray";
    public const string RunValueName = "WindowMute";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IStartupRunStore _store;
    private readonly Func<string> _currentExePathProvider;

    public StartupService()
        : this(new RegistryStartupRunStore(), GetDefaultCurrentExePath)
    {
    }

    internal StartupService(IStartupRunStore store, Func<string> currentExePathProvider)
    {
        _store = store;
        _currentExePathProvider = currentExePathProvider;
    }

    public string? GetCurrentRunCommand() => _store.GetValue(RunValueName);

    public bool IsEnabledForCurrentExe()
    {
        var command = GetCurrentRunCommand();
        return IsRunCommandValidForCurrentExe(command, GetCurrentExePath());
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _store.SetValue(RunValueName, BuildStartupCommand(GetCurrentExePath()));
            return;
        }

        _store.DeleteValue(RunValueName);
    }

    public void ReconcileOnLaunch()
    {
        var command = GetCurrentRunCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (!IsRunCommandValidForCurrentExe(command, GetCurrentExePath()))
        {
            _store.DeleteValue(RunValueName);
        }
    }

    internal static string BuildStartupCommand(string exePath) => $"\"{exePath}\" {TrayArgument}";

    internal static bool IsRunCommandValidForCurrentExe(string? command, string currentExePath)
    {
        var parsed = ParseRunCommand(command);
        if (parsed is null || !parsed.HasTrayArgument)
        {
            return false;
        }

        if (!File.Exists(parsed.ExePath))
        {
            return false;
        }

        return PathEquals(parsed.ExePath, currentExePath);
    }

    internal static StartupRunCommand? ParseRunCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var value = command.Trim();
        string exePath;
        string args;

        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return null;
            }

            exePath = value[1..endQuote];
            args = value[(endQuote + 1)..].Trim();
        }
        else
        {
            var firstSpace = value.IndexOfAny([' ', '\t']);
            if (firstSpace < 0)
            {
                exePath = value;
                args = string.Empty;
            }
            else
            {
                exePath = value[..firstSpace];
                args = value[(firstSpace + 1)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        return new StartupRunCommand(exePath, HasTrayArgument(args));
    }

    private string GetCurrentExePath()
    {
        var path = _currentExePathProvider();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Unable to resolve current executable path.");
        }

        return Path.GetFullPath(path);
    }

    private static string GetDefaultCurrentExePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Unable to resolve current executable path.");
    }

    private static bool HasTrayArgument(string args)
    {
        return args
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(arg => string.Equals(arg, TrayArgument, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RegistryStartupRunStore : IStartupRunStore
    {
        public string? GetValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }

        public void SetValue(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void DeleteValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}

internal interface IStartupRunStore
{
    string? GetValue(string name);

    void SetValue(string name, string value);

    void DeleteValue(string name);
}

internal sealed record StartupRunCommand(string ExePath, bool HasTrayArgument);
