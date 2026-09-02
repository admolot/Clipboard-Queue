using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClipboardQueue;

public sealed class AppSettings
{
    public string PasteAllSeparator { get; set; } = Environment.NewLine + Environment.NewLine;

    // If true, Ctrl+V pastes the oldest queued item whenever the queue is not empty.
    public bool OverrideCtrlV { get; set; } = true;

    // If true, plain-text copies (no HTML on clipboard) are rendered as Markdown.
    public bool RenderMarkdownForPlainText { get; set; } = false;

    // If true, the app owns the clipboard while the queue is not empty,
    // so any paste method can paste the oldest item.
    public bool InterceptAllPastes { get; set; } = true;

    // If true, diagnostics.log is written (next to the exe).
    public bool Diagnostics { get; set; } = true;

    // Apps that read the clipboard via OLE (no delayed-render signals).
    // While such an app is in the foreground the clipboard holds REAL data,
    // and mouse-paste consumption uses the click confirmation.
    public List<string> RealDataApps { get; set; } = new List<string> { "anki" };
}

public static class SettingsManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    // Portable: everything is stored next to the executable.
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
