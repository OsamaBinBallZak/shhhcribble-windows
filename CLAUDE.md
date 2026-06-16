# Shhhcribble for Windows — project context for Claude

**Single source of truth for "what Claude should know before touching this repo." Skim it at the start of every session.** This is a Windows reimplementation of Henry's macOS [Shhhcribble](https://github.com/itsHendri/Shhhcribble). It was built and validated on a Mac via cross-targeting + CI; the maintainer has no local Windows machine, so the verification model is unusual — read "How this project is developed & verified" before assuming you can just run the app.

---

## What this app is

A tray-only Windows voice-to-text utility. Hold (or tap) a hotkey → speak → release → the transcribed text is pasted into whatever field has focus. Runs **entirely on-device** via NVIDIA **Parakeet V3** (ONNX, through sherpa-onnx). No cloud, no API keys.

It is a **clean reimplementation, not a recompile** — the macOS app is SwiftUI/AppKit and every subsystem sits on an Apple-only framework. The *code* doesn't carry over; the *design and behaviour* do, and behaviour with a documented spec on macOS is ported 1:1 and pinned by tests.

**Target OS:** Windows 10 1809+ / Windows 11. **Stack:** C# / .NET 10, WPF (+ WinForms tray). **Lang:** C#.

---

## Current status (read before working)

| Layer | State |
|---|---|
| Core: transcription pipeline, filler filter, personal dictionary, audio | ✅ Done. Built + tested on macOS and CI. |
| On-device Parakeet V3 transcription (ONNX) | ✅ **Proven** — transcribes a 5 s clip in ~180 ms on macOS arm64. |
| Windows app: tray, hotkey, mic, paste, music-pause, lozenge, settings | ✅ **Compiles + publishes + packages an installer on CI.** Runtime behaviour NOT yet smoke-tested on real Windows. |

**The next real work is the Windows smoke-test** (does the hotkey fire, does paste land, mic quality, SMTC). The checklist is [docs/WINDOWS_TESTING.md](docs/WINDOWS_TESTING.md). Everything that can be verified without a human pressing keys is already verified.

---

## How this project is developed & verified

This is the unusual part. There is **no local Windows machine**. Two things make that work:

1. **The Core (the hard part — ASR engine + text processing) runs everywhere.** sherpa-onnx ships `osx-arm64`, `win-x64`, and `linux-x64` native runtimes behind one managed API, so the transcription pipeline can be built, run, and tested on macOS/Linux. The CLI harness (`Shhhcribble.Cli`) transcribes a `.wav` to prove it end-to-end off-Windows.
2. **The Windows-only app is built on real Windows by CI.** `.github/workflows/ci.yml` has a `windows-latest` job that builds the WPF app, runs tests, publishes a self-contained `win-x64` bundle, and packages an Inno Setup installer — uploaded as artifacts. The maintainer's Mac can even *compile* the Windows project locally thanks to `<EnableWindowsTargeting>true</EnableWindowsTargeting>` (catches compile errors without a CI round-trip), but **cannot run it** (no Windows desktop runtime on macOS).

**What CI cannot do:** press keys, check paste lands in real apps, judge mic quality. That is the human-on-Windows step.

### Continuing on a Windows machine (the intended handoff)

The chat transcript does **not** sync across machines — the repo + this file are the handoff. On a fresh Windows box:

```powershell
# Prereqs: install Git, the .NET 10 SDK (https://dotnet.microsoft.com/download),
# and (optionally) Claude Code. Then:
git clone https://github.com/OsamaBinBallZak/shhhcribble-windows
cd shhhcribble-windows

dotnet test  tests/Shhhcribble.Core.Tests          # 20 parity tests, should pass
dotnet run   --project src/Shhhcribble.Windows      # launches the tray app (Windows only)
```

First launch downloads the ~480 MB model to `%LOCALAPPDATA%\Shhhcribble\models` (once). A fresh Claude Code session started in this directory will read this file and be fully oriented. Then work through [docs/WINDOWS_TESTING.md](docs/WINDOWS_TESTING.md) and fix what breaks.

---

## How it works (end-to-end recording flow)

`AppController` owns every subsystem and the recording state machine (the Windows analogue of the macOS `AppDelegate`):

1. **Startup** — load settings, show tray icon, install the global keyboard hook, kick off async model download+load (tray shows "Loading…" → "Ready").
2. **Hotkey** — `GlobalHotkey` (low-level `WH_KEYBOARD_LL` hook) fires `ComboDown`/`ComboUp` on the UI thread. `AppController` maps these to start/stop per the activation mode:
   - **hybrid** (default): quick tap = latch on/off (toggle); press-and-hold = record while held, release stops (push-to-talk). 300 ms tap threshold.
   - **toggle**: press toggles; **pushToTalk**: down starts, up stops.
3. **Record** — `MicRecorder` captures the default mic via WASAPI (`WasapiCapture`), fresh per recording, downmixed + resampled to 16 kHz mono float. The lozenge shows animated bars. Music pauses via SMTC if enabled.
4. **Stop** — capture stops; samples go to `ParakeetTranscriptionEngine.Transcribe` on a worker thread.
5. **Post-process** — `PersonalDictionary.Apply` then (if enabled) `FillerWordFilter.Filter`.
6. **Paste** — `TextInserter` writes to the clipboard, sends Ctrl+V via `SendInput`, then restores the prior clipboard after 2 s (if unchanged). Result added to history; lozenge shows "Copied ✓".
7. **Escape** — while recording, a global Esc cancels (discard, no paste).

---

## Directory map

```
src/Shhhcribble.Core/            net10.0 — portable, NO OS deps. Runs on Win/Mac/Linux + CI.
  Audio/WavAudio.cs                 WAV decode + linear resample to 16 kHz mono
  Text/FillerWordFilter.cs          um/uh/you-know stripping (ported 1:1, tested)
  Text/PersonalDictionary.cs        phrase→replacement substitution (ported 1:1, tested)
  Text/DictionaryEntry.cs           JSON-compatible with the macOS dictionary format
  Transcription/ParakeetModels.cs       model registry (V3 multilingual / V2 English)
  Transcription/ModelDownloader.cs       download + bz2/tar-extract + cache on first use
  Transcription/ParakeetTranscriptionEngine.cs   sherpa-onnx offline transducer wrapper

src/Shhhcribble.Cli/             net10.0 — transcribe a .wav (the off-Windows proof harness)

src/Shhhcribble.Windows/         net10.0-windows10.0.19041.0 (WPF) — the desktop app
  App.xaml(.cs)                     tray-only app entry (no main window)
  AppController.cs                  owns subsystems + recording state machine + hybrid logic
  SettingsStore.cs                  JSON settings in %APPDATA%\Shhhcribble\settings.json
  Input/GlobalHotkey.cs             WH_KEYBOARD_LL hook (sees key-up, needed for hold) + Esc
  Input/HotkeyOptions.cs            hotkey presets (default Ctrl+Space)
  Audio/MicRecorder.cs              WASAPI capture, fresh per recording
  TextInsertion/TextInserter.cs     clipboard + SendInput Ctrl+V + clipboard restore
  Media/MusicPauser.cs              SMTC pause/resume (Spotify, browsers, etc.)
  UI/LozengeWindow.xaml(.cs)        borderless, click-through, non-activating soundwave pill
  UI/TrayIcon.cs                    WinForms NotifyIcon + menu (status/activation/model/history)
  UI/SettingsWindow.xaml(.cs)       model/hotkey/activation, filler + music toggles, dictionary editor

tests/Shhhcribble.Core.Tests/    xUnit — filler + dictionary parity tests (ported from XCTest)
installer/Shhhcribble.iss        Inno Setup per-user installer (no admin)
.github/workflows/ci.yml         Core tests (Linux) + full Windows build/installer (windows-latest)
```

---

## Load-bearing decisions & Windows gotchas (don't relitigate without reading why)

- **WPF + WinForms together** → `Application`, `Clipboard`, `Rectangle`, `Color` are ambiguous between namespaces. Resolved with per-file `using X = …;` aliases. If you add a file that mixes both, expect to alias.
- **Low-level keyboard hook, not RegisterHotKey** — RegisterHotKey delivers only key-down; the hybrid "hold = push-to-talk" needs key-up, so we use `WH_KEYBOARD_LL`. The hook is installed on the WPF UI thread (which pumps messages), so its callbacks arrive on the UI thread. The trigger key is swallowed (return 1) while modifiers are held so it doesn't type a stray space.
- **Fresh mic capture per recording** — `MicRecorder.Start()` disposes any prior capture and grabs the current default device, mirroring the macOS "fresh AVAudioEngine per recording" rule so a device swap heals on the next press. Do not keep capture warm across recordings.
- **Paste = clipboard + Ctrl+V, not UIA SetValue** — UIA `ValuePattern.SetValue` replaces an entire field, which would clobber existing text. There is no reliable universal "insert at caret" API on Windows, so we use the clipboard + simulated Ctrl+V (the same path the macOS app falls back to) and restore the prior clipboard after 2 s.
- **Music pause via SMTC** — covers any SMTC-aware source (Spotify, browser/YouTube, Groove), broader than the macOS AppleScript path. Only sessions we actually paused are resumed.
- **Self-contained publish** — the published app bundles the .NET runtime + native `onnxruntime.dll`/`sherpa-onnx*.dll`, so friends need nothing pre-installed. The speech model still downloads on first run.
- **`EnableWindowsTargeting`** — lets the Windows project restore/compile on macOS for the dev loop; the real build + packaging happens on Windows CI.

---

## Known risk areas (smoke-test these first on Windows)

These are untested at runtime and are the most likely to need a fix:
1. **Mic capture format** — `MicRecorder` handles float + 16-bit PCM WASAPI streams. If a recording is silent/garbled, the device mix format is the first suspect.
2. **Paste targets** — Electron/Win32/browser hosts vary in how they accept synthetic Ctrl+V. Test Notepad, a browser, and an Electron app (VS Code/Slack).
3. **Hotkey conflicts** — Ctrl+Space collides with some IMEs; offer a different preset if so.
4. **WASAPI stop tail** — `Stop()` waits 500 ms for final buffers; lengthen if the last word clips.

---

## Commands

```bash
dotnet test  tests/Shhhcribble.Core.Tests                         # parity tests (any OS)
dotnet run   --project src/Shhhcribble.Cli -- audio.wav           # transcribe a wav (any OS)
dotnet build src/Shhhcribble.Windows/...csproj -c Release         # compiles on Mac too (EnableWindowsTargeting)
dotnet run   --project src/Shhhcribble.Windows                    # run the app (WINDOWS ONLY)
dotnet publish src/Shhhcribble.Windows/...csproj -c Release -r win-x64 --self-contained true -o publish/Shhhcribble
```

On this maintainer's Mac, dotnet lives at `/opt/homebrew/bin/dotnet` (Homebrew) — prepend it to PATH if `dotnet` isn't found.

---

## Scope (per the project owner)

**In:** core dictation loop, hybrid/toggle/push-to-talk activation, on-device Parakeet, filler filter, Personal Dictionary, SMTC music-pause, recent history, Escape-to-cancel, a plain one-time installer.
**Cut:** transcript cleanup (the on-device LLM), auto-update, translation.

---

## The macOS source of truth (reference)

The original Swift app is the behavioural reference. On the maintainer's Mac it's the sibling repo `../Shhhcribble` (remote `upstream` = `github.com/itsHendri/Shhhcribble`, branch `shhhcribble/main`). When porting a behaviour, read the corresponding Swift file and its `CLAUDE.md` decision notes there. From elsewhere, fetch from that GitHub repo.
