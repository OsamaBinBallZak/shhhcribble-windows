using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shhhcribble.Core.Audio;

namespace Shhhcribble.Windows.Audio;

/// <summary>
/// Records the default microphone via WASAPI and returns 16 kHz mono float
/// samples ready for the transcription engine.
///
/// Mirrors the macOS app's load-bearing "fresh capture per recording" rule:
/// <see cref="Start"/> always disposes any prior capture and grabs the current
/// default device, so a device swap (Bluetooth headset (dis)connect, etc.)
/// heals itself on the next hotkey press without any device-change listener.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private readonly List<float> _mono = new();
    private readonly object _lock = new();
    private ManualResetEventSlim? _stopped;

    public void Start()
    {
        Stop(); // discard any prior session and rebind to the current default device

        lock (_lock) _mono.Clear();
        _stopped = new ManualResetEventSlim(false);

        _capture = new WasapiCapture(); // default capture device, shared mode
        _format = _capture.WaveFormat;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => _stopped?.Set();
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _format!;
        int channels = Math.Max(1, fmt.Channels);

        lock (_lock)
        {
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                int frames = e.BytesRecorded / (4 * channels);
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                    _mono.Add(sum / channels);
                }
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                int frames = e.BytesRecorded / (2 * channels);
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                    _mono.Add(sum / channels);
                }
            }
        }
    }

    /// <summary>Stops capture and returns the recording as 16 kHz mono float in [-1, 1].</summary>
    public float[] Stop()
    {
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

        float[] mono;
        lock (_lock) mono = _mono.ToArray();
        return WavAudio.Resample(mono, srcRate, WavAudio.TargetSampleRate);
    }

    public void Dispose() => Stop();
}
