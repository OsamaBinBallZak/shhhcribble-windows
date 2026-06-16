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
/// </summary>
public sealed class MusicPauser
{
    private readonly List<GlobalSystemMediaTransportControlsSession> _paused = new();

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
}
