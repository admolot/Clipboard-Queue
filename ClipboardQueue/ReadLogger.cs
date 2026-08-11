using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardQueue;

/// <summary>
/// Logs every clipboard read (real pastes and background reads) together with
/// the process/window that was in the foreground at that moment.
/// This helps identify apps or extensions that secretly read the clipboard.
/// </summary>
internal static class ReadLogger
{
    private static readonly object Sync = new();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClipboardQueue",
            "clipboard_reads.log");

    public static void Log(string kind)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind} | foreground: {GetForegroundDescription()}";

            lock (Sync)
            {
                string? dir = Path.GetDirectoryName(LogPath);

                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static string GetForegroundDescription()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();

            if (hwnd == IntPtr.Zero)
                return "none |";

            GetWindowThreadProcessId(hwnd, out uint pid);

            var sb = new StringBuilder(256);
            string title = GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";

            string proc = Process.GetProcessById((int)pid).ProcessName;

            return $"{proc} | {title}";
        }
        catch
        {
            return "unknown |";
        }
    }
}
