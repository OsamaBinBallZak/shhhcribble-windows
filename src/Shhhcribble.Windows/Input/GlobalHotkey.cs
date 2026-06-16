using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shhhcribble.Windows.Input;

/// <summary>
/// System-wide hotkey detection via a low-level keyboard hook
/// (WH_KEYBOARD_LL). Unlike Win32 RegisterHotKey, a low-level hook delivers BOTH
/// key-down and key-up, which we need for the hybrid tap/hold gesture (hold =
/// push-to-talk, so we must see the release). Anchors on a single trigger key
/// and checks modifier state at the moment it fires.
///
/// The hook callback runs on the thread that installed the hook — install on the
/// WPF UI thread, which already pumps messages, so events arrive on the UI
/// thread and need no marshalling.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public event Action? ComboDown;
    public event Action? ComboUp;
    public event Action? EscapePressed;

    /// <summary>When true, a global Escape press is captured (and swallowed) and
    /// raises <see cref="EscapePressed"/>. AppController sets this only while
    /// recording, so Escape isn't intercepted elsewhere.</summary>
    public bool InterceptEscape { get; set; }

    private HotkeyOption _hotkey;
    private IntPtr _hookHandle;
    private readonly LowLevelKeyboardProc _proc; // kept alive against GC
    private bool _triggerDown;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    public GlobalHotkey(HotkeyOption hotkey)
    {
        _hotkey = hotkey;
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook.");
    }

    public void SetHotkey(HotkeyOption hotkey)
    {
        _hotkey = hotkey;
        _triggerDown = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is first field
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

            // Escape-to-cancel while recording.
            if (InterceptEscape && vk == VK_ESCAPE && isDown)
            {
                EscapePressed?.Invoke();
                return 1; // swallow
            }

            if (vk == (int)_hotkey.Vk)
            {
                if (isDown && ModifiersMatch())
                {
                    if (!_triggerDown)
                    {
                        _triggerDown = true;
                        ComboDown?.Invoke();
                    }
                    return 1; // swallow the trigger key so it doesn't type / system-handle
                }
                if (isUp && _triggerDown)
                {
                    _triggerDown = false;
                    ComboUp?.Invoke();
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool ModifiersMatch()
    {
        bool ctrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt   = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool win   = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        var m = _hotkey.Modifiers;
        return ctrl  == m.HasFlag(HotModifiers.Ctrl)
            && alt   == m.HasFlag(HotModifiers.Alt)
            && shift == m.HasFlag(HotModifiers.Shift)
            && win   == m.HasFlag(HotModifiers.Win);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
