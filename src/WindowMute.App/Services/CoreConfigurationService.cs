using System.Text.Json;
using WindowMute.App.Core;

namespace WindowMute.App.Services;

internal sealed class CoreConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public CoreConfigurationService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "WindowMute");
        ConfigPath = Path.Combine(directory, "config.json");
        DiagnosticsPath = Path.Combine(directory, "diagnostics.log");
    }

    public string ConfigPath { get; }

    public string DiagnosticsPath { get; }

    public CoreConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return Normalize(new CoreConfig());
            }

            var json = File.ReadAllText(ConfigPath);
            return Normalize(JsonSerializer.Deserialize<CoreConfig>(json, JsonOptions) ?? new CoreConfig());
        }
        catch
        {
            return Normalize(new CoreConfig());
        }
    }

    public void Save(CoreConfig config)
    {
        RuleHelpers.NormalizeConfig(config);
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    public void AppendDiagnostic(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(DiagnosticsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(DiagnosticsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics should never block the app.
        }
    }

    private static CoreConfig Normalize(CoreConfig config)
    {
        RuleHelpers.NormalizeConfig(config);
        return config;
    }
}
