using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Shhhcribble.Core.Text;
using Shhhcribble.Core.Transcription;
using Shhhcribble.Windows.Audio;
using Shhhcribble.Windows.Input;
using Shhhcribble.Windows.Media;
using Shhhcribble.Windows.TextInsertion;
using Shhhcribble.Windows.UI;
using Application = System.Windows.Application; // disambiguate from WinForms
using Clipboard = System.Windows.Clipboard;

namespace Shhhcribble.Windows;

/// <summary>
/// Owns every subsystem and the recording state machine — Windows analogue of
/// the macOS AppDelegate. Hotkey events arrive on the UI thread (the hook is
/// installed there); transcription runs on a worker thread; UI/paste marshal
/// back to the dispatcher.
/// </summary>
public sealed class AppController : IDisposable
{
    private enum State { Idle, Recording }

    private readonly Dispatcher _ui;
    private readonly TrayIcon _tray = new();
    private readonly MicRecorder _mic = new();
    private readonly MusicPauser _music = new();
    private readonly TextInserter _inserter;
    private readonly LozengeWindow _lozenge = new();
    private GlobalHotkey _hotkey;

    private ParakeetTranscriptionEngine? _engine;
    private volatile bool _engineReady;

    private State _state = State.Idle;

    // Hybrid-gesture bookkeeping.
    private const int TapThresholdMs = 300;
    private readonly Stopwatch _pressTimer = new();
    private bool _armed;        // a press started a recording that's awaiting tap/hold classification
    private bool _toggleActive; // recording is latched on via a tap

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        _inserter = new TextInserter(ui);
        _hotkey = new GlobalHotkey(HotkeyOptions.ById(SettingsStore.Current.HotkeyId));
    }

    public void Start()
    {
        WireTray();

        _hotkey.ComboDown += OnComboDown;
        _hotkey.ComboUp += OnComboUp;
        _hotkey.EscapePressed += () => _ui.BeginInvoke(CancelRecording);
        _hotkey.Install();

        LoadEngineAsync(SettingsStore.Current.ModelId);
    }

    // ---- Model loading ----

    private void LoadEngineAsync(string modelId)
    {
        _engineReady = false;
        var model = ParakeetModels.ById(modelId);
        _tray.SetStatus($"Loading {model.DisplayName}…");

        Task.Run(async () =>
        {
            try
            {
                var files = await ModelDownloader.EnsureAsync(model,
                    new Progress<string>(msg => _ui.BeginInvoke(() => _tray.SetTooltip(msg))));
                var engine = ParakeetTranscriptionEngine.Load(files);
                _ui.BeginInvoke(() =>
                {
                    _engine?.Dispose();
                    _engine = engine;
                    _engineReady = true;
                    _tray.SetStatus($"Ready · {model.DisplayName}");
                });
            }
            catch (Exception ex)
            {
                _ui.BeginInvoke(() =>
                {
                    _tray.SetStatus("Model failed to load");
                    _tray.ShowBalloon("Shhhcribble", $"Could not load the model: {ex.Message}");
                });
            }
        });
    }

    // ---- Hotkey → activation logic ----

    private void OnComboDown() => _ui.BeginInvoke(() =>
    {
        switch (SettingsStore.Current.ActivationMode)
        {
            case "pushToTalk":
                StartRecording();
                break;

            case "toggle":
                if (_state == State.Idle) { StartRecording(); _toggleActive = true; }
                else StopRecording();
                break;

            default: // hybrid
                if (_state == State.Idle)
                {
                    StartRecording();
                    _armed = true;
                    _pressTimer.Restart();
                }
                else if (_toggleActive)
                {
                    StopRecording(); // a second tap ends a latched recording
                }
                break;
        }
    });

    private void OnComboUp() => _ui.BeginInvoke(() =>
    {
        switch (SettingsStore.Current.ActivationMode)
        {
            case "pushToTalk":
                StopRecording();
                break;

            case "toggle":
                break; // toggle acts on press only

            default: // hybrid
                if (_state == State.Recording && _armed)
                {
                    _armed = false;
                    if (_pressTimer.ElapsedMilliseconds < TapThresholdMs)
                        _toggleActive = true;   // quick tap → latch on (toggle)
                    else
                        StopRecording();        // held → push-to-talk release
                }
                break;
        }
    });

    // ---- Recording state machine ----

    private void StartRecording()
    {
        if (_state == State.Recording) return;
        if (!_engineReady)
        {
            _tray.ShowBalloon("Shhhcribble", "Still loading the model — try again in a moment.");
            return;
        }

        _state = State.Recording;
        _hotkey.InterceptEscape = true;

        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.PauseAsync();

        _lozenge.ShowRecording();
        _mic.Start();
    }

    private void StopRecording()
    {
        if (_state != State.Recording) return;
        _state = State.Idle;
        _armed = false;
        _toggleActive = false;
        _hotkey.InterceptEscape = false;

        var samples = _mic.Stop();
        var engine = _engine;

        Task.Run(() =>
        {
            string text = "";
            string? error = null;
            try { text = engine?.Transcribe(samples) ?? ""; }
            catch (Exception ex) { error = ex.Message; }

            _ui.BeginInvoke(() =>
            {
                ResumeMusic();

                if (error != null) { _lozenge.ShowError("Transcription failed"); return; }

                var s = SettingsStore.Current;
                var processed = PersonalDictionary.Apply(s.Dictionary, text);
                if (s.FillerFilterEnabled) processed = FillerWordFilter.Filter(processed);

                if (string.IsNullOrWhiteSpace(processed))
                {
                    _lozenge.ShowNoResult();
                    return;
                }

                _inserter.Insert(processed);
                SettingsStore.AddHistory(processed);
                _tray.Rebuild();
                _lozenge.ShowCopied();
            });
        });
    }

    private void CancelRecording()
    {
        if (_state != State.Recording) return;
        _state = State.Idle;
        _armed = false;
        _toggleActive = false;
        _hotkey.InterceptEscape = false;
        _mic.Stop();
        ResumeMusic();
        _lozenge.Cancel();
    }

    private void ResumeMusic()
    {
        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.ResumeAsync();
    }

    // ---- Tray wiring ----

    private void WireTray()
    {
        _tray.SettingsClicked += () => _ui.BeginInvoke(OpenSettings);
        _tray.QuitClicked += () => _ui.BeginInvoke(() => Application.Current.Shutdown());

        _tray.ActivationModeSelected += mode =>
        {
            SettingsStore.Current.ActivationMode = mode;
            SettingsStore.Save();
            _tray.Rebuild();
        };

        _tray.PauseMusicToggled += on =>
        {
            SettingsStore.Current.PauseMusicEnabled = on;
            SettingsStore.Save();
            _tray.Rebuild();
        };

        _tray.ModelSelected += id =>
        {
            if (id == SettingsStore.Current.ModelId) return;
            SettingsStore.Current.ModelId = id;
            SettingsStore.Save();
            _tray.Rebuild();
            LoadEngineAsync(id);
        };

        _tray.HistoryItemClicked += text =>
            _ui.BeginInvoke(() => { try { Clipboard.SetText(text); } catch { } });
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.HotkeyChanged += id => _hotkey.SetHotkey(HotkeyOptions.ById(id));
        _settingsWindow.ModelChanged += id => LoadEngineAsync(id);
        _settingsWindow.SettingsChanged += () => _tray.Rebuild();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _mic.Dispose();
        _engine?.Dispose();
        _tray.Dispose();
    }
}
