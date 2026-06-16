# Feature parity checklist — macOS Shhhcribble → Windows port

Every feature/behaviour of Henry's macOS app, with the Windows port's status.
Use this to verify coverage: walk each row, confirm the Windows behaviour matches
(or that a gap/cut is intentional). The macOS app is the source of truth
(`itsHendri/Shhhcribble`).

**Status legend**
- ✅ **Done & verified** — built and proven on macOS/CI
- 🟡 **Built, needs Windows smoke-test** — code complete, never run on real Windows
- 🟠 **Partial** — simplified vs. macOS; works but not 1:1
- ⬜ **Gap** — macOS has it, Windows does **not** yet (decide: port or drop)
- ❌ **Cut** — intentionally out of scope

---

## 1. Core dictation loop

| Feature | Status | Windows impl / notes |
|---|---|---|
| Hotkey → record → release → transcribe → paste into focused field | 🟡 | `AppController` state machine |
| Runs 100% on-device, no cloud / no API keys | ✅ | sherpa-onnx ONNX, proven on Mac |
| Transcription itself (audio → text) | ✅ | Proven on macOS: 5 s clip → text in ~180 ms |
| Minimum-audio guard (ignore <0.5 s taps) | ✅ | `ParakeetTranscriptionEngine.Transcribe` |

## 2. Activation & hotkey

| Feature | Status | Windows impl / notes |
|---|---|---|
| Hybrid gesture: tap = toggle, hold = push-to-talk (300 ms) | 🟡 | `AppController` hybrid logic |
| Toggle mode | 🟡 | |
| Push-to-talk mode | 🟡 | |
| Configurable hotkey presets | 🟡 | `HotkeyOptions` (Ctrl+Space default) |
| Sees key-up (needed for hold) | 🟡 | `GlobalHotkey` (WH_KEYBOARD_LL hook) |
| Trigger key swallowed so it doesn't type a stray char | 🟡 | hook returns 1 while modifiers held |
| Escape-to-cancel during recording (discard, no paste) | 🟡 | `GlobalHotkey.InterceptEscape` |

## 3. Audio capture

| Feature | Status | Windows impl / notes |
|---|---|---|
| Capture default mic | 🟡 | `MicRecorder` (WASAPI) |
| Fresh capture per recording (device swap heals on next press) | 🟡 | `MicRecorder.Start()` disposes prior |
| Downmix + resample to 16 kHz mono float | ✅ | `WavAudio.Resample` (logic tested) |
| Mid-recording device-route change rebuild | ⬜ | macOS rebuilds engine mid-recording; Windows only heals on next press |
| Trailing-words tail on stop | 🟠 | 500 ms WASAPI stop-wait vs macOS's explicit 350 ms tail |

## 4. Transcription engine & models

| Feature | Status | Windows impl / notes |
|---|---|---|
| Parakeet V3 (multilingual) | ✅ | proven; default model |
| Parakeet V2 (English-optimized) | 🟡 | same code path; not yet run |
| Model picker (tray + settings) | 🟡 | `TrayIcon`, `SettingsWindow` |
| Model auto-download + cache on first use | ✅ | `ModelDownloader` (download + bz2/tar extract), proven on Mac |
| Live preview text in lozenge as you speak | ⬜ | macOS `LiveTranscriptionSession` (3 s polling); Windows shows bars only |

## 5. Text post-processing

| Feature | Status | Windows impl / notes |
|---|---|---|
| Filler-word filter (um/uh/you know/like) | ✅ | `FillerWordFilter`, 6 parity tests pass |
| Filler filter on/off toggle | 🟡 | settings (logic done; toggle untested) |
| Personal Dictionary (phrase→replacement, ordered, whole-word) | ✅ | `PersonalDictionary`, 14 parity tests pass |
| Case-sensitive dictionary entries | ✅ | tested |
| Dictionary editor UI | 🟡 | `SettingsWindow` DataGrid |
| Transcript cleanup via on-device LLM | ❌ | **Cut** (macOS uses Apple Foundation Models; no Windows equivalent in scope) |

## 6. Text insertion (paste)

| Feature | Status | Windows impl / notes |
|---|---|---|
| Paste into focused field | 🟡 | `TextInserter`: clipboard + Ctrl+V (`SendInput`) |
| Restore prior clipboard after 2 s (if unchanged) | 🟡 | `TextInserter` timer |
| Accessibility direct-insert path | ➖ | macOS-only; Windows uses paste by design (no reliable caret-insert API) |

## 7. Tray / menu

| Feature | Status | Windows impl / notes |
|---|---|---|
| Tray icon with status ("Loading…"/"Ready") | 🟡 | `TrayIcon` (WinForms NotifyIcon) |
| Menu: recent transcriptions (click to copy) | 🟡 | |
| Menu: activation mode switch | 🟡 | |
| Menu: model switch | 🟡 | |
| Menu: pause-music toggle | 🟡 | |
| Menu: hotkey display, Settings…, Quit | 🟡 | |
| Custom app/tray icon (not the generic one) | ⬜ | currently uses `SystemIcons.Application` |

## 8. Soundwave lozenge

| Feature | Status | Windows impl / notes |
|---|---|---|
| Floating, borderless, non-activating, click-through pill | 🟡 | `LozengeWindow` (WS_EX_NOACTIVATE/TRANSPARENT) |
| Animated bars while recording | 🟡 | |
| States: Recording / Copied ✓ / No speech / Error | 🟡 | auto-hide timings mirror macOS |
| Typewriter intro animation | ⬜ | macOS has it; minor, not ported |

## 9. Settings

| Feature | Status | Windows impl / notes |
|---|---|---|
| Model / hotkey / activation pickers | 🟡 | `SettingsWindow` |
| Filler + pause-music toggles | 🟡 | |
| Personal dictionary editor | 🟡 | |
| Settings persist across launches | 🟡 | `SettingsStore` → `%APPDATA%\Shhhcribble\settings.json` |
| About / version display | ⬜ | no About dialog yet (minor) |

## 10. History

| Feature | Status | Windows impl / notes |
|---|---|---|
| Recent transcriptions, cap 10 | 🟡 | `SettingsStore.AddHistory` |
| Persisted across launches | 🟡 | JSON |
| Shown in tray menu, click to copy | 🟡 | `TrayIcon` |

## 11. Music pause

| Feature | Status | Windows impl / notes |
|---|---|---|
| Pause media on record-start, resume on stop | 🟡 | `MusicPauser` (SMTC) |
| Only resume what we actually paused | 🟡 | tracks paused sessions |
| Coverage | 🟠➕ | SMTC covers Spotify **+ browsers/YouTube + more** (broader than macOS's Spotify/Apple Music) |
| Transport-aware resume delay (AirPods codec settle) | ➖ | macOS-specific Bluetooth concern; N/A on Windows |
| On/off toggle (default on) | 🟡 | |

## 12. Audio feedback (sounds)

| Feature | Status | Windows impl / notes |
|---|---|---|
| Scribble sound on hotkey release | ⬜ | macOS plays `shhhcribble-scribble-sound.mp3`; **not implemented on Windows** |
| Completion sound | ⬜ | not implemented |

## 13. Permissions & first-run

| Feature | Status | Windows impl / notes |
|---|---|---|
| Microphone permission | 🟡 | Windows mic-privacy prompt; verify capture isn't blocked |
| Accessibility/Input-Monitoring permission | ➖ | not needed on Windows at same integrity (no macOS-style TCC) |
| Launch at sign-in | 🟡 | installer "Start when I sign in" task |

## 14. Distribution

| Feature | Status | Windows impl / notes |
|---|---|---|
| One-time installer, no admin | 🟡 | Inno Setup per-user installer (builds on CI) |
| Self-contained, no prerequisites for users | ✅ | publish bundles .NET runtime + native ONNX/sherpa DLLs |
| Auto-update | ❌ | **Cut** (macOS uses Sparkle) |
| Code signing / notarization | ⬜ | unsigned → SmartScreen warning on first run (out of scope for now) |

## 15. Explicitly cut (out of scope, by decision)

- ❌ Transcript cleanup via on-device LLM (Apple Foundation Models)
- ❌ Auto-update (Sparkle)
- ❌ Translation
- ❌ Easter egg (macOS has a hidden one; not ported)

---

## Summary of real gaps to decide on (macOS has, Windows doesn't yet)

These are the honest ⬜ items — none are dealbreakers, but they're where the
Windows port is *not* yet at parity:

1. **Live transcription preview** in the lozenge (text appearing as you speak).
2. **Audio feedback sounds** (scribble-on-release + completion chime).
3. **Mid-recording device-route rebuild** (Windows only heals on the next press).
4. **Custom app icon** (currently the generic Windows icon).
5. **About/version dialog** and **typewriter intro** (cosmetic).

## What needs a human on Windows (everything marked 🟡)

All 🟡 rows are *written and compiling* but have never executed on real Windows.
Verifying them is exactly the [smoke-test checklist](WINDOWS_TESTING.md). Start
there; the engine, text processing, download/cache, build, and packaging (the ✅
rows) are already proven.
