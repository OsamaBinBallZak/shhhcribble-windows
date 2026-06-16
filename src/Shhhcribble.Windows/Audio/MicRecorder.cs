using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Shhhcribble.Core.Audio;
using Timer = System.Threading.Timer; // disambiguate from System.Windows.Forms.Timer

namespace Shhhcribble.Windows.Audio;

/// <summary>
/// Records the default microphone via WASAPI and returns 16 kHz mono float
/// samples ready for the transcription engine.
///
/// Mirrors the macOS app's load-bearing "fresh capture per recording" rule:
/// <see cref="Start"/> always disposes any prior capture and grabs the current
/// default device, so a device swap (Bluetooth headset (dis)connect, etc.)
/// heals itself on the next hotkey press without any device-change listener.
///
/// Additional parity features vs. the macOS AudioRecorder:
///   - Per-buffer RMS LevelChanged event (level = min(rms * 20, 1)), raised
///     off the UI thread; callers must marshal to the dispatcher themselves.
///   - Warm-up / onReady gate: polls capture-device readiness every 25 ms up
///     to 1200 ms, discards silent pre-roll, fires onReady once; fires anyway
///     at budget so a stuck route never hangs recording forever.
///   - onError callback for format-validation failures and capture exceptions.
///   - Thread-safe CurrentSamples snapshot (16 kHz mono, for live preview).
///   - Mid-recording route-change healing via IMMNotificationClient: on
///     OnDefaultDeviceChanged (Capture) while recording, preserves captured
///     samples, rebuilds WasapiCapture on the new default device, and resumes.
/// </summary>
public sealed class MicRecorder : IDisposable, IMMNotificationClient
{
    // ── capture state ──────────────────────────────────────────────────────

    private WasapiCapture? _capture;
    private WaveFormat?    _format;
    private readonly List<float> _mono = new();
    private readonly object      _lock = new();
    private ManualResetEventSlim? _stopped;

    // ── warm-up gate ───────────────────────────────────────────────────────

    // 1200 ms budget, 25 ms poll — exact macOS values.
    private const int WarmUpBudgetMs   = 1200;
    private const int WarmUpPollMs     = 25;

    private Action?         _onReady;
    private Action<string>? _onError;
    private bool            _didFireReady;
    private volatile bool   _recording;     // cancellation flag for poll + route handler
    private Timer?          _warmUpTimer;
    // Set to true when the first DataAvailable buffer with actual audio arrives —
    // secondary "route is live" signal supplementing the MMDevice channel-count poll.
    private volatile bool   _firstDataReceived;

    // ── route-change healing ───────────────────────────────────────────────

    private readonly MMDeviceEnumerator _enumerator = new();

    // ── public contracts ───────────────────────────────────────────────────

    /// <summary>
    /// Fired once per audio buffer, off the UI thread.
    /// level = min(rms * 20, 1) where rms is computed over all mono frames in the buffer.
    /// Callers must marshal to the dispatcher before touching UI.
    /// </summary>
    public event Action<float>? LevelChanged;

    /// <summary>
    /// Thread-safe snapshot of all 16 kHz mono samples captured so far.
    /// Returns an empty array before onReady fires (pre-roll is discarded).
    /// Used by the live-preview loop in AppController.
    /// </summary>
    public float[] CurrentSamples
    {
        get
        {
            float[] raw;
            int srcRate;
            lock (_lock)
            {
                raw = _mono.ToArray();
                srcRate = _format?.SampleRate ?? WavAudio.TargetSampleRate;
            }
            if (raw.Length == 0) return Array.Empty<float>();
            return WavAudio.Resample(raw, srcRate, WavAudio.TargetSampleRate);
        }
    }

    // ── Start ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a fresh recording session.
    /// <paramref name="onReady"/> is invoked once on a thread-pool thread after the
    /// capture route is confirmed live (or the 1200 ms budget elapses). Callers
    /// should marshal to the UI dispatcher inside the callback.
    /// <paramref name="onError"/> is invoked on a thread-pool thread on failure.
    /// </summary>
    public void Start(Action? onReady = null, Action<string>? onError = null)
    {
        // Tear down any previous session (fresh-per-recording rule).
        Stop();

        _onReady           = onReady;
        _onError           = onError;
        _didFireReady      = false;
        _recording         = true;
        _firstDataReceived = false;

        lock (_lock) _mono.Clear();
        _stopped = new ManualResetEventSlim(false);

        // ── construct capture on the current default device ─────────────

        WasapiCapture capture;
        try
        {
            capture = new WasapiCapture(); // default capture device, shared mode
        }
        catch (Exception ex)
        {
            _recording = false;
            Debug.WriteLine($"[MicRecorder] Capture construction failed: {ex.Message}");
            onError?.Invoke("Couldn't start microphone");
            return;
        }

        // ── validate format (mirrors macOS sampleRate>0 && channelCount>0 guard) ─

        var fmt = capture.WaveFormat;
        if (fmt.SampleRate <= 0 || fmt.Channels <= 0)
        {
            capture.Dispose();
            _recording = false;
            Debug.WriteLine($"[MicRecorder] Bad format: {fmt.SampleRate} Hz / {fmt.Channels} ch");
            onError?.Invoke("Microphone not ready — try again");
            return;
        }

        _format  = fmt;
        _capture = capture;

        capture.DataAvailable    += OnData;
        capture.RecordingStopped += (_, _) => _stopped?.Set();

        try
        {
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicRecorder] StartRecording failed: {ex.Message}");
            capture.DataAvailable    -= OnData;
            capture.Dispose();
            _capture   = null;
            _recording = false;
            onError?.Invoke("Couldn't start microphone");
            return;
        }

        Debug.WriteLine($"[MicRecorder] Started on {fmt.SampleRate} Hz / {fmt.Channels} ch");

        // ── register for default-device change events ───────────────────
        // IMMNotificationClient — we implement OnDefaultDeviceChanged below.
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { /* non-fatal */ }

        // ── begin warm-up poll ──────────────────────────────────────────
        BeginWarmUp();
    }

    // ── Stop ───────────────────────────────────────────────────────────────

    /// <summary>Stops capture and returns the recording as 16 kHz mono float in [-1, 1].</summary>
    public float[] Stop()
    {
        // Null callbacks first — acts as cancellation flag for warm-up poll and
        // route-change handler (mirrors macOS tearDown() nil-first pattern).
        _recording    = false;
        _onReady      = null;
        _onError      = null;
        _didFireReady = false;

        _warmUpTimer?.Dispose();
        _warmUpTimer = null;

        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* non-fatal */ }

        int srcRate = _format?.SampleRate ?? WavAudio.TargetSampleRate;

        if (_capture != null)
        {
            try
            {
                _capture.StopRecording();
                // WASAPI stop is async; wait briefly so the final buffers land.
                _stopped?.Wait(500);
            }
            catch { /* ignore */ }

            _capture.DataAvailable -= OnData;
            _capture.Dispose();
            _capture = null;
        }
        _stopped?.Dispose();
        _stopped = null;

        _format = null;

        float[] mono;
        lock (_lock)
        {
            mono = _mono.ToArray();
            _mono.Clear();
        }
        return WavAudio.Resample(mono, srcRate, WavAudio.TargetSampleRate);
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }

    // ── OnData — per-buffer processing ─────────────────────────────────────

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt      = _format!;
        int channels = Math.Max(1, fmt.Channels);

        // Decode frames to mono floats and compute RMS for the level meter.
        float sumOfSquares = 0;
        int   frameCount   = 0;

        lock (_lock)
        {
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                frameCount = e.BytesRecorded / (4 * channels);
                for (int f = 0; f < frameCount; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                    float sample = sum / channels;
                    _mono.Add(sample);
                    sumOfSquares += sample * sample;
                }
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                frameCount = e.BytesRecorded / (2 * channels);
                for (int f = 0; f < frameCount; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                    float sample = sum / channels;
                    _mono.Add(sample);
                    sumOfSquares += sample * sample;
                }
            }
        }

        // Signal the warm-up poll that real audio has arrived.
        if (frameCount > 0)
            _firstDataReceived = true;

        // Raise level event (off UI thread — caller marshals).
        // Exact macOS formula: rms = sqrt(sum² / max(count,1)), level = min(rms * 20, 1).
        if (frameCount > 0 && LevelChanged != null)
        {
            float rms   = MathF.Sqrt(sumOfSquares / frameCount);
            float level = Math.Min(rms * 20.0f, 1.0f);
            LevelChanged.Invoke(level);
        }
    }

    // ── Warm-up poll ────────────────────────────────────────────────────────

    /// <summary>
    /// Kick off the warm-up timer. Polls every 25 ms for up to 1200 ms; on the
    /// first "ready" signal (non-zero channel count from the WASAPI device), or
    /// at budget expiry, discards pre-roll and invokes onReady exactly once.
    /// </summary>
    private void BeginWarmUp()
    {
        // If no onReady caller, fire immediately (no one to gate).
        if (_onReady == null)
        {
            FireReady();
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(WarmUpBudgetMs);
        _warmUpTimer = new Timer(_ => PollWarmUp(deadline), null,
                                 WarmUpPollMs, Timeout.Infinite);
    }

    private void PollWarmUp(DateTime deadline)
    {
        // Cancellation: if _recording was cleared by Stop() we bail out.
        if (!_recording || _didFireReady) return;

        bool ready   = IsDeviceReady();
        bool expired = DateTime.UtcNow >= deadline;

        if (ready)
            Debug.WriteLine("[MicRecorder] Warm-up: device ready — discarding pre-roll, going live");
        else if (expired)
            Debug.WriteLine("[MicRecorder] Warm-up: timed out — going live anyway");

        if (ready || expired)
        {
            FireReady();
        }
        else
        {
            // Schedule next poll.
            _warmUpTimer?.Change(WarmUpPollMs, Timeout.Infinite);
        }
    }

    private void FireReady()
    {
        if (_didFireReady) return;
        _didFireReady = true;

        _warmUpTimer?.Dispose();
        _warmUpTimer = null;

        // Discard silent pre-roll (exact macOS: samples.removeAll before callback).
        lock (_lock) _mono.Clear();

        var cb = _onReady;
        _onReady = null;
        cb?.Invoke();
    }

    /// <summary>
    /// Returns true when the capture route is confirmed live, using two signals:
    /// 1. Primary: the first DataAvailable callback has fired (real audio arrived).
    /// 2. Secondary: the WASAPI MMDevice MixFormat reports channels > 0.
    /// This mirrors the macOS HAL kAudioDevicePropertyStreamConfiguration poll
    /// (the "signal that doesn't lie"). On Windows, the primary signal (actual
    /// data from the capture callback) is the most reliable live-route indicator,
    /// with the MMDevice channel-count as a fallback for cases where Bluetooth
    /// HFP negotiation hasn't yet delivered a first buffer.
    /// </summary>
    private bool IsDeviceReady()
    {
        // Primary: first DataAvailable callback means the route is definitely live.
        if (_firstDataReceived) return true;

        // Secondary: MMDevice MixFormat channel count.
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.AudioClient.MixFormat.Channels > 0;
        }
        catch
        {
            // No capture device available — treat as not ready.
            return false;
        }
    }

    // ── IMMNotificationClient — mid-recording route-change healing ──────────

    /// <summary>
    /// Fired by Windows when the default audio endpoint changes.
    /// Mirrors macOS handleConfigurationChange(): snapshot captured samples,
    /// dispose old WasapiCapture, create new on new default device, restore
    /// samples, re-wire handlers, restart. No debounce.
    /// </summary>
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Only react to capture-role changes while actively recording.
        if (flow != DataFlow.Capture) return;
        if (!_recording) return;
        if (_capture == null) return;

        Debug.WriteLine($"[MicRecorder] Default capture device changed → rebuilding capture");

        // 1. Snapshot captured samples under lock.
        float[] preservedSamples;
        int     preservedSrcRate;
        lock (_lock)
        {
            preservedSamples  = _mono.ToArray();
            preservedSrcRate  = _format?.SampleRate ?? WavAudio.TargetSampleRate;
        }

        // 2. Tear down the old capture (keep _recording = true).
        var oldCapture = _capture;
        _capture = null;

        if (oldCapture != null)
        {
            try
            {
                oldCapture.StopRecording();
                _stopped?.Wait(200);
            }
            catch { /* ignore */ }
            oldCapture.DataAvailable -= OnData;
            oldCapture.Dispose();
        }

        // Reset stopped event for the new capture.
        _stopped?.Dispose();
        _stopped = new ManualResetEventSlim(false);

        // 3. Build a new capture on the new default device.
        WasapiCapture newCapture;
        try
        {
            newCapture = new WasapiCapture();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicRecorder] Route-change rebuild failed: {ex.Message}");
            _recording = false;
            _onError?.Invoke("Couldn't start microphone");
            return;
        }

        var newFmt = newCapture.WaveFormat;
        if (newFmt.SampleRate <= 0 || newFmt.Channels <= 0)
        {
            newCapture.Dispose();
            _recording = false;
            _onError?.Invoke("Microphone not ready — try again");
            return;
        }

        // 4. Restore preserved samples (re-sample to new device's native rate if needed).
        lock (_lock)
        {
            _mono.Clear();
            // Convert preserved samples from old rate to new capture's native rate.
            // We store samples at native rate and resample-to-16kHz only in Stop().
            // If rates differ, re-sample the preserved buffer to the new native rate.
            if (preservedSrcRate != newFmt.SampleRate && preservedSamples.Length > 0)
            {
                float[] resampled = WavAudio.Resample(preservedSamples, preservedSrcRate, newFmt.SampleRate);
                _mono.AddRange(resampled);
            }
            else
            {
                _mono.AddRange(preservedSamples);
            }
            _format = newFmt;
        }

        // 5. Wire and start the new capture.
        _firstDataReceived = false; // reset so warm-up logic stays coherent if still polling
        _capture = newCapture;
        newCapture.DataAvailable    += OnData;
        newCapture.RecordingStopped += (_, _) => _stopped?.Set();

        try
        {
            newCapture.StartRecording();
            Debug.WriteLine($"[MicRecorder] Route-change rebuild succeeded: {newFmt.SampleRate} Hz / {newFmt.Channels} ch");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicRecorder] Route-change StartRecording failed: {ex.Message}");
            newCapture.Dispose();
            _capture   = null;
            _recording = false;
            _onError?.Invoke("Couldn't start microphone");
        }
    }

    // ── IMMNotificationClient — unused notifications (required by interface) ─

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
