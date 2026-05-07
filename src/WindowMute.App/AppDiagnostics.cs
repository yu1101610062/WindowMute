namespace WindowMute.App;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowMute");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "startup.log");
            lock (Gate)
            {
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startup diagnostics must never prevent the app from launching.
        }
    }
}
