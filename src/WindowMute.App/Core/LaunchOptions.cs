namespace WindowMute.App.Core;

internal static class LaunchOptions
{
    public static bool ShouldStartInTray(IEnumerable<string> args)
    {
        return args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
    }
}
