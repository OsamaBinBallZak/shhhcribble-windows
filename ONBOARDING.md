# Start here — Shhhcribble for Windows

You're looking at a **Windows port of [Shhhcribble](https://github.com/itsHendri/Shhhcribble)**, Henry's macOS hold-a-hotkey-and-dictate app. Hold (or tap) a hotkey → speak → release → the transcription pastes into whatever field has focus. Runs **fully on-device** (NVIDIA Parakeet V3 via ONNX) — no cloud, no keys.

This is a clean C#/.NET rebuild, not a recompile. It was built and validated on a Mac + GitHub Actions; the engine even transcribes on macOS to prove itself. **Everything compiles, tests pass, and CI packages a Windows installer.** The one thing left is a hands-on smoke test on real Windows.

---

## Just want to run it? (no dev setup)

1. Grab `Shhhcribble-Setup.exe` — from the **Actions** tab → latest green run → Artifacts → `Shhhcribble-Setup`, or:
   ```powershell
   gh run download --repo OsamaBinBallZak/shhhcribble-windows -n Shhhcribble-Setup
   ```
2. Run it (per-user, no admin). SmartScreen will warn (unsigned) → **More info → Run anyway**.
3. A tray icon appears; the speech model (~480 MB) downloads once on first launch. Then hold **Ctrl+Space**, speak, release.

---

## Continuing development / testing (with Claude Code on Windows)

```powershell
# Install: Git, the .NET 10 SDK, Claude Code. Then:
git clone https://github.com/OsamaBinBallZak/shhhcribble-windows
cd shhhcribble-windows
claude                                    # a fresh session reads CLAUDE.md and is fully caught up

dotnet test tests/Shhhcribble.Core.Tests  # 20 parity tests — should pass
dotnet run  --project src/Shhhcribble.Windows   # launch the app (Windows only)
```

Then work through the smoke-test checklist and fix whatever breaks. The build/verify loop is wired: every push to `main` runs CI and produces a fresh tested installer.

---

## The docs (all in this repo)

| Doc | What it's for |
|---|---|
| **[CLAUDE.md](CLAUDE.md)** | The full handoff — how every subsystem works, the recording flow, design decisions, known risks, all commands. Auto-read by any Claude session here. |
| **[docs/WINDOWS_TESTING.md](docs/WINDOWS_TESTING.md)** | The click-through smoke-test checklist for real Windows. |
| **[README.md](README.md)** | Overview, architecture, the macOS→Windows subsystem map, download/install. |

**To pick this up cold: open this repo in Claude Code and say "read CLAUDE.md and let's continue the Windows smoke test."** That's the whole onboarding.
