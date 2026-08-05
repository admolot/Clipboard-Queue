using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClipboardQueue;

/// <summary>
/// Writes text + HTML to the clipboard using the native Win32 API,
/// so the CF_HTML ("HTML Format") data is guaranteed to be UTF-8
/// with correct byte offsets. This makes rich paste work reliably
/// in apps like Anki, Word, browsers, etc.
/// </summary>
internal static class NativeClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static bool TrySetHtmlAndText(string text, string htmlClipboardData, int retries = 10)
    {
        uint cfHtml = RegisterClipboardFormat("HTML Format");

        if (cfHtml == 0)
            return false;

        for (int i = 0; i < retries; i++)
        {
            if (TrySetOnce(text, htmlClipboardData, cfHtml))
                return true;

            Thread.Sleep(80);
        }

        return false;
    }

    private static bool TrySetOnce(string text, string htmlClipboardData, uint cfHtml)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            bool textOk = SetUnicodeText(text);
            bool htmlOk = SetHtml(htmlClipboardData, cfHtml);

            return textOk && htmlOk;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool SetUnicodeText(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        return SetBytes(CF_UNICODETEXT, bytes);
    }

    private static bool SetHtml(string htmlClipboardData, uint cfHtml)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(htmlClipboardData + "\0");
        return SetBytes(cfHtml, bytes);
    }

    private static bool SetBytes(uint format, byte[] bytes)
    {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)bytes.Length);

        if (hMem == IntPtr.Zero)
            return false;

        IntPtr locked = GlobalLock(hMem);

        if (locked == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        Marshal.Copy(bytes, 0, locked, bytes.Length);
        GlobalUnlock(hMem);

        IntPtr result = SetClipboardData(format, hMem);

        if (result == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        // On success the system owns hMem. Do NOT free it.
        return true;
    }
}
