using System.Text.Json;

namespace WindowMute.App.Services;

internal sealed class UiSettings
{
    public string Language { get; set; } = LocalizationService.SystemLanguage;
    public string Theme { get; set; } = UiSettingsService.SystemTheme;
}

internal sealed class UiSettingsService
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public UiSettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        SettingsPath = Path.Combine(appData, "WindowMute", "ui-settings.json");
        Current = Load();
    }

    public string SettingsPath { get; }

    public UiSettings Current { get; private set; }

    public void SetLanguage(string language)
    {
        Current.Language = LocalizationService.NormalizeLanguage(language);
        Save();
    }

    public void SetTheme(string theme)
    {
        Current.Theme = NormalizeTheme(theme);
        Save();
    }

    public static string NormalizeTheme(string? theme)
    {
        return theme switch
        {
            LightTheme => LightTheme,
            DarkTheme => DarkTheme,
            _ => SystemTheme
        };
    }

    private UiSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UiSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? new UiSettings();
            settings.Language = LocalizationService.NormalizeLanguage(settings.Language);
            settings.Theme = NormalizeTheme(settings.Theme);
            return settings;
        }
        catch
        {
            return new UiSettings();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
