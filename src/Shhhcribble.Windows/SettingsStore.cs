using System.IO;
using System.Text.Json;
using Shhhcribble.Core.Text;

namespace Shhhcribble.Windows;

/// <summary>
/// Persisted user settings — the Windows analogue of the macOS app's
/// UserDefaults (domain com.shhhcribble.app). Stored as JSON in
/// %APPDATA%\Shhhcribble\settings.json. Keys mirror the macOS prefs so the two
/// apps stay conceptually aligned.
/// </summary>
public sealed class AppSettings
{
    public string ModelId { get; set; } = Core.Transcription.ParakeetModels.DefaultId;
    public string HotkeyId { get; set; } = Input.HotkeyOptions.DefaultId;

    /// <summary>"hybrid" (tap = toggle, hold = push-to-talk), "toggle", or "pushToTalk".</summary>
    public string ActivationMode { get; set; } = "hybrid";

    public bool FillerFilterEnabled { get; set; } = true;
    public bool PauseMusicEnabled { get; set; } = true;

    public List<DictionaryEntry> Dictionary { get; set; } = new();
    public List<HistoryItem> History { get; set; } = new();
}

public sealed class HistoryItem
{
    public string Text { get; set; } = "";
    public DateTimeOffset Date { get; set; }
}

public static class SettingsStore
{
    private const int HistoryCap = 10; // matches the macOS history cap

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shhhcribble");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file → fall back to defaults rather than crash */ }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* best-effort persistence */ }
    }

    public static void AddHistory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Current.History.Insert(0, new HistoryItem { Text = text, Date = DateTimeOffset.Now });
        if (Current.History.Count > HistoryCap)
            Current.History.RemoveRange(HistoryCap, Current.History.Count - HistoryCap);
        Save();
    }

    public static void ClearHistory()
    {
        Current.History.Clear();
        Save();
    }
}
