# Shhhcribble for Windows

A Windows port of [Shhhcribble](https://github.com/itsHendri/Shhhcribble) — Henry's
menu-bar macOS voice-to-text utility. Hold a hotkey → speak → release → the
transcribed text is pasted into whatever field is focused. Runs **entirely
on-device** via NVIDIA **Parakeet V3** (ONNX); no cloud, no API keys.

This is a **clean reimplementation**, not a recompile: the macOS app is
SwiftUI/AppKit and every subsystem sits on an Apple-only framework, so the *code*
doesn't carry over — but the *design and behaviour* do, faithfully. Behaviour
that has a documented spec on macOS is ported 1:1 and pinned by tests.

## Status

| Layer | State |
|---|---|
| **Core** — transcription pipeline, filler filter, personal dictionary, audio | ✅ Done, tested on macOS + CI |
| **On-device Parakeet V3 transcription (ONNX)** | ✅ **Proven** — transcribes on macOS in ~180 ms / 5 s clip |
| **Windows app** — tray, hotkey, mic, paste, music-pause, settings UI | 🚧 Next phase (builds on the `windows-latest` CI runner) |

### Verification model

The author develops on a Mac and has no local Windows machine, so:
- The **portable Core** (the hard part — the ASR engine + text processing) is
  built and tested on macOS *and* in CI. sherpa-onnx ships `osx-arm64` and
  `win-x64` native runtimes behind one managed API, so the same code runs both
  places.
- The **Windows-only app** is built, tested, and packaged into a downloadable
  installer by GitHub Actions on a real `windows-latest` runner (see
  [.github/workflows/ci.yml](.github/workflows/ci.yml)). Interactive
  hotkey/paste/mic smoke-testing is the one step that still needs a human on
  Windows.

## Architecture

```
Shhhcribble.sln
├── src/Shhhcribble.Core/        net10.0  — portable, no OS deps. Runs on Mac + Win + CI.
│   ├── Audio/WavAudio.cs            WAV decode + resample to 16 kHz mono float
│   ├── Text/FillerWordFilter.cs     um/uh/you-know stripping (ported 1:1)
│   ├── Text/PersonalDictionary.cs   phrase→replacement substitution (ported 1:1)
│   ├── Text/DictionaryEntry.cs      JSON-compatible with the macOS dictionary
│   └── Transcription/
│       ├── ParakeetModels.cs            model registry (V3 multilingual / V2 English)
│       ├── ModelDownloader.cs           download + bz2/tar-extract + cache on first use
│       └── ParakeetTranscriptionEngine.cs  sherpa-onnx offline transducer wrapper
├── src/Shhhcribble.Cli/         net10.0  — drives the Core pipeline from a .wav (the macOS proof harness)
├── src/Shhhcribble.Windows/     net10.0-windows (WPF) — the desktop app  [🚧 next phase]
└── tests/Shhhcribble.Core.Tests/         xUnit — filler + dictionary parity tests
```

### macOS → Windows subsystem map

| Subsystem | macOS (Henry's app) | Windows plan | State |
|---|---|---|---|
| Transcription | Parakeet V3 on CoreML (FluidAudio) | Parakeet V3 on ONNX (sherpa-onnx) | ✅ |
| Filler filter / dictionary | Swift regex | C# regex (1:1, tested) | ✅ |
| Audio capture | AVAudioEngine | NAudio (WASAPI) | 🚧 |
| Global hotkey | Carbon RegisterEventHotKey | `RegisterHotKey` / low-level kbd hook | 🚧 |
| Paste into focused app | Accessibility API + ⌘V | UI Automation + `SendInput` (Ctrl+V) | 🚧 |
| Menu-bar UI | NSStatusItem | tray icon (WPF + Win32 NotifyIcon) | 🚧 |
| Soundwave lozenge | NSPanel + SwiftUI | borderless topmost WPF window | 🚧 |
| Music pause | AppleScript → Spotify/Music | Windows SMTC (covers browsers too) | 🚧 |

Cut for this port (per project owner): transcript cleanup (the on-device LLM),
auto-update, translation.

## Build & run

Requires the .NET 10 SDK.

```bash
# Run the Core test suite (cross-platform)
dotnet test tests/Shhhcribble.Core.Tests

# Transcribe a .wav with the real on-device model (downloads ~487 MB on first run)
dotnet run --project src/Shhhcribble.Cli -- path/to/audio.wav
dotnet run --project src/Shhhcribble.Cli -- audio.wav --model parakeet-v2
```

The Windows desktop app builds only on Windows (it references Windows-only
frameworks); the CI workflow produces a ready-to-run build + installer artifact.

## Credits

Original macOS app © Henry (`itsHendri`). Transcription by
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) / NVIDIA Parakeet.
