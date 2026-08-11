using System;
using System.IO;
using System.Text.Json;

namespace ClipboardQueue;

public sealed class AppSettings
{
    public string PasteAllSeparator { get; set; } = Environment.NewLine + Environment.NewLine;

    // If true, Ctrl+V pastes the oldest queued item whenever the queue is not empty.
    // If false, Ctrl+V is left alone for normal Windows paste.
    public bool OverrideCtrlV { get; set; } = true;

    // If true, plain-text copies (no HTML on clipboard) are rendered as Markdown.
    // If false (default), they are pasted exactly as-is, preserving all line breaks.
    public bool RenderMarkdownForPlainText { get; set; } = false;

    // If true (default), the app owns the clipboard while the queue is not empty,
    // so ANY paste method (keyboard, right-click menu, Edit menu) pastes the
    // oldest queued item and removes it.
    // If you use Windows Clipboard History (Win+V) or clipboard-monitoring apps,
    // set this to false, because those apps also read the clipboard and would
    // consume queue items.
    public bool InterceptAllPastes { get; set; } = true;
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
            // Ignore settings errors and fall back to defaults.
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
            // Ignore settings save errors.
        }
    }
}
