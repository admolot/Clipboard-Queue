using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClipboardQueue;

public sealed class AppSettings
{
    public string PasteAllSeparator { get; set; } = Environment.NewLine + Environment.NewLine;

    public bool OverrideCtrlV { get; set; } = true;

    public bool RenderMarkdownForPlainText { get; set; } = false;

    public bool InterceptAllPastes { get; set; } = true;

    public bool Diagnostics { get; set; } = true;

    public List<string> RealDataApps { get; set; } = new List<string> { "anki" };

    public bool StripHyperlinks { get; set; } = true;

    public bool StripBulletPoints { get; set; } = false;

    // If true, user-defined filter words are removed from text and HTML.
    // If false, the filter logic is bypassed completely (zero CPU overhead).
    public bool EnableFilter { get; set; } = true;

    public List<string> FilterWords { get; set; } = new List<string>();
}

public static class SettingsManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
        }
    }
}
