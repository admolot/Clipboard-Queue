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
    public bool EnableFilter { get; set; } = true;
    public List<string> FilterWords { get; set; } = new List<string>();

    // If true, the pasted HTML keeps the source's exact structure/spacing and
    // only the word-filter is applied (no italics/bullet/block rewriting).
    public bool PreserveSourceSpacing { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
            else
            {
                AppSettings defaults = new();
                Save(defaults);
                return defaults;
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options)); }
        catch { }
    }
}
