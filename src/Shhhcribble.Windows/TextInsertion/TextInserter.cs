using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard; // disambiguate from System.Windows.Forms.Clipboard
using IDataObject = System.Windows.IDataObject; // disambiguate from System.Windows.Forms.IDataObject

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
/// but only if the sequence number is unchanged (i.e. the user didn't copy
/// anything new in the meantime — any format, not just text).
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
            // Snapshot the full clipboard (all formats — images, files, RTF, etc.)
            // before we overwrite it.  A null snapshot means the clipboard was empty.
            IDataObject? previous = SafeGetDataObject();

            TrySetClipboardText(text);

            // Capture the sequence number AFTER writing our text.  The Windows
            // clipboard sequence number is a monotonic counter incremented on every
            // SetClipboardData / EmptyClipboard call — any new copy by any app, in
            // any format, advances it.  This mirrors macOS pasteboard.changeCount.
            uint markerSeq = GetClipboardSequenceNumber();
            Debug.WriteLine($"[TextInserter] Paste sent. markerSeq={markerSeq}, hasPrior={previous != null}");

            SendCtrlV();

            // Restore the prior clipboard after 2 s, but only if the sequence number
            // is still at the marker (i.e. nobody has written to the clipboard since
            // we did).  This is immune to the identical-text false-positive of text
            // equality, and correctly detects re-copies of any non-text content.
            // Per macOS parity: if the clipboard was originally empty we do NOT clear
            // it — we leave our transcription in place.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                uint currentSeq = GetClipboardSequenceNumber();
                if (currentSeq == markerSeq)
                {
                    if (previous != null)
                    {
                        TrySetDataObject(previous);
                        Debug.WriteLine($"[TextInserter] Clipboard restored (seq {currentSeq} == marker).");
                    }
                    else
                    {
                        // Clipboard was empty before we wrote — leave our text in place
                        // (macOS: guard !priorItems.isEmpty else { return }).
                        Debug.WriteLine($"[TextInserter] No prior clipboard content — leaving transcription in place (seq {currentSeq} == marker).");
                    }
                }
                else
                {
                    Debug.WriteLine($"[TextInserter] Restore skipped — clipboard changed (seq {currentSeq} != marker {markerSeq}).");
                }
            };
            timer.Start();
        });
    }

    // ---- Clipboard helpers ----

    /// <summary>
    /// Returns the full multi-format clipboard data object, or null if the
    /// clipboard is empty or cannot be read.
    /// </summary>
    private static IDataObject? SafeGetDataObject()
    {
        try
        {
            // GetDataObject() returns null when the clipboard is empty.
            return Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetClipboardText(string text)
    {
        // The clipboard is occasionally locked by another process; retry briefly.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch { System.Threading.Thread.Sleep(20); }
        }
        Debug.WriteLine("[TextInserter] WARNING: Failed to set clipboard text after retries.");
    }

    /// <summary>
    /// Restores a previously snapshotted full-format data object to the clipboard.
    /// </summary>
    private static void TrySetDataObject(IDataObject data)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch { System.Threading.Thread.Sleep(20); }
        }
        Debug.WriteLine("[TextInserter] WARNING: Failed to restore clipboard data object after retries.");
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

    /// <summary>
    /// Returns a monotonically increasing counter that the system increments every
    /// time the clipboard contents change (any format).  Mirrors macOS
    /// NSPasteboard.changeCount.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
