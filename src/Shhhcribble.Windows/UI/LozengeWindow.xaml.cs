using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
// Disambiguate WPF types from the System.Drawing globals pulled in by WinForms.
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Shhhcribble.Windows.UI;

/// <summary>
/// The floating "soundwave" lozenge — a borderless, click-through, non-activating
/// pill near the bottom of the screen. Windows analogue of the macOS
/// SoundwavePanel/SoundwaveView. State transitions, auto-hide dwell times, entry/exit
/// spring animations, and all visual values mirror the macOS app exactly.
///
/// State auto-hide: copied/noResult = 1.0 s, error = 1.6 s.
/// Entry spring: scale 0.78→1.0, Y -18→0, opacity 0→1 (~0.25 s, overshoot easing).
/// Exit spring:  scale→0.78, Y→-18, opacity→0 (~0.22 s), Hide() after 0.3 s.
/// </summary>
public partial class LozengeWindow : Window
{
    // ---- Timers ----
    private readonly DispatcherTimer _hideTimer  = new();
    private readonly DispatcherTimer _waveTimer  = new() { Interval = TimeSpan.FromMilliseconds(50) };

    // ---- Bar state ----
    private readonly List<Rectangle> _bars = new();
    private double _phase;
    private bool _barsRunning;

    // ---- Dot state ----
    private bool _dotAnimating;
    private readonly DispatcherTimer _shimmerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _shimmerX = -0.6;

    // ---- Lozenge visual state ----
    private enum LozengeState { Hidden, Recording, Transcribing, Copied, NoResult, Error }
    private LozengeState _state = LozengeState.Hidden;

    // ---- Live-text typing ----
    private string _typingTarget = "";
    private string _typingDisplayed = "";
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };

    // ---- Pending-hide cancellation token ----
    private CancellationTokenSource? _hideCts;

    /// <summary>Latest mic level in [0,1], fed from MicRecorder.LevelChanged so the
    /// bars react to real audio. Gain 20.0×, clamped 1.0 — matches macOS formula.</summary>
    public double AudioLevel { get; set; } = 0.0;

    // ---- Per-state dot colors (exact macOS RGBs) ----
    private static readonly Color RecordingDotColor    = Color.FromRgb(0x40, 0x8C, 0xFF);  // RGB(0.25,0.55,1.0)
    private static readonly Color TranscribingDotColor = Color.FromRgb(0xA6, 0x80, 0xFF); // RGB(0.65,0.50,1.0) violet
    private static readonly Color CopiedDotColor       = Color.FromRgb(0x30, 0xD1, 0x58); // system green
    private static readonly Color NoResultDotColor     = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF); // white@0.30
    private static readonly Color ErrorDotColor        = Color.FromRgb(0xFF, 0x73, 0x73);  // RGB(1.0,0.45,0.45) ≈ #FF7373

    // ---- Per-state border/halo colors (@28%/30%) ----
    private static readonly Color RecordingHaloColor    = Color.FromArgb(0x47, 0x40, 0x8C, 0xFF);
    private static readonly Color TranscribingHaloColor = Color.FromArgb(0x47, 0xA6, 0x80, 0xFF);
    private static readonly Color CopiedHaloColor       = Color.FromArgb(0x47, 0x30, 0xD1, 0x58);
    private static readonly Color NoResultHaloColor     = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF);
    private static readonly Color ErrorHaloColor        = Color.FromArgb(0x47, 0xFF, 0x73, 0x73);
    private static readonly Color DefaultHaloColor      = Color.FromArgb(0x47, 0x40, 0x40, 0x80);

    // ---- Segoe MDL2 Assets glyph codes ----
    private const string GlyphMic         = ""; // microphone
    private const string GlyphCheckmark   = ""; // checkmark circle
    private const string GlyphWaveSlash   = ""; // waveform slash (closest MDL2 equivalent)
    private const string GlyphWarning     = ""; // exclamationmark triangle

    public LozengeWindow()
    {
        InitializeComponent();

        _hideTimer.Tick   += (_, _) => { _hideTimer.Stop(); AnimateExit(); };
        _waveTimer.Tick   += (_, _) => AnimateBars();
        _shimmerTimer.Tick += (_, _) => AdvanceShimmer();
        _typingTimer.Tick  += (_, _) => AdvanceTyping();

        BuildBars();
        BuildDot();

        // Start hidden (collapsed) — entry animation runs on first Present()
        Opacity = 0;
        PillScale.ScaleX = 0.78;
        PillScale.ScaleY = 0.78;
        PillTranslate.Y  = -18;
    }

    // =========================================================================
    // PUBLIC STATE PRESENTERS
    // =========================================================================

    /// <summary>Show the recording pill: bars animate, live text area visible, no auto-hide.</summary>
    public void ShowRecording()
    {
        CancelPendingHide();
        _state = LozengeState.Recording;

        Label.Visibility           = Visibility.Collapsed;
        RecordingContent.Visibility = Visibility.Visible;
        StateIcon.Visibility        = Visibility.Visible;
        TranscribingSpinner.Visibility = Visibility.Collapsed;

        // Leading icon: mic, white@0.55
        StateIcon.Text       = GlyphMic;
        StateIcon.Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

        SetDotColor(RecordingDotColor, animate: true);
        SetHaloColor(RecordingHaloColor);
        StartBars();
        ResetLiveText();
        Present();
    }

    /// <summary>Transition to persistent "Transcribing…" state (spinner + violet dot, no auto-hide).
    /// Called right after mic.Stop(); stays until ShowCopied/ShowNoResult/ShowError replaces it.</summary>
    public void ShowTranscribing()
    {
        CancelPendingHide();
        _state = LozengeState.Transcribing;

        // Stop bars; show label
        StopBars();
        ResetLiveText();
        RecordingContent.Visibility = Visibility.Collapsed;
        Label.Visibility            = Visibility.Visible;
        Label.Text                  = "Transcribing…"; // "Transcribing…"
        Label.Foreground            = new SolidColorBrush(Colors.White);

        // Leading: spinner replaces icon while transcribing
        StateIcon.Visibility           = Visibility.Collapsed;
        TranscribingSpinner.Visibility = Visibility.Visible;

        SetDotColor(TranscribingDotColor, animate: false);
        SetHaloColor(TranscribingHaloColor);

        // No auto-hide — stays until replaced
        Present();
    }

    /// <summary>Show the "Copied!" state with 1.0 s auto-hide and 0.35 s crossfade.</summary>
    public void ShowCopied()
    {
        CancelPendingHide();
        _state = LozengeState.Copied;

        StopBars();
        ResetLiveText();
        RecordingContent.Visibility    = Visibility.Collapsed;
        TranscribingSpinner.Visibility = Visibility.Collapsed;
        Label.Visibility               = Visibility.Visible;
        Label.Text                     = "Copied! Ctrl+V to paste";
        Label.Foreground               = new SolidColorBrush(Colors.White);

        // Leading: checkmark in green
        StateIcon.Visibility = Visibility.Visible;
        StateIcon.Text       = GlyphCheckmark;
        StateIcon.Foreground = new SolidColorBrush(CopiedDotColor);

        SetDotColor(CopiedDotColor, animate: false);
        SetHaloColor(CopiedHaloColor);

        Present(crossfadeDuration: 0.35);
        AutoHide(TimeSpan.FromSeconds(1.0));
    }

    /// <summary>Show "No speech detected" with muted foreground and 1.0 s auto-hide (0.25 s crossfade).</summary>
    public void ShowNoResult()
    {
        CancelPendingHide();
        _state = LozengeState.NoResult;

        StopBars();
        ResetLiveText();
        RecordingContent.Visibility    = Visibility.Collapsed;
        TranscribingSpinner.Visibility = Visibility.Collapsed;
        Label.Visibility               = Visibility.Visible;
        Label.Text                     = "No speech detected";
        Label.Foreground               = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)); // white@0.7

        // Leading: waveform-slash at default (white@0.55)
        StateIcon.Visibility = Visibility.Visible;
        StateIcon.Text       = GlyphWaveSlash;
        StateIcon.Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

        SetDotColor(NoResultDotColor, animate: false);
        SetHaloColor(NoResultHaloColor);

        Present(crossfadeDuration: 0.25);
        AutoHide(TimeSpan.FromSeconds(1.0));
    }

    /// <summary>Show an error message in salmon-red with 1.6 s auto-hide (0.25 s crossfade).</summary>
    public void ShowError(string message)
    {
        CancelPendingHide();
        _state = LozengeState.Error;

        StopBars();
        ResetLiveText();
        RecordingContent.Visibility    = Visibility.Collapsed;
        TranscribingSpinner.Visibility = Visibility.Collapsed;
        Label.Visibility               = Visibility.Visible;
        Label.Text                     = message;
        Label.Foreground               = new SolidColorBrush(ErrorDotColor); // #FFFF7373

        // Leading: warning triangle in salmon
        StateIcon.Visibility = Visibility.Visible;
        StateIcon.Text       = GlyphWarning;
        StateIcon.Foreground = new SolidColorBrush(ErrorDotColor);

        SetDotColor(ErrorDotColor, animate: false);
        SetHaloColor(ErrorHaloColor);

        Present(crossfadeDuration: 0.25);
        AutoHide(TimeSpan.FromSeconds(1.6));
    }

    /// <summary>Cancel recording and immediately hide without any auto-hide dwell.</summary>
    public void Cancel()
    {
        CancelPendingHide();
        StopBars();
        ResetLiveText();
        _state = LozengeState.Hidden;
        AnimateExit();
    }

    /// <summary>Update live transcript text with 70 ms/char typing + longest-common-prefix rewind.</summary>
    public void UpdateLiveText(string text)
    {
        if (_state != LozengeState.Recording) return;

        // Longest-common-prefix rewind: if new text doesn't extend displayed, rewind to common prefix
        if (!text.StartsWith(_typingDisplayed, StringComparison.Ordinal))
        {
            int commonLen = 0;
            int limit = Math.Min(_typingDisplayed.Length, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (_typingDisplayed[i] == text[i]) commonLen = i + 1;
                else break;
            }
            _typingDisplayed = _typingDisplayed[..commonLen];
            SetLiveTextDisplay(_typingDisplayed);
        }

        _typingTarget = text;
        if (!_typingTimer.IsEnabled && _typingDisplayed.Length < _typingTarget.Length)
            _typingTimer.Start();
    }

    // =========================================================================
    // PRIVATE: Bar building and animation
    // =========================================================================

    private void BuildBars()
    {
        const int count = 7;
        const double w = 3.0, gap = 3.0;
        var fill = new SolidColorBrush(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF)); // white@0.75

        for (int i = 0; i < count; i++)
        {
            var bar = new Rectangle
            {
                Width    = w,
                Height   = 4, // initial height = 4 (matches macOS Array(repeating:4))
                RadiusX  = 2,
                RadiusY  = 2,
                Fill     = fill,
            };
            Canvas.SetLeft(bar, i * (w + gap));
            Waves.Children.Add(bar);
            _bars.Add(bar);
        }
    }

    private void StartBars()
    {
        SetBarsVisible(true);
        if (!_barsRunning)
        {
            _barsRunning = true;
            _waveTimer.Start();
        }
    }

    private void StopBars()
    {
        _waveTimer.Stop();
        _barsRunning = false;
        SetBarsVisible(false);
        // Reset all bars to initial height
        foreach (var bar in _bars) bar.Height = 4;
        _phase = 0;
    }

    private void AnimateBars()
    {
        // macOS SoundwaveBars formula: height = 4 + level * 32 * (0.65 + 0.35*sin(phase + i*0.75))
        // Canvas height = 36px; amplitude 32 restored.
        _phase += 0.35;
        double level = Math.Max(AudioLevel, 0.05);
        for (int i = 0; i < _bars.Count; i++)
        {
            double normalised = 0.65 + 0.35 * Math.Sin(_phase + i * 0.75);
            double h = 4 + level * 32 * normalised;
            // 50 ms DoubleAnimation ease per bar (matches macOS .easeInOut(duration:0.05))
            AnimateBarHeight(_bars[i], h);
            Canvas.SetTop(_bars[i], (36 - h) / 2);
        }
    }

    private void AnimateBarHeight(Rectangle bar, double targetHeight)
    {
        var anim = new DoubleAnimation(targetHeight, new Duration(TimeSpan.FromMilliseconds(50)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }

    private void SetBarsVisible(bool visible)
    {
        Waves.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // =========================================================================
    // PRIVATE: AnimatedDot (4-layer: glow, bezel, lit core, shimmer)
    // =========================================================================

    // Dot layer references — built programmatically and placed in DotCanvas
    private Ellipse? _dotGlow;    // Layer 1: outer glow (recording only)
    private Ellipse? _dotBezel;   // Layer 2: dark housing/bezel
    private Ellipse? _dotCore;    // Layer 3: lit core (RadialGradient)
    private Ellipse? _dotShimmer; // Layer 4: shimmer streak (recording only)

    private void BuildDot()
    {
        // Layer 1: outer glow circle (20x20, blurred, recording-only)
        _dotGlow = new Ellipse { Width = 20, Height = 20, Opacity = 0 };
        var glowEffect = new BlurEffect { Radius = 4 };
        _dotGlow.Effect = glowEffect;
        CenterInDotCanvas(_dotGlow, 20);
        DotCanvas.Children.Add(_dotGlow);

        // Layer 2: dark bezel (14x14, white@0.10 fill, white@0.22 stroke 0.5px)
        _dotBezel = new Ellipse
        {
            Width = 14, Height = 14,
            Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), // white@0.10
            Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), // white@0.22
            StrokeThickness = 0.5
        };
        CenterInDotCanvas(_dotBezel, 14);
        DotCanvas.Children.Add(_dotBezel);

        // Layer 3: lit core (9x9, RadialGradient center 0.35,0.28 endRadius 5)
        _dotCore = new Ellipse { Width = 9, Height = 9 };
        UpdateDotCoreGradient(RecordingDotColor);
        CenterInDotCanvas(_dotCore, 9);
        DotCanvas.Children.Add(_dotCore);

        // Layer 4: shimmer streak (9x9, LinearGradient, recording-only)
        _dotShimmer = new Ellipse { Width = 9, Height = 9, Opacity = 0 };
        _shimmerX = -0.6;
        UpdateShimmerGradient();
        CenterInDotCanvas(_dotShimmer, 9);
        DotCanvas.Children.Add(_dotShimmer);
    }

    private static void CenterInDotCanvas(FrameworkElement el, double size)
    {
        Canvas.SetLeft(el, (22 - size) / 2);
        Canvas.SetTop(el, (22 - size) / 2);
    }

    private void SetDotColor(Color color, bool animate)
    {
        // Crossfade color in 0.4 s easeOut (animate transitions; instant on init)
        double duration = animate ? 0.4 : 0.0;

        // Glow: animate opacity (recording = 1.0, others = 0.0)
        double glowOpacity = animate && color == RecordingDotColor ? 1.0 : 0.0;
        SetGlowColor(color, glowOpacity, duration);

        // Core: update gradient fill
        UpdateDotCoreGradient(color);

        // Shimmer: only animate during recording
        bool shouldShimmer = (color == RecordingDotColor);
        if (shouldShimmer != _dotAnimating)
        {
            _dotAnimating = shouldShimmer;
            if (shouldShimmer)
            {
                _shimmerX = -0.6;
                _dotShimmer!.Opacity = 1.0;
                _shimmerTimer.Start();
            }
            else
            {
                _shimmerTimer.Stop();
                _dotShimmer!.Opacity = 0;
            }
        }
    }

    private void SetGlowColor(Color color, double opacity, double durationSeconds)
    {
        if (_dotGlow == null) return;
        _dotGlow.Fill = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)); // color@0.22
        if (durationSeconds > 0)
        {
            var anim = new DoubleAnimation(opacity, new Duration(TimeSpan.FromSeconds(durationSeconds)))
            {
                EasingFunction = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 2 }
            };
            _dotGlow.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            _dotGlow.Opacity = opacity;
        }
    }

    private void UpdateDotCoreGradient(Color color)
    {
        if (_dotCore == null) return;
        // RadialGradient: [white@0.85, color, color@0.35], center (0.35,0.28), endRadius 5 in 9px = 0.556 relative
        _dotCore.Fill = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF), 0.0), // white@0.85 at center
                new GradientStop(color, 0.5),
                new GradientStop(Color.FromArgb(0x59, color.R, color.G, color.B), 1.0) // color@0.35
            })
        {
            GradientOrigin = new Point(0.35, 0.28),
            Center         = new Point(0.35, 0.28),
            RadiusX        = 0.556,
            RadiusY        = 0.556,
        };
    }

    private void AdvanceShimmer()
    {
        // Linear 2.8 s sweep from -0.6 → 1.6, repeat forever
        _shimmerX += (2.2) / (2.8 * 62.5); // 2.8s at ~62.5 ticks/s (16ms timer)
        if (_shimmerX > 1.6) _shimmerX = -0.6;
        UpdateShimmerGradient();
    }

    private void UpdateShimmerGradient()
    {
        if (_dotShimmer == null) return;
        double x = _shimmerX;
        _dotShimmer.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent,                    0.0),
                new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.5), // white@0.50
                new GradientStop(Colors.Transparent,                    1.0),
            },
            startPoint: new Point(Math.Max(0, x - 0.4), 0.1),
            endPoint:   new Point(Math.Min(1, x + 0.4), 0.9)
        );
    }

    private void SetHaloColor(Color color)
    {
        // Animate halo border color change in 0.4 s
        var brush = new SolidColorBrush(color);
        Pill.BorderBrush = brush;
    }

    // =========================================================================
    // PRIVATE: Live-text typing (70 ms/char)
    // =========================================================================

    private void ResetLiveText()
    {
        _typingTimer.Stop();
        _typingTarget    = "";
        _typingDisplayed = "";
        SetLiveTextDisplay("");
    }

    private void AdvanceTyping()
    {
        if (_typingDisplayed.Length >= _typingTarget.Length)
        {
            _typingTimer.Stop();
            return;
        }
        int nextLen = _typingDisplayed.Length + 1;
        _typingDisplayed = _typingTarget[..nextLen];
        SetLiveTextDisplay(_typingDisplayed);
    }

    private void SetLiveTextDisplay(string text)
    {
        // Render with just-completed-word highlight (white@0.85 for 2nd-to-last token, white@0.40 rest)
        // Use an Inlines-based approach via a helper
        LiveText.Inlines.Clear();
        if (string.IsNullOrEmpty(text))
        {
            LiveText.Inlines.Add(new System.Windows.Documents.Run(" ")
            {
                Foreground = new SolidColorBrush(Colors.Transparent)
            });
            return;
        }

        var dim = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)); // white@0.40
        var lit = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)); // white@0.85

        int lastSpace = text.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            // Only one token so far — render dim
            LiveText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = dim });
            return;
        }

        string beforeLastSpace = text[..lastSpace];
        int secondLastSpace = beforeLastSpace.LastIndexOf(' ');

        if (secondLastSpace < 0)
        {
            // Exactly two tokens
            LiveText.Inlines.Add(new System.Windows.Documents.Run(beforeLastSpace) { Foreground = lit });
            LiveText.Inlines.Add(new System.Windows.Documents.Run(text[lastSpace..]) { Foreground = dim });
        }
        else
        {
            // Three or more tokens
            string head        = text[..(secondLastSpace + 1)];
            string highlighted = text[(secondLastSpace + 1)..lastSpace];
            string tail        = text[lastSpace..];
            LiveText.Inlines.Add(new System.Windows.Documents.Run(head)        { Foreground = dim });
            LiveText.Inlines.Add(new System.Windows.Documents.Run(highlighted) { Foreground = lit });
            LiveText.Inlines.Add(new System.Windows.Documents.Run(tail)        { Foreground = dim });
        }
    }

    // =========================================================================
    // PRIVATE: Present / AutoHide / Entry+Exit animations
    // =========================================================================

    private void Present(double crossfadeDuration = 0.0)
    {
        if (!IsVisible)
        {
            // Start collapsed so spring has somewhere to animate from
            Opacity          = 0;
            PillScale.ScaleX = 0.78;
            PillScale.ScaleY = 0.78;
            PillTranslate.Y  = -18;

            Show();
            Reposition();

            // Kick entry animation on next tick (gives WPF time to render the collapsed state first)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnimateEntry());
        }
        else
        {
            Reposition();
            if (crossfadeDuration > 0)
            {
                // Crossfade: gentle opacity pulse for state change while visible
                var anim = new DoubleAnimation(1.0, new Duration(TimeSpan.FromSeconds(crossfadeDuration)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                BeginAnimation(OpacityProperty, anim);
            }
        }
    }

    private void AnimateEntry()
    {
        // Spring entry: scale 0.78→1.0, Y -18→0, opacity 0→1, ~0.25 s with overshoot (BackEase)
        var duration      = new Duration(TimeSpan.FromSeconds(0.25));
        var opacDuration  = new Duration(TimeSpan.FromSeconds(0.15));
        var overshootEase = new BackEase  { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        var easeOut       = new CubicEase { EasingMode = EasingMode.EaseOut };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.0, duration) { EasingFunction = overshootEase });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.0, duration) { EasingFunction = overshootEase });
        PillTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0.0, duration) { EasingFunction = overshootEase });
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, opacDuration) { EasingFunction = easeOut });
    }

    private void AnimateExit()
    {
        // Exit spring: scale→0.78, Y→-18, opacity→0, ~0.22 s; then Hide() after 0.3 s
        var duration = new Duration(TimeSpan.FromSeconds(0.22));
        var easeIn   = new CubicEase { EasingMode = EasingMode.EaseIn };

        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.78, duration) { EasingFunction = easeIn });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.78, duration) { EasingFunction = easeIn });
        PillTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-18.0, duration) { EasingFunction = easeIn });
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, duration) { EasingFunction = easeIn });

        // Hide and reset state after 0.3 s (animation has settled).
        // Use _hideCts so CancelPendingHide() can abort this if a new state arrives first.
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        _ = HideAfterDelayAsync(TimeSpan.FromSeconds(0.3), _hideCts.Token);
    }

    private async Task HideAfterDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    Hide();
                    // Reset visual state for next entry
                    _state = LozengeState.Hidden;
                    ResetLiveText();
                    AudioLevel = 0;
                    foreach (var bar in _bars) bar.Height = 4;
                    _phase = 0;
                }
            });
        }
        catch (TaskCanceledException) { }
    }

    private void AutoHide(TimeSpan after)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = after;
        _hideTimer.Start();
    }

    private void CancelPendingHide()
    {
        _hideTimer.Stop();
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
    }

    private void Reposition()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Top-center, ~8 px below the top of the work area — matches macOS positionAtTopCenter().
            // The 400x136 window is oversized; center it so the 320x56 pill appears top-center.
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width  - ActualWidth)  / 2;
            Top  = area.Top  + 8;
        });
    }

    // =========================================================================
    // PRIVATE: Non-activating, click-through, tool window
    // =========================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
