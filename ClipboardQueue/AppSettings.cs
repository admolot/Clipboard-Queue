using System;
using System.IO;
using System.Text.Json;

namespace ClipboardQueue;

public sealed class AppSettings
{
    public string PasteAllSeparator { get; set; } = Environment.NewLine + Environment.NewLine;

    public bool OverrideCtrlV { get; set; } = true;

    public bool RenderMarkdownForPlainText { get; set; } = false;

    // Set to false to stop writing diagnostics.log.
    public bool Diagnostics { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipboardQueue");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);

                if (settings != null)
                    return settings;
            }
            else
            {
                AppSettings defaults = new();
                Save(defaults);
                return defaults;
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
        }
    }
}
