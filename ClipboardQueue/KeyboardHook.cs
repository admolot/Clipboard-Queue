using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipboardQueue;

internal sealed class KeyboardHook : IDisposable
{
    private const double RepeatWindowMs = 600;

    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private readonly IntPtr _hookId;

    private bool _ctrlVHandled;
    private bool _ctrlAltVHandled;
    private bool _vKeyActive;
    private DateTime _lastCtrlVKeyDown = DateTime.MinValue;
    private bool _disposed;

    public Func<bool>? ShouldHandleCtrlV { get; set; }
    public Func<bool>? ShouldHandleCtrlAltV { get; set; }

    public Action? CtrlVPressed { get; set; }
    public Action? CtrlAltVPressed { get; set; }

    public KeyboardHook()
    {
        _hookProc = HookCallback;

        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
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
                int vkCode = Marshal.ReadInt32(lParam);
                int flags = Marshal.ReadInt32(lParam, 8);

                bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
                bool keyUp = (flags & NativeMethods.LLKHF_UP) != 0;

                if (!injected)
                {
                    bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

                    if (vkCode == NativeMethods.VK_V && ctrlDown && !keyUp)
                    {
                        // Self-healing repeat detection: a press is a repeat
                        // only if it closely follows the previous V press.
                        bool isRepeat = _vKeyActive &&
                                        (DateTime.UtcNow - _lastCtrlVKeyDown).TotalMilliseconds < RepeatWindowMs;

                        _vKeyActive = true;
                        _lastCtrlVKeyDown = DateTime.UtcNow;

                        bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
                        bool leftMouseDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

                        // Bulk-paste combos are ALWAYS handled, even if the
                        // repeat flag was stuck for some reason.
                        if (altDown || leftMouseDown)
                        {
                            if (ShouldHandleCtrlAltV?.Invoke() == true)
                            {
                                if (!_ctrlAltVHandled)
                                {
                                    _ctrlAltVHandled = true;
                                    CtrlAltVPressed?.Invoke();
                                }

                                return (IntPtr)1;
                            }
                        }
                        else
                        {
                            // Held Ctrl+V: auto-repeats pass through untouched
                            // so the target repeats the current clipboard
                            // natively ("111111...").
                            if (isRepeat)
                            {
                                InputActivity.Note();
                                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                            }

                            if (ShouldHandleCtrlV?.Invoke() == true)
                            {
                                if (!_ctrlVHandled)
                                {
                                    _ctrlVHandled = true;
                                    CtrlVPressed?.Invoke();
                                }

                                return (IntPtr)1;
                            }

                            InputActivity.NoteGesture();
                        }
                    }
                    else if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN ||
                             wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
                    {
                        InputActivity.Note();
                    }

                    if (keyUp &&
                        (vkCode == NativeMethods.VK_V ||
                         vkCode == NativeMethods.VK_CONTROL ||
                         vkCode == NativeMethods.VK_MENU))
                    {
                        _ctrlVHandled = false;
                        _ctrlAltVHandled = false;
                    }

                    if (keyUp &&
                        (vkCode == NativeMethods.VK_V ||
                         vkCode == NativeMethods.VK_CONTROL))
                    {
                        _vKeyActive = false;
                    }
                }
            }
        }
        catch
        {
            // Never crash the global keyboard hook.
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
        }
    }
}
