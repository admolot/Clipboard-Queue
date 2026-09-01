using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardQueue;

internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private const string MenuWindowClass = "#32768";

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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    private readonly LowLevelMouseProc _proc;
    private readonly IntPtr _hookId;
    private bool _disposed;

    private DateTime _lastRightButtonUp = DateTime.MinValue;

    /// <summary>
    /// Fired when the user left-clicks shortly after a right click.
    /// Arguments: clipboard sequence at click time, and whether the click
    /// landed on a real system context menu (window class "#32768").
    /// </summary>
    public Action<uint, bool>? LeftClickAfterRightClick { get; set; }

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
                    if ((DateTime.UtcNow - _lastRightButtonUp).TotalSeconds < 10)
                    {
                        bool menuWindow = IsMenuWindowUnderCursor(lParam);

                        if (menuWindow)
                        {
                            InputActivity.NoteGesture();
                        }
                        else
                        {
                            InputActivity.Note();
                        }

                        LeftClickAfterRightClick?.Invoke(
                            NativeMethods.GetClipboardSequenceNumber(),
                            menuWindow);
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

    private static bool IsMenuWindowUnderCursor(IntPtr lParam)
    {
        try
        {
            POINT pt;
            pt.x = Marshal.ReadInt32(lParam, 0);
            pt.y = Marshal.ReadInt32(lParam, 4);

            IntPtr hwnd = WindowFromPoint(pt);

            if (hwnd == IntPtr.Zero)
                return false;

            var sb = new StringBuilder(32);

            if (GetClassName(hwnd, sb, sb.Capacity) == 0)
                return false;

            return sb.ToString() == MenuWindowClass;
        }
        catch
        {
            return false;
        }
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
