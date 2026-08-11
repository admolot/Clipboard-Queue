using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClipboardQueue;

/// <summary>
/// Native Win32 clipboard access:
/// - writing text + UTF-8 CF_HTML with correct byte offsets,
/// - "delayed rendering" ownership so we can detect when ANY app pastes.
/// </summary>
internal static class NativeClipboard
{
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    public const int WM_RENDERALLFORMATS = 0x0306;
    public const int WM_RENDERFORMAT = 0x0305;
    public const int WM_DESTROYCLIPBOARD = 0x0301;

    private static uint? _cfHtml;

    public static uint CfHtml =>
        _cfHtml ??= RegisterClipboardFormat("HTML Format");

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
        for (int i = 0; i < retries; i++)
        {
            if (TrySetOnce(text, htmlClipboardData))
                return true;

            Thread.Sleep(80);
        }

        return false;
    }

    private static bool TrySetOnce(string text, string htmlClipboardData)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            bool textOk = SetBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            bool htmlOk = SetBytes(CfHtml, Encoding.UTF8.GetBytes(htmlClipboardData + "\0"));

            return textOk && htmlOk;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Takes ownership of the clipboard using delayed rendering.
    /// The data is supplied later, when another app actually requests it
    /// (we get WM_RENDERFORMAT then). This is how we detect "any" paste.
    /// </summary>
    public static bool ArmDelayed(IntPtr ownerWindow, int retries = 10)
    {
        for (int i = 0; i < retries; i++)
        {
            if (!OpenClipboard(ownerWindow))
            {
                Thread.Sleep(50);
                continue;
            }

            try
            {
                if (EmptyClipboard())
                {
                    SetClipboardData(CF_UNICODETEXT, IntPtr.Zero);
                    SetClipboardData(CfHtml, IntPtr.Zero);
                    return true;
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    /// <summary>
    /// Supplies data while handling WM_RENDERFORMAT.
    /// Must be called WITHOUT opening the clipboard.
    /// </summary>
    public static bool ProvideData(uint format, byte[] bytes)
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

        return true;
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

        return true;
    }
}
