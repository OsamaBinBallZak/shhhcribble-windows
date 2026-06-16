# Windows smoke-test checklist

CI proves the app **builds, tests pass, publishes, and packages into an
installer** on real Windows. It cannot click through the UI or press keys, so
the items below need a human on an actual Windows 10/11 machine. Grab the
`Shhhcribble-Setup` (or `Shhhcribble-win-x64`) artifact from the latest green CI
run, install/run it, and walk this list.

## First launch
- [ ] App starts with no window; a tray icon appears (look in the hidden-icons
      overflow). Tooltip shows "Loading…", then "Ready · Parakeet V3 …" once the
      ~480 MB model has downloaded (first run only; cached afterwards).
- [ ] Right-click the tray icon → the menu shows status, Recent Transcriptions,
      Activation, Model, Pause-music toggle, Hotkey label, Settings…, Quit.

## Core dictation loop
- [ ] Focus a text field (Notepad). Press & hold **Ctrl+Space**, speak, release.
      → lozenge shows "Listening…" with animated bars while held; on release the
      transcribed text pastes at the caret.
- [ ] **Tap** Ctrl+Space quickly (don't hold) → recording latches on (toggle);
      tap again → it stops and pastes. (This is the hybrid gesture.)
- [ ] Press **Esc** while recording → recording cancels, nothing is pasted.
- [ ] Very short tap with no speech → lozenge shows "No speech detected", no paste.

## Paste targets (the risky bit — different apps handle Ctrl+V differently)
- [ ] Pastes into Notepad / WordPad.
- [ ] Pastes into a browser address bar and a web text box (Chrome/Edge).
- [ ] Pastes into an Electron app if you have one (VS Code, Slack, Discord).
- [ ] After pasting, your previous clipboard contents are restored ~2 s later
      (copy something, dictate, then Ctrl+V again → your original copy is back).

## Music pause (SMTC)
- [ ] Play Spotify (or a YouTube tab). Start recording → playback pauses.
      Finish → playback resumes. Music you had *already paused* stays paused.

## Settings & persistence
- [ ] Settings… → change Hotkey, Activation, Model; toggle filler-strip and
      music-pause; add a Personal Dictionary entry (e.g. `shit cribble` →
      `Shhhcribble`). Save.
- [ ] The dictionary entry actually rewrites the transcript on the next dictation.
- [ ] Quit and relaunch → all settings + recent history survived (stored in
      `%APPDATA%\Shhhcribble\settings.json`).
- [ ] Switching Model in the menu re-downloads/loads and status updates.

## Known risk areas to watch (no Mac equivalent to lean on)
- **Mic format**: WASAPI capture assumes float or 16-bit PCM from the default
  device. If a clip comes back silent/garbled, the device mix format is the first
  suspect (see `MicRecorder`).
- **Hotkey swallowing**: the hook swallows the trigger key while modifiers are
  held. If Ctrl+Space is needed by another app (e.g. an IME), pick a different
  preset in Settings.
- **SmartScreen**: the installer is unsigned, so Windows SmartScreen will warn on
  first run ("More info → Run anyway"). Code-signing is out of scope for now.
- **Tail loss**: WASAPI stop waits 500 ms for final buffers; if the last word is
  clipped, that wait may need lengthening.
