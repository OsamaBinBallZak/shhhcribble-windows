using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Shhhcribble.Core.Text;
using Shhhcribble.Core.Transcription;
using Shhhcribble.Windows.Audio;
using Shhhcribble.Windows.Input;
using Shhhcribble.Windows.Media;
using Shhhcribble.Windows.TextInsertion;
using Shhhcribble.Windows.UI;
using Application = System.Windows.Application; // disambiguate from WinForms

namespace Shhhcribble.Windows;

/// <summary>
/// Owns every subsystem and the recording state machine — Windows analogue of
/// the macOS AppDelegate. Hotkey events arrive on the UI thread (the hook is
/// installed there); transcription runs on a worker thread; UI/paste marshal
/// back to the dispatcher.
///
/// State machine mirrors macOS exactly:
///   Idle → (hotkey down) → Recording → (hotkey up/second-tap) → Transcribing → Idle
/// The Transcribing state blocks hotkey re-entry for the full transcribe+paste
/// window, preventing a second recording from starting over an in-flight engine
/// call (macOS .transcribing break in onKeyDown).
/// </summary>
public sealed class AppController : IDisposable
{
    private enum State { Idle, Recording, Transcribing }

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

    // Hybrid-gesture bookkeeping. 500 ms hold threshold matches the macOS app.
    private const int TapThresholdMs = 500;
    private readonly Stopwatch _pressTimer = new();
    private bool _armed;        // a press started a recording that's awaiting tap/hold classification
    private bool _toggleActive; // recording is latched on via a tap

    // Live-preview loop cancellation
    private CancellationTokenSource? _livePreviewCts;

    // Completion sound player (WPF MediaPlayer at SpeedRatio 1.4 — mirrors macOS AVPlayer rate=1.4)
    private MediaPlayer? _soundPlayer;

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        _inserter = new TextInserter(ui);
        _hotkey = new GlobalHotkey(HotkeyOptions.ById(SettingsStore.Current.HotkeyId));
        InitSound();
    }

    // ---- Sound ----

    private void InitSound()
    {
        try
        {
            // The MP3 is a Content item copied next to the exe under Resources\.
            // Use AppContext.BaseDirectory so this resolves correctly for both
            // dotnet run (output dir) and the self-contained publish layout.
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "shhhcribble-scribble-sound.mp3");
            if (!File.Exists(path))
            {
                Debug.WriteLine($"[AppController] Sound file not found at {path}");
                return;
            }
            _soundPlayer = new MediaPlayer();
            _soundPlayer.Open(new Uri(path, UriKind.Absolute));
            _soundPlayer.SpeedRatio = 1.4;
            _soundPlayer.Volume     = 1.0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppController] Sound init failed: {ex.Message}");
            _soundPlayer = null;
        }
    }

    private void PlayCompletionSound()
    {
        try
        {
            if (_soundPlayer != null)
            {
                _soundPlayer.Stop();
                _soundPlayer.Position = TimeSpan.Zero;
                _soundPlayer.Play();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppController] Sound play failed: {ex.Message}");
        }
    }

    public void Start()
    {
        WireTray();
        WireMic();

        _hotkey.ComboDown += OnComboDown;
        _hotkey.ComboUp += OnComboUp;
        _hotkey.EscapePressed += () => _ui.BeginInvoke(CancelRecording);
        _hotkey.Install();

        LoadEngineAsync(SettingsStore.Current.ModelId);
    }

    // ---- Mic wiring ----

    private void WireMic()
    {
        // Level callback: marshal to UI thread and drive lozenge bars.
        _mic.LevelChanged += level =>
            _ui.BeginInvoke(() => _lozenge.AudioLevel = level);
    }

    // ---- Model loading ----

    private void LoadEngineAsync(string modelId)
    {
        _engineReady = false;
        var model = ParakeetModels.ById(modelId);
        _tray.SetStatus($"Loading {model.DisplayName}…");
        _tray.SetReady(false);

        // Notify open settings window that model is loading
        _ui.BeginInvoke(() => _settingsWindow?.SetModelStatus($"Loading {model.DisplayName}…", false));

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
                    _tray.SetReady(true);
                    _settingsWindow?.SetModelStatus($"Ready — {model.DisplayName}", false);
                });
            }
            catch (Exception ex)
            {
                _ui.BeginInvoke(() =>
                {
                    // macOS: "Error: {err}" in status line, real error text
                    _tray.SetStatus($"Error: {ex.Message}");
                    _tray.ShowBalloon("Shhhcribble", $"Could not load the model: {ex.Message}");
                    _settingsWindow?.SetModelStatus($"Failed: {ex.Message}", true);
                });
            }
        });
    }

    // ---- Hotkey → activation logic ----

    private void OnComboDown() => _ui.BeginInvoke(() =>
    {
        // Transcribing state blocks hotkey re-entry — mirrors macOS .transcribing → break
        if (_state == State.Transcribing) return;

        switch (SettingsStore.Current.ActivationMode)
        {
            case "pushToTalk":
                StartRecording();
                break;

            case "toggle":
                if (_state == State.Idle) { StartRecording(); _toggleActive = true; }
                else if (_state == State.Recording) StopRecording();
                break;

            default: // hybrid
                if (_state == State.Idle)
                {
                    StartRecording();
                    _armed = true;
                    _pressTimer.Restart();
                }
                else if (_state == State.Recording && _toggleActive)
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
                if (_state == State.Recording) StopRecording();
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
        if (_state != State.Idle) return;
        if (!_engineReady)
        {
            _tray.ShowBalloon("Shhhcribble", "Still loading the model — try again in a moment.");
            return;
        }

        // Mic permission gate — mirrors macOS AVCaptureDevice.authorizationStatus(.audio)
        // Windows AppCapability API is optional; if unavailable assume allowed.
        if (!CheckMicPermission()) return;

        _state = State.Recording;
        _hotkey.InterceptEscape = true;
        _tray.SetRecordingIndicator(true);
        _settingsWindow?.SetBusy(true);

        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.PauseAsync();

        // Show lozenge ONLY from onReady (mirrors macOS actuallyBeginRecording onReady gate).
        // This prevents showing "Listening…" while the mic route is cold (Bluetooth warm-up).
        _mic.Start(
            onReady: () =>
            {
                // onReady fires on a thread-pool thread; marshal to UI.
                _ui.BeginInvoke(() =>
                {
                    if (_state != State.Recording) return; // guard: cancelled before ready
                    _lozenge.ShowRecording();
                    StartLivePreview();
                });
            },
            onError: msg =>
            {
                _ui.BeginInvoke(() =>
                {
                    // Audio-error teardown: mirrors macOS handleAudioError
                    StopLivePreview();
                    _music.ResumeAsync().ConfigureAwait(false);
                    _hotkey.InterceptEscape = false;
                    _tray.SetRecordingIndicator(false);
                    _settingsWindow?.SetBusy(false);
                    _armed = false;
                    _toggleActive = false;
                    _state = State.Idle;
                    _lozenge.ShowError(msg);
                });
            }
        );
    }

    // Returns false if mic permission is definitively denied and we surfaced an error.
    private bool CheckMicPermission()
    {
        // Try Windows AppCapability (Windows 10 1809+). If unavailable/not packaged, assume allowed.
        try
        {
            var type = Type.GetType("Windows.Security.Authorization.AppCapabilityAccess.AppCapability, Windows, ContentType=WindowsRuntime");
            if (type != null)
            {
                var createMethod = type.GetMethod("Create", new[] { typeof(string) });
                var capability = createMethod?.Invoke(null, new object[] { "microphone" });
                if (capability != null)
                {
                    var checkMethod = capability.GetType().GetMethod("CheckAccess");
                    var result = checkMethod?.Invoke(capability, null);
                    if (result != null)
                    {
                        // AppCapabilityAccessStatus: 0=DeniedBySystem, 1=DeniedByUser, 2=NotDeclaredByApp, 3=Allowed
                        int status = (int)result;
                        if (status == 0 || status == 1) // denied
                        {
                            _lozenge.ShowError("Microphone permission denied");
                            return false;
                        }
                    }
                }
            }
        }
        catch
        {
            // API unavailable (not MSIX packaged) — assume allowed, same as macOS .notDetermined→granted
        }
        return true;
    }

    private async void StopRecording()
    {
        if (_state != State.Recording) return;

        // Immediately update state and UI indicators before any awaits
        _state = State.Transcribing; // blocks hotkey re-entry
        _armed = false;
        _toggleActive = false;
        _hotkey.InterceptEscape = false;
        _tray.SetRecordingIndicator(false);
        _settingsWindow?.SetBusy(false);

        // 1. Cancel+await the live-preview task BEFORE final transcribe
        //    (mirrors macOS: liveTask?.cancel() + await liveTask?.value)
        await StopLivePreviewAsync();

        // 2. Play the completion sound FIRST — immediate audible confirmation
        //    (mirrors macOS: soundwavePanel.playCompletionSound() first in endRecording)
        PlayCompletionSound();

        // 3. Show persistent "Transcribing…" pill (no auto-hide)
        //    (mirrors macOS: soundwavePanel.showTranscribing())
        _lozenge.ShowTranscribing();

        // 4. 350 ms recording tail — keep capturing so the last word isn't clipped
        //    (mirrors macOS: try? await Task.sleep(for: .milliseconds(350)))
        await Task.Delay(350);

        // 5. Stop capture and get samples
        var samples = _mic.Stop();

        // 6. Schedule transport-aware music resume (success path)
        //    (mirrors macOS: musicPauser.scheduleResumeAfterOutputSettles())
        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.ScheduleResumeAfterOutputSettlesAsync();

        var engine = _engine;

        // 7. Transcribe on worker thread
        _ = Task.Run(async () =>
        {
            string text = "";
            string? error = null;
            try { text = engine?.Transcribe(samples) ?? ""; }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[AppController] Transcription error: {ex.Message}");
            }

            await _ui.InvokeAsync(() =>
            {
                if (error != null)
                {
                    // Pass real error message — mirrors macOS showError(message)
                    _lozenge.ShowError(error);
                    _state = State.Idle;
                    return;
                }

                var s = SettingsStore.Current;

                // Trim raw text BEFORE dictionary (mirrors macOS: text.trimmingCharacters)
                var trimmed = text?.Trim() ?? "";

                // PersonalDictionary on trimmed raw
                var processed = PersonalDictionary.Apply(s.Dictionary, trimmed);

                // FillerWordFilter — intentional Windows feature: toggleable (keep toggle, documented divergence).
                // macOS removed the toggle; on Windows it's still available per project scope.
                if (s.FillerFilterEnabled)
                    processed = FillerWordFilter.Filter(processed);

                if (string.IsNullOrWhiteSpace(processed))
                {
                    _lozenge.ShowNoResult();
                    _state = State.Idle;
                    return;
                }

                // 8. Add to history + rebuild tray BEFORE paste
                //    (mirrors macOS: addToHistory + rebuildMenu BEFORE Task.sleep + insert)
                SettingsStore.AddHistory(processed);
                _tray.Rebuild();

                // 9. ~150 ms focus-settle delay before paste
                //    (mirrors macOS: try? await Task.sleep(for: .milliseconds(150)))
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150);
                    await _ui.InvokeAsync(() =>
                    {
                        _inserter.Insert(processed);
                        _lozenge.ShowCopied();
                        _state = State.Idle;
                    });
                });
            });
        });
    }

    private void CancelRecording()
    {
        if (_state != State.Recording) return;
        Debug.WriteLine("[AppController] Recording cancelled (Escape)");

        StopLivePreview(); // fire-and-forget cancel (sync)
        _state = State.Idle;
        _armed = false;
        _toggleActive = false;
        _hotkey.InterceptEscape = false;
        _tray.SetRecordingIndicator(false);
        _settingsWindow?.SetBusy(false);

        _mic.Stop();

        // Immediate music resume on cancel — mirrors macOS cancelRecording musicPauser.resumeIfPaused()
        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.ResumeAsync();

        _lozenge.Cancel();
    }

    // ---- Live-preview loop ----
    // Mirrors macOS liveTranscriptionTask: polls every 3 s while recording,
    // transcribes CurrentSamples snapshot if > 16000 samples, applies PersonalDictionary,
    // updates lozenge with UpdateLiveText. Cancelled and awaited before final transcribe.

    private const int LiveTranscriptionIntervalMs = 3000;

    private void StartLivePreview()
    {
        StopLivePreview();
        _livePreviewCts = new CancellationTokenSource();
        var token = _livePreviewCts.Token;

        _ = Task.Run(async () =>
        {
            // First pass: wait one interval before sampling
            try { await Task.Delay(LiveTranscriptionIntervalMs, token); }
            catch (TaskCanceledException) { return; }

            while (!token.IsCancellationRequested && _state == State.Recording)
            {
                var snapshot = _mic.CurrentSamples;
                // Need > 1 s of audio (> 16000 samples at 16 kHz)
                if (snapshot.Length > 16_000)
                {
                    var engine = _engine;
                    if (engine != null)
                    {
                        try
                        {
                            var text = engine.Transcribe(snapshot);
                            var trimmed = text?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                var s = SettingsStore.Current;
                                var corrected = PersonalDictionary.Apply(s.Dictionary, trimmed);
                                // Marshal UpdateLiveText to UI thread
                                _ui.BeginInvoke(() => _lozenge.UpdateLiveText(corrected));
                            }
                        }
                        catch
                        {
                            // Live-preview errors are non-fatal — ignore silently
                        }
                    }
                }

                try { await Task.Delay(LiveTranscriptionIntervalMs, token); }
                catch (TaskCanceledException) { return; }
            }
        }, token);
    }

    private void StopLivePreview()
    {
        _livePreviewCts?.Cancel();
        _livePreviewCts = null;
    }

    private async Task StopLivePreviewAsync()
    {
        var cts = _livePreviewCts;
        _livePreviewCts = null;
        if (cts == null) return;
        cts.Cancel();
        // Give the live-preview task a moment to observe the cancellation token.
        // We can't await the Task directly (it's fire-and-forget), so a brief
        // cooperative pause ensures the engine call on the worker thread has
        // time to exit before the final Transcribe call below begins.
        // Mirrors macOS: _ = await liveTask?.value
        await Task.Delay(50);
        cts.Dispose();
    }

    // ---- Tray wiring ----

    private void WireTray()
    {
        _tray.SettingsClicked += () => _ui.BeginInvoke(OpenSettings);

        _tray.QuitClicked += () => _ui.BeginInvoke(async () =>
        {
            // Defensive music resume before quit
            // (mirrors macOS applicationWillTerminate: musicPauser.resumeIfPaused())
            if (SettingsStore.Current.PauseMusicEnabled)
            {
                try { await _music.ResumeAsync(); } catch { /* best-effort */ }
            }
            Application.Current.Shutdown();
        });

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

        // History repaste: capture foreground window, wait ~200 ms for menu to close,
        // then Insert (not copy-only) — mirrors macOS menuBarControllerDidRequestRepaste
        _tray.HistoryItemClicked += text =>
        {
            var hwnd = GetForegroundWindow();
            _ = Task.Run(async () =>
            {
                await Task.Delay(200); // let tray menu fully close
                await _ui.InvokeAsync(() =>
                {
                    // Re-focus the captured window so paste lands in the right target
                    if (hwnd != IntPtr.Zero)
                        SetForegroundWindow(hwnd);
                    _inserter.Insert(text);
                });
            });
        };

        // ClearHistoryClicked — mirrors macOS clearHistory()
        _tray.ClearHistoryClicked += () =>
        {
            SettingsStore.ClearHistory();
            _tray.Rebuild();
        };
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

        // Feed current busy/model-status state into the window
        _settingsWindow.SetBusy(_state != State.Idle);
        if (_engineReady)
        {
            var model = ParakeetModels.ById(SettingsStore.Current.ModelId);
            _settingsWindow.SetModelStatus($"Ready — {model.DisplayName}", false);
        }
        else
        {
            var model = ParakeetModels.ById(SettingsStore.Current.ModelId);
            _settingsWindow.SetModelStatus($"Loading {model.DisplayName}…", false);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- P/Invoke helpers for foreground-window capture (history repaste) ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- IDisposable ----

    public void Dispose()
    {
        StopLivePreview();

        // Defensive music resume on dispose
        if (SettingsStore.Current.PauseMusicEnabled)
            _ = _music.ResumeAsync();

        _hotkey.Dispose();
        _mic.Dispose();
        _engine?.Dispose();
        _tray.Dispose();
        _soundPlayer?.Close();
    }
}
