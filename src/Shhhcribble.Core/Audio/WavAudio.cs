namespace Shhhcribble.Core.Audio;

/// <summary>
/// Minimal, dependency-free WAV reader + linear resampler.
///
/// The transcription engine wants 16 kHz mono float samples in [-1, 1]. On
/// Windows the live mic path (NAudio) produces float samples directly; this
/// reader exists so the same engine can be driven from a .wav file (CLI, tests,
/// and the macOS verification path) without pulling an audio framework into
/// Core. Handles the two formats that matter: 16-bit PCM and 32-bit IEEE float.
/// </summary>
public static class WavAudio
{
    public const int TargetSampleRate = 16_000;

    public static float[] ReadAsMono16k(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        br.ReadInt32(); // overall size
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        int channels = 0, sampleRate = 0, bitsPerSample = 0, audioFormat = 0;
        byte[]? data = null;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            int chunkSize = br.ReadInt32();

            if (chunkId == "fmt ")
            {
                audioFormat   = br.ReadInt16();
                channels      = br.ReadInt16();
                sampleRate    = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bitsPerSample = br.ReadInt16();
                int extra = chunkSize - 16;
                if (extra > 0) br.ReadBytes(extra);
            }
            else if (chunkId == "data")
            {
                data = br.ReadBytes(chunkSize);
            }
            else
            {
                br.ReadBytes(chunkSize);
            }

            // RIFF chunks are word-aligned: skip a pad byte after odd-sized chunks.
            if (chunkSize % 2 == 1 && br.BaseStream.Position < br.BaseStream.Length)
                br.ReadByte();
        }

        if (data is null || channels == 0) throw new InvalidDataException("Missing fmt/data chunk.");

        var interleaved = Decode(data, audioFormat, bitsPerSample);
        var mono = Downmix(interleaved, channels);
        return sampleRate == TargetSampleRate ? mono : Resample(mono, sampleRate, TargetSampleRate);
    }

    private static float[] Decode(byte[] data, int audioFormat, int bitsPerSample)
    {
        // PCM = 1, IEEE float = 3.
        if (audioFormat == 3 && bitsPerSample == 32)
        {
            var n = data.Length / 4;
            var outp = new float[n];
            Buffer.BlockCopy(data, 0, outp, 0, n * 4);
            return outp;
        }
        if (audioFormat == 1 && bitsPerSample == 16)
        {
            var n = data.Length / 2;
            var outp = new float[n];
            for (int i = 0; i < n; i++)
                outp[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
            return outp;
        }
        throw new NotSupportedException(
            $"Unsupported WAV format (audioFormat={audioFormat}, bits={bitsPerSample}). " +
            "Expected 16-bit PCM or 32-bit float.");
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    /// <summary>Linear-interpolation resampler. Good enough for speech ASR input.</summary>
    public static float[] Resample(float[] mono, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || mono.Length == 0) return mono;
        double ratio = (double)dstRate / srcRate;
        int outLen = (int)(mono.Length * ratio);
        var outp = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            int j = (int)srcPos;
            double frac = srcPos - j;
            float a = mono[j];
            float b = j + 1 < mono.Length ? mono[j + 1] : a;
            outp[i] = (float)(a + (b - a) * frac);
        }
        return outp;
    }
}
