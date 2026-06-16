using System.Diagnostics;
using Shhhcribble.Core.Audio;
using Shhhcribble.Core.Text;
using Shhhcribble.Core.Transcription;

// A tiny harness that drives the *exact* Core pipeline the Windows app will use
// (download → load → transcribe → filler-filter), but from a .wav file instead
// of a live mic. Lets us prove the on-device Parakeet engine works off-Windows.

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: dotnet run --project src/Shhhcribble.Cli -- <audio.wav> [--model parakeet-v3|parakeet-v2]");
    return args.Length < 1 ? 1 : 0;
}

var wavPath = args[0];
var modelId = ParakeetModels.DefaultId;
for (int i = 1; i < args.Length - 1; i++)
    if (args[i] is "--model") modelId = args[i + 1];

if (!File.Exists(wavPath))
{
    Console.Error.WriteLine($"File not found: {wavPath}");
    return 1;
}

var model = ParakeetModels.ById(modelId);
var progress = new Progress<string>(Console.WriteLine);

var files = await ModelDownloader.EnsureAsync(model, progress);

Console.WriteLine("Loading model…");
using var engine = ParakeetTranscriptionEngine.Load(files);

Console.WriteLine($"Reading {wavPath}…");
var samples = WavAudio.ReadAsMono16k(wavPath);
Console.WriteLine($"  {samples.Length} samples (~{samples.Length / (double)WavAudio.TargetSampleRate:F1}s @ 16 kHz)");

var sw = Stopwatch.StartNew();
var raw = engine.Transcribe(samples);
sw.Stop();

var cleaned = FillerWordFilter.Filter(raw);

Console.WriteLine();
Console.WriteLine($"RAW:     \"{raw}\"");
Console.WriteLine($"CLEANED: \"{cleaned}\"");
Console.WriteLine($"\nTranscribed in {sw.ElapsedMilliseconds} ms.");
return 0;
