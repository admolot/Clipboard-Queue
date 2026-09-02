using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipboardQueue;

internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private readonly IntPtr _hookId;

    private bool _ctrlVHandled;
    private bool _ctrlAltVHandled;
    private bool _vKeyActive;
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

                    // Ctrl+V first press = a paste gesture; auto-repeats are
                    // passed through untouched so the target app repeats the
                    // current clipboard natively (held Ctrl+V = "111111...").
                    if (vkCode == NativeMethods.VK_V && ctrlDown && !keyUp)
                    {
                        bool isRepeat = _vKeyActive;
                        _vKeyActive = true;

                        if (isRepeat)
                        {
                            InputActivity.Note();
                            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                        }

                        InputActivity.NoteGesture();
                    }
                    else if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN ||
                             wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
                    {
                        InputActivity.Note();
                    }

                    if (vkCode == NativeMethods.VK_V)
                    {
                        bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
                        bool leftMouseDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

                        if (ctrlDown)
                        {
                            if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN ||
                                wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
                            {
                                // Ctrl+Alt+V -> paste all
                                // Ctrl+V while holding LEFT mouse button -> paste all
                                // Ctrl+V -> paste next
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
                                    if (ShouldHandleCtrlV?.Invoke() == true)
                                    {
                                        if (!_ctrlVHandled)
                                        {
                                            _ctrlVHandled = true;
                                            CtrlVPressed?.Invoke();
                                        }

                                        return (IntPtr)1;
                                    }
                                }
                            }
                        }
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
