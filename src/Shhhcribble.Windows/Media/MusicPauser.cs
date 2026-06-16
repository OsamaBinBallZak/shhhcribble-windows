using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Shhhcribble.Windows.Media;

/// <summary>
/// Pauses currently-playing media when recording starts and resumes it on stop,
/// via Windows System Media Transport Controls (SMTC). The Windows analogue of
/// the macOS MusicPauser — but SMTC covers ANY SMTC-aware source (Spotify,
/// browser tabs / YouTube, Groove, etc.), not just Spotify + Apple Music, so
/// this is actually broader than the macOS coverage.
///
/// Only sessions we actually paused are tracked and resumed, so we never start
/// playback the user had paused themselves before recording (mirrors the macOS
/// "only resume what we paused" rule).
///
/// Resume paths:
///   Success path  — call <see cref="ScheduleResumeAfterOutputSettlesAsync"/>:
///                   detects Bluetooth output and waits 2100 ms (BT) or 700 ms
///                   (non-BT) before resuming. This matches the macOS
///                   <c>scheduleResumeAfterOutputSettles()</c> behaviour — the
///                   delay clears the HFP→A2DP codec transition on AirPods /
///                   Bluetooth headsets so music doesn't audibly bleed through
///                   the mic codec while the route switches back.
///   Cancel / error / quit — call <see cref="ResumeAsync"/> directly for
///                   immediate resume with no delay.
/// </summary>
public sealed class MusicPauser
{
    private readonly List<GlobalSystemMediaTransportControlsSession> _paused = new();

    // PKEY_Device_EnumeratorName — {A45C254E-DF1C-4EFD-8020-67D146A850E0}, pid 24
    // Value is "BTHENUM" (Classic BT) or "BTHHFENUM" (HFP) or "BluetoothLE" (BLE).
    private static readonly Guid s_enumeratorNameFmtid =
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const int EnumeratorNamePid = 24;

    public async Task PauseAsync()
    {
        _paused.Clear();
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                var info = session.GetPlaybackInfo();
                if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && info.Controls.IsPauseEnabled)
                {
                    if (await session.TryPauseAsync())
                        _paused.Add(session);
                }
            }
        }
        catch { /* SMTC unavailable or access denied → no-op */ }
    }

    public async Task ResumeAsync()
    {
        foreach (var session in _paused)
        {
            try { await session.TryPlayAsync(); } catch { /* session gone → skip */ }
        }
        _paused.Clear();
    }

    /// <summary>
    /// Success-path resume: mirrors macOS <c>scheduleResumeAfterOutputSettles()</c>.
    /// If nothing was paused this is a no-op.
    /// Otherwise detects whether the default audio render endpoint is Bluetooth,
    /// waits 2100 ms (BT) or 700 ms (non-BT), then calls <see cref="ResumeAsync"/>.
    /// The BT delay covers the full HFP→A2DP codec transition plus buffer flush so
    /// music does not audibly bleed through the mic codec after recording stops.
    /// </summary>
    public async Task ScheduleResumeAfterOutputSettlesAsync()
    {
        if (_paused.Count == 0)
            return;

        bool isBluetooth = IsDefaultRenderEndpointBluetooth();
        int delayMs = isBluetooth ? 2100 : 700;
        await Task.Delay(delayMs);
        await ResumeAsync();
    }

    // -----------------------------------------------------------------------
    // Bluetooth detection helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the current default multimedia render (output) endpoint
    /// is a Bluetooth device.
    ///
    /// Detection strategy (in order, returns true on first match):
    ///   1. Read PKEY_Device_EnumeratorName from the device's PropertyStore.
    ///      The value is "BTHENUM" (Classic BT audio), "BTHHFENUM" (HFP profile),
    ///      or starts with "Bluetooth" (BLE, some OEM strings).
    ///   2. Name heuristic: FriendlyName contains common BT keywords.
    ///      Catches devices whose enumerator name is non-standard but whose
    ///      display name unmistakably identifies them as Bluetooth.
    ///
    /// Any exception (no audio device, permission error, COM failure) is swallowed
    /// and returns false — the delay falls back to the non-BT 700 ms path, which
    /// is always safe.
    /// </summary>
    private static bool IsDefaultRenderEndpointBluetooth()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // 1. PKEY_Device_EnumeratorName property
            try
            {
                var key = new PropertyKey(s_enumeratorNameFmtid, EnumeratorNamePid);
                var props = device.Properties;
                // PropertyStore indexer returns a PropVariant; .Value is the boxed object.
                var value = props[key].Value as string;
                if (value != null)
                {
                    // "BTHENUM"    = Bluetooth Classic audio/A2DP
                    // "BTHHFENUM"  = Bluetooth HFP (hands-free profile)
                    // "BluetoothLE" or starts with "Bluetooth" = BLE audio
                    if (value.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
                        value.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* property not present or COM error — fall through */ }

            // 2. Name heuristic fallback
            var name = device.FriendlyName ?? string.Empty;
            if (name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("airpods", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("buds", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("headset", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
        catch
        {
            // No render device, COM failure, etc. — treat as non-BT (700 ms delay).
            return false;
        }
    }
}
