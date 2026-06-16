using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Shhhcribble.Core.Transcription;
using Shhhcribble.Windows.Input;

namespace Shhhcribble.Windows.UI;

/// <summary>
/// The menu-bar/tray presence — Windows analogue of the macOS MenuBarController
/// (NSStatusItem). Uses the WinForms <see cref="NotifyIcon"/> (stable, no extra
/// dependency) and rebuilds its context menu from current settings + status.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private string _status = "Model not loaded";
    private bool _isReady;
    private bool _isRecording;

    // ---- Events ------------------------------------------------------------

    public event Action? SettingsClicked;
    public event Action? QuitClicked;
    public event Action? ClearHistoryClicked;
    public event Action<string>? ActivationModeSelected;
    public event Action<string>? ModelSelected;
    public event Action<bool>? PauseMusicToggled;
    public event Action<string>? HistoryItemClicked;

    // ---- Icons (generated in code — no bundled .ico required) ---------------

    // 16×16 microphone glyph drawn from simple rectangles/ellipses.
    private static Icon CreateMicIcon(Color color)
    {
        const int sz = 16;
        using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var b = new SolidBrush(color);
        // Mic capsule body: 5px wide, 7px tall, centered at (8,6), rounded
        g.FillEllipse(b, 5, 1, 6, 7);   // top dome
        g.FillRectangle(b, 5, 4, 6, 5); // body

        // Stand arc (two small rects simulating an arc base)
        using var pen = new Pen(color, 1.5f);
        g.DrawArc(pen, 3, 5, 10, 8, 0, 180);  // arc over stand

        // Vertical stand
        g.FillRectangle(b, 7, 12, 2, 2);
        // Horizontal base
        g.FillRectangle(b, 5, 13, 6, 1);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static readonly Icon _idleIcon      = CreateMicIcon(Color.FromArgb(220, 220, 220));
    private static readonly Icon _recordingIcon = CreateMicIcon(Color.FromArgb(220, 50,  50));

    // ---- Construction ------------------------------------------------------

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Icon    = _idleIcon,
            Visible = true,
            Text    = "Shhhcribble",
        };
        Rebuild();
    }

    // ---- Public API --------------------------------------------------------

    public void SetStatus(string status)
    {
        _status = status;
        Rebuild();
    }

    /// <summary>
    /// Call once the model is fully loaded and recording is available so the
    /// tray can switch from the plain status line to the compact usage hint.
    /// </summary>
    public void SetReady(bool ready)
    {
        _isReady = ready;
        Rebuild();
    }

    public void SetTooltip(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    /// <summary>
    /// Swaps the tray icon to a red variant while recording and reverts to
    /// the idle mic when not recording — mirrors macOS <c>setRecordingIndicator(active:)</c>.
    /// </summary>
    public void SetRecordingIndicator(bool active)
    {
        _isRecording = active;
        _icon.Icon   = active ? _recordingIcon : _idleIcon;
        // Keep tooltip constant per macOS parity; status lives in the menu.
        _icon.Text   = "Shhhcribble";
    }

    public void Rebuild()
    {
        var s    = SettingsStore.Current;
        var menu = new ContextMenuStrip();

        // ── Disabled "Shhhcribble" app-name header (macOS parity) ────────────
        menu.Items.Add(new ToolStripMenuItem("Shhhcribble") { Enabled = false });

        // ── Engine status (always shown) ─────────────────────────────────────
        menu.Items.Add(new ToolStripMenuItem(_status) { Enabled = false });

        // ── Usage hint — only when ready (replaces the "Hotkey: {label}" row) ─
        if (_isReady)
        {
            var symbol   = HotkeyOptions.ById(s.HotkeyId).Symbol;
            var hintText = $"Tap {symbol} to start · tap again · or hold & release";
            var hint     = new ToolStripMenuItem(hintText) { Enabled = false };
            // Render at ~11 pt secondary style — approximate via a slightly smaller font.
            hint.Font        = new Font(SystemFonts.MenuFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 8.5f);
            hint.ForeColor   = SystemColors.GrayText;
            menu.Items.Add(hint);
        }

        menu.Items.Add(new ToolStripSeparator());

        // ── Recent Transcriptions — only when non-empty ───────────────────────
        if (s.History.Count > 0)
        {
            var recent = new ToolStripMenuItem("Recent Transcriptions");

            // Inner title (bold, 13 pt)
            var titleItem = new ToolStripMenuItem("Recent Transcriptions")
            {
                Enabled  = false,
                Font     = new Font(SystemFonts.MenuFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily,
                                    9.75f, FontStyle.Bold),
            };
            recent.DropDownItems.Add(titleItem);

            // Description (secondary, smaller)
            var desc = new ToolStripMenuItem("Click any item to copy & paste it.")
            {
                Enabled   = false,
                ForeColor = SystemColors.GrayText,
                Font      = new Font(SystemFonts.MenuFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 8.5f),
            };
            recent.DropDownItems.Add(desc);

            // Separator after title block, before entries
            recent.DropDownItems.Add(new ToolStripSeparator());

            // Per-entry items with clipboard glyph
            var clipIcon = CreateClipboardIcon();
            foreach (var item in s.History)
            {
                var displayTitle = item.Text.Length > 60 ? item.Text[..60] + "…" : item.Text;
                var mi           = new ToolStripMenuItem(displayTitle) { Image = clipIcon };
                var capturedText = item.Text;
                mi.Click += (_, _) => HistoryItemClicked?.Invoke(capturedText);
                recent.DropDownItems.Add(mi);
            }

            // Trailing separator then Clear History
            recent.DropDownItems.Add(new ToolStripSeparator());
            var clear = new ToolStripMenuItem("Clear History");
            clear.Click += (_, _) => ClearHistoryClicked?.Invoke();
            recent.DropDownItems.Add(clear);

            menu.Items.Add(recent);
            menu.Items.Add(new ToolStripSeparator());
        }

        // ── Activation mode submenu (intentional Windows feature — keep) ─────
        var activation = new ToolStripMenuItem("Activation");
        foreach (var (id, label) in new[]
        {
            ("hybrid",     "Hybrid (tap = toggle, hold = push)"),
            ("toggle",     "Toggle"),
            ("pushToTalk", "Push-to-talk"),
        })
        {
            var mi = new ToolStripMenuItem(label) { Checked = s.ActivationMode == id };
            var captured = id;
            mi.Click += (_, _) => ActivationModeSelected?.Invoke(captured);
            activation.DropDownItems.Add(mi);
        }
        menu.Items.Add(activation);

        // ── Model submenu (intentional Windows feature — keep) ────────────────
        var model = new ToolStripMenuItem("Model");
        foreach (var m in ParakeetModels.All)
        {
            var mi = new ToolStripMenuItem(m.DisplayName) { Checked = s.ModelId == m.Id };
            var capturedId = m.Id;
            mi.Click += (_, _) => ModelSelected?.Invoke(capturedId);
            model.DropDownItems.Add(mi);
        }
        menu.Items.Add(model);

        // ── Pause music toggle (intentional Windows feature — keep) ───────────
        var pause = new ToolStripMenuItem("Pause music while recording") { Checked = s.PauseMusicEnabled };
        pause.Click += (_, _) => PauseMusicToggled?.Invoke(!s.PauseMusicEnabled);
        menu.Items.Add(pause);

        menu.Items.Add(new ToolStripSeparator());

        // Settings (Ctrl+, shortcut mirrors macOS Cmd+,)
        var settings = new ToolStripMenuItem("Settings…")
        {
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
        };
        settings.Click += (_, _) => SettingsClicked?.Invoke();
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        // Quit (Ctrl+Q mirrors macOS Cmd+Q)
        var quit = new ToolStripMenuItem("Quit Shhhcribble")
        {
            ShortcutKeys = Keys.Control | Keys.Q,
        };
        quit.Click += (_, _) => QuitClicked?.Invoke();
        menu.Items.Add(quit);

        _icon.ContextMenuStrip?.Dispose();
        _icon.ContextMenuStrip = menu;
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>
    /// Generates a small 16×16 clipboard icon in code so no bundled resource is
    /// needed.  Matches the macOS "doc.on.clipboard" per-entry glyph intent.
    /// </summary>
    private static Bitmap CreateClipboardIcon()
    {
        const int sz = 16;
        var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Clipboard board body
        using var bg = new SolidBrush(Color.FromArgb(200, 200, 200));
        g.FillRectangle(bg, 2, 3, 12, 12);

        // Clip at top
        using var clip = new SolidBrush(Color.FromArgb(150, 150, 150));
        g.FillRectangle(clip, 5, 1, 6, 4);

        // Lines on clipboard
        using var line = new Pen(Color.FromArgb(120, 120, 120), 1f);
        g.DrawLine(line, 4,  7, 12,  7);
        g.DrawLine(line, 4, 10, 12, 10);
        g.DrawLine(line, 4, 13, 10, 13);

        return bmp;
    }

    // ---- IDisposable -------------------------------------------------------

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
