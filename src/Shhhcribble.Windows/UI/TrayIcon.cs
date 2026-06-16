using System.Drawing;
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
    private string _status = "Starting…";

    public event Action? SettingsClicked;
    public event Action? QuitClicked;
    public event Action<string>? ActivationModeSelected;
    public event Action<string>? ModelSelected;
    public event Action<bool>? PauseMusicToggled;
    public event Action<string>? HistoryItemClicked;

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Shhhcribble",
        };
        Rebuild();
    }

    public void SetStatus(string status)
    {
        _status = status;
        SetTooltip(status);
        Rebuild();
    }

    public void SetTooltip(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Rebuild()
    {
        var s = SettingsStore.Current;
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem(_status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        // Recent transcriptions → click copies to clipboard.
        var recent = new ToolStripMenuItem("Recent Transcriptions");
        if (s.History.Count == 0)
            recent.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
        else
            foreach (var item in s.History)
            {
                var title = item.Text.Length > 60 ? item.Text[..60] + "…" : item.Text;
                var mi = new ToolStripMenuItem(title);
                var text = item.Text;
                mi.Click += (_, _) => HistoryItemClicked?.Invoke(text);
                recent.DropDownItems.Add(mi);
            }
        menu.Items.Add(recent);

        // Activation mode
        var activation = new ToolStripMenuItem("Activation");
        foreach (var (id, label) in new[] { ("hybrid", "Hybrid (tap = toggle, hold = push)"),
                                            ("toggle", "Toggle"), ("pushToTalk", "Push-to-talk") })
        {
            var mi = new ToolStripMenuItem(label) { Checked = s.ActivationMode == id };
            mi.Click += (_, _) => ActivationModeSelected?.Invoke(id);
            activation.DropDownItems.Add(mi);
        }
        menu.Items.Add(activation);

        // Model
        var model = new ToolStripMenuItem("Model");
        foreach (var m in ParakeetModels.All)
        {
            var mi = new ToolStripMenuItem(m.DisplayName) { Checked = s.ModelId == m.Id };
            var id = m.Id;
            mi.Click += (_, _) => ModelSelected?.Invoke(id);
            model.DropDownItems.Add(mi);
        }
        menu.Items.Add(model);

        // Pause music toggle
        var pause = new ToolStripMenuItem("Pause music while recording") { Checked = s.PauseMusicEnabled };
        pause.Click += (_, _) => PauseMusicToggled?.Invoke(!s.PauseMusicEnabled);
        menu.Items.Add(pause);

        menu.Items.Add(new ToolStripSeparator());

        var hotkeyLabel = HotkeyOptions.ById(s.HotkeyId).Label;
        menu.Items.Add(new ToolStripMenuItem($"Hotkey: {hotkeyLabel}") { Enabled = false });

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsClicked?.Invoke();
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit Shhhcribble");
        quit.Click += (_, _) => QuitClicked?.Invoke();
        menu.Items.Add(quit);

        _icon.ContextMenuStrip?.Dispose();
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
