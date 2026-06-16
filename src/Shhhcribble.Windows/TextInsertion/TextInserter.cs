using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard; // disambiguate from System.Windows.Forms.Clipboard

namespace Shhhcribble.Windows.TextInsertion;

/// <summary>
/// Inserts transcribed text into whatever app currently has keyboard focus, then
/// restores the user's prior clipboard — the Windows analogue of the macOS
/// TextInserter.
///
/// On macOS the primary path is an Accessibility "set selected text" insert. On
/// Windows there is no reliable universal "insert at caret" API (UIA's
/// ValuePattern.SetValue replaces the entire field, which would clobber existing
/// text), so we use the clipboard + simulated Ctrl+V — the same path the macOS
/// app falls back to, which already handles the vast majority of apps.
///
/// Clipboard save/restore mirrors macOS: snapshot → write transcription → paste
/// → after a 2 s window (long enough for the paste to land and for the user to
/// manually Ctrl+V if a host swallowed the event) restore the prior clipboard,
/// but only if it hasn't changed in the meantime.
/// </summary>
public sealed class TextInserter
{
    private readonly Dispatcher _dispatcher;

    public TextInserter(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        _dispatcher.Invoke(() =>
        {
            string? previous = SafeGetClipboardText();

            TrySetClipboardText(text);
            SendCtrlV();

            // Restore the prior clipboard after 2 s, but only if our text is still
            // there (i.e. the user didn't copy something new in the meantime).
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (SafeGetClipboardText() == text)
                {
                    if (previous != null) TrySetClipboardText(previous);
                    else SafeClearClipboard();
                }
            };
            timer.Start();
        });
    }

    private static string? SafeGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    private static void TrySetClipboardText(string text)
    {
        // The clipboard is occasionally locked by another process; retry briefly.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch { System.Threading.Thread.Sleep(20); }
        }
    }

    private static void SafeClearClipboard()
    {
        try { Clipboard.Clear(); } catch { /* ignore */ }
    }

    // ---- Ctrl+V via SendInput ----

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint INPUT_KEYBOARD = 1;

    private static void SendCtrlV()
    {
        INPUT[] inputs =
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
