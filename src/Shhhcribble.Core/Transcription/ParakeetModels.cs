namespace Shhhcribble.Core.Transcription;

/// <summary>
/// A Parakeet model variant and where to fetch its ONNX export.
/// </summary>
public sealed record ParakeetModel(string Id, string DisplayName, string ArchiveUrl);

/// <summary>
/// Central registry of available Parakeet model variants — the Windows analogue
/// of the macOS app's <c>ModelManager.availableModels</c>. Both variants are the
/// NVIDIA Parakeet TDT 0.6B family, pre-exported to ONNX (int8) and hosted on the
/// sherpa-onnx releases page, so they run fully on-device with no cloud.
/// </summary>
public static class ParakeetModels
{
    private const string Base =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public static readonly ParakeetModel V3 = new(
        "parakeet-v3",
        "Parakeet V3 — Multilingual (25 languages)",
        Base + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2");

    public static readonly ParakeetModel V2 = new(
        "parakeet-v2",
        "Parakeet V2 — English-optimized",
        Base + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2");

    public static readonly IReadOnlyList<ParakeetModel> All = new[] { V3, V2 };

    public const string DefaultId = "parakeet-v3";

    public static ParakeetModel ById(string id) =>
        All.FirstOrDefault(m => m.Id == id) ?? V3;
}
