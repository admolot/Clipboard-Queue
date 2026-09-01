using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipboardQueue;

internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    private readonly LowLevelMouseProc _proc;
    private readonly IntPtr _hookId;
    private bool _disposed;

    private DateTime _lastRightButtonUp = DateTime.MinValue;

    /// <summary>
    /// Fired when the user clicks an item of a context menu
    /// (left button shortly after a right button release).
    /// The argument is the clipboard sequence number captured synchronously
    /// at the instant of the click.
    /// </summary>
    public Action<uint>? MenuSelectClicked { get; set; }

    public MouseHook()
    {
        _proc = HookCallback;

        _hookId = SetWindowsHookEx(
            WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookId == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;

                if (msg == WM_RBUTTONUP)
                {
                    _lastRightButtonUp = DateTime.UtcNow;
                    InputActivity.Note();
                }
                else if (msg == WM_LBUTTONDOWN)
                {
                    // A left click shortly after a right click almost always
                    // means "user chose an item from a context menu".
                    if ((DateTime.UtcNow - _lastRightButtonUp).TotalSeconds < 10)
                    {
                        InputActivity.NoteGesture();

                        // Capture synchronously, at the exact instant of the
                        // click, so later comparisons are race-free.
                        MenuSelectClicked?.Invoke(NativeMethods.GetClipboardSequenceNumber());
                    }
                    else
                    {
                        InputActivity.Note();
                    }
                }
                else if (msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
                {
                    InputActivity.Note();
                }
            }
        }
        catch
        {
            // Never crash the global mouse hook.
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
        }
    }
}
