using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;

namespace Shhhcribble.Core.Transcription;

/// <summary>
/// Downloads a Parakeet ONNX model archive on first use and caches it locally,
/// so subsequent launches load instantly (mirrors FluidAudio's
/// download-and-cache behaviour on macOS). The archive is a .tar.bz2 from the
/// sherpa-onnx releases page; .NET has no built-in bzip2, so SharpZipLib does
/// the bz2 → tar extraction.
/// </summary>
public static class ModelDownloader
{
    public sealed record ModelFiles(string Encoder, string Decoder, string Joiner, string Tokens);

    /// <summary>Per-user cache: %LOCALAPPDATA%\Shhhcribble\models on Windows,
    /// ~/.local/share/Shhhcribble/models on macOS/Linux.</summary>
    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shhhcribble", "models");

    public static async Task<ModelFiles> EnsureAsync(
        ParakeetModel model, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(CacheRoot, model.Id);

        if (TryLocate(dir) is { } cached)
        {
            progress?.Report($"Model cached at {dir}");
            return cached;
        }

        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, "model.tar.bz2");

        progress?.Report($"Downloading {model.DisplayName}…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        await using (var src = await http.GetStreamAsync(model.ArchiveUrl, ct))
        await using (var dst = File.Create(archive))
        {
            await src.CopyToAsync(dst, ct);
        }

        progress?.Report("Extracting…");
        Extract(archive, dir);
        File.Delete(archive);

        return TryLocate(dir) ?? throw new InvalidOperationException(
            "Model archive did not contain the expected encoder/decoder/joiner/tokens files.");
    }

    private static void Extract(string archivePath, string destDir)
    {
        using var fs = File.OpenRead(archivePath);
        using var bz = new BZip2InputStream(fs);
        using var tar = TarArchive.CreateInputTarArchive(bz, System.Text.Encoding.UTF8);
        tar.ExtractContents(destDir);
    }

    /// <summary>
    /// Finds the four model files by glob, so we don't hard-depend on the exact
    /// folder name inside the archive (int8 builds name files
    /// encoder.int8.onnx etc.).
    /// </summary>
    private static ModelFiles? TryLocate(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string? First(string pattern) =>
            Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).OrderBy(p => p).FirstOrDefault();

        var enc = First("encoder*.onnx");
        var dec = First("decoder*.onnx");
        var joi = First("joiner*.onnx");
        var tok = First("tokens.txt");

        return enc is not null && dec is not null && joi is not null && tok is not null
            ? new ModelFiles(enc, dec, joi, tok)
            : null;
    }
}
