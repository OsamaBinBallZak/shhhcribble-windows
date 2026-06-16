using Shhhcribble.Core.Audio;
using SherpaOnnx;

namespace Shhhcribble.Core.Transcription;

/// <summary>
/// Wraps sherpa-onnx's offline transducer recognizer to provide batch Parakeet
/// transcription — the Windows analogue of the macOS app's
/// <c>TranscriptionEngine</c> (which wrapped FluidAudio's AsrManager). Models run
/// fully on-device via ONNX Runtime. One engine instance holds one loaded model;
/// transcription is a single batch pass on recording stop.
/// </summary>
public sealed class ParakeetTranscriptionEngine : IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    private ParakeetTranscriptionEngine(OfflineRecognizer recognizer) => _recognizer = recognizer;

    public static ParakeetTranscriptionEngine Load(ModelDownloader.ModelFiles files, int numThreads = 2)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner  = files.Joiner;
        config.ModelConfig.Tokens     = files.Tokens;
        config.ModelConfig.ModelType  = "nemo_transducer";
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Debug      = 0;
        config.DecodingMethod         = "greedy_search";

        return new ParakeetTranscriptionEngine(new OfflineRecognizer(config));
    }

    /// <summary>
    /// Transcribe 16 kHz mono float samples in [-1, 1]. Returns "" for clips
    /// under ~0.5 s, matching the macOS app's minimum-audio guard that avoids
    /// spurious transcriptions on accidental taps.
    /// </summary>
    public string Transcribe(float[] samples16k)
    {
        if (samples16k.Length <= WavAudio.TargetSampleRate / 2) return "";

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(WavAudio.TargetSampleRate, samples16k);
        _recognizer.Decode(stream);
        return stream.Result.Text ?? "";
    }

    public void Dispose() => _recognizer.Dispose();
}
