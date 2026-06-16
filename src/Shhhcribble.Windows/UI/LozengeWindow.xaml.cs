using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
// Disambiguate WPF types from the System.Drawing globals pulled in by WinForms.
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;

namespace Shhhcribble.Windows.UI;

/// <summary>
/// The floating "soundwave" lozenge — a borderless, click-through, non-activating
/// pill near the bottom of the screen. Windows analogue of the macOS
/// SoundwavePanel/SoundwaveView. State transitions and auto-hide dwell times
/// mirror the macOS app (copied/no-result ~1 s, error ~1.6 s).
/// </summary>
public partial class LozengeWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new();
    // 50 ms cadence + phase step 0.35 mirror the macOS SoundwaveBars timer.
    private readonly DispatcherTimer _waveTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<Rectangle> _bars = new();
    private double _phase;

    /// <summary>Latest mic level in [0,1], fed from the recorder so the bars
    /// react to real audio (matches macOS). Until that's wired, a mid-level
    /// default keeps the wave visibly alive.</summary>
    public double AudioLevel { get; set; } = 0.5;

    public LozengeWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        _waveTimer.Tick += (_, _) => AnimateBars();
        BuildBars();
    }

    private void BuildBars()
    {
        const int count = 7;
        const double w = 4, gap = 4;
        for (int i = 0; i < count; i++)
        {
            var bar = new Rectangle
            {
                Width = w,
                Height = 6,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x6E, 0x9F, 0xFF)),
            };
            Canvas.SetLeft(bar, i * (w + gap));
            Waves.Children.Add(bar);
            _bars.Add(bar);
        }
    }

    private void AnimateBars()
    {
        // Flowing sine wave swelling with the mic level — the macOS SoundwaveBars
        // formula: height = 4 + level * 32 * (0.65 + 0.35*sin(phase + i*0.75)).
        // Scaled to this 22 px-tall canvas.
        _phase += 0.35;
        double level = Math.Max(AudioLevel, 0.05);
        for (int i = 0; i < _bars.Count; i++)
        {
            double normalised = 0.65 + 0.35 * Math.Sin(_phase + i * 0.75);
            double h = 4 + level * 20 * normalised;
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (22 - h) / 2);
        }
    }

    // ---- State presenters ----

    public void ShowRecording()
    {
        Label.Text = "Listening…";
        SetBarsVisible(true);
        _waveTimer.Start();
        _hideTimer.Stop();
        Present();
    }

    public void ShowCopied()
    {
        Label.Text = "Copied! Ctrl+V to paste";
        StopWaves();
        Present();
        AutoHide(TimeSpan.FromSeconds(1.0));
    }

    public void ShowNoResult()
    {
        Label.Text = "No speech detected";
        StopWaves();
        Present();
        AutoHide(TimeSpan.FromSeconds(1.0));
    }

    public void ShowError(string message)
    {
        Label.Text = message;
        StopWaves();
        Present();
        AutoHide(TimeSpan.FromSeconds(1.6));
    }

    public void Cancel()
    {
        StopWaves();
        _hideTimer.Stop();
        Hide();
    }

    private void SetBarsVisible(bool visible)
    {
        Waves.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopWaves()
    {
        _waveTimer.Stop();
        SetBarsVisible(false);
    }

    private void AutoHide(TimeSpan after)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = after;
        _hideTimer.Start();
    }

    private void Present()
    {
        if (!IsVisible) Show();
        Reposition();
    }

    private void Reposition()
    {
        // Recompute after content has measured.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Top-center, ~8 px below the top of the work area — matches the
            // macOS SoundwavePanel.positionAtTopCenter().
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + 8;
        });
    }

    // ---- Non-activating, click-through, tool window ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
