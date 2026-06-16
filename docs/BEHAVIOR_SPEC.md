# Behavior spec — the exact details (macOS ground truth → Windows status)

FEATURE_PARITY.md lists *what* exists. This file pins down *exactly how it
should look and behave* — the subtle stuff (pill position, waveform motion, live
text, timings) that gets lost when you rebuild from a description instead of the
source.

> **The macOS Swift source is the ground truth.** When a detail here is unclear,
> read the original: `itsHendri/Shhhcribble` →
> `Shhhcribble/UI/SoundwaveView.swift`, `Shhhcribble/UI/SoundwavePanel.swift`,
> `Shhhcribble/App/AppDelegate.swift`, `Shhhcribble/Audio/AudioRecorder.swift`.
> Every number below is quoted from those files.

**Status:** ✅ matches · ⚠️ differs (fix) · ⬜ missing on Windows

---

## 1. The recording flow (exact sequence + timings)

From `AppDelegate.swift`. State machine: `idle → recording → transcribing → idle`.

1. **Hotkey down (idle):** record the press timestamp, then start. Gate first:
   engine must be ready (else flash "not ready"); mic permission resolved
   **before** the pill shows (don't show "recording" over a permission prompt).
2. **Pill appears only when the mic route is physically live** (`onReady`), not
   instantly — prevents the "first AirPods record is silent" glitch. ⬜ *Windows shows it immediately.*
3. **While recording:** real mic level drives the bars; a **live transcript**
   updates every **3.0 s** (needs ≥1 s of audio). ⬜ *Windows: bars are random, no live text.*
4. **Hotkey up:** if held **≥ 0.5 s** → stop (push-to-talk). Quick tap → keep
   recording (toggle); next tap stops. ⚠️ *Windows uses 0.3 s — should be 0.5 s.*
5. **On stop:** play the scribble sound **immediately** (rate 1.4) ⬜; flip pill to
   **"Transcribing…"** with a spinner (no auto-hide) ⬜; keep recording a
   **350 ms tail** so the last word isn't clipped ⬜; then stop capture.
6. **Resume music** after the audio settles (transport-aware delay on macOS). ✅ *(SMTC; Windows resumes simply.)*
7. **Process:** `raw → PersonalDictionary → FillerWordFilter → result`. ✅
8. **Paste:** wait **150 ms** for focus to settle ⬜, capture the frontmost app
   **at paste time** (so you can start in one app and paste into another),
   insert, then flip pill to **"Copied!"** (1 s auto-hide). ✅ *(auto-paste works; no 150 ms settle / explicit target.)*
9. **Escape while recording** → cancel: discard, no paste, resume music. ✅
10. Empty transcript → **"No speech detected"**; failure → **error** pill. ✅

---

## 2. Activation gesture (exact)

| Detail | macOS value | Windows |
|---|---|---|
| Hold threshold (hold vs tap) | **0.5 s** | ⚠️ 0.3 s — change to 0.5 s |
| Tap = latch on, tap again = stop | yes | ✅ |
| Hold = record while held, release = stop | yes | ✅ |

---

## 3. The pill / lozenge — exact visual spec

All from `SoundwaveView.swift` + `SoundwavePanel.swift`.

### Geometry & position
| Property | macOS value | Windows |
|---|---|---|
| Visible pill size | **320 × 56**, Capsule (fully rounded) | ⚠️ approximate |
| Container (anim overshoot room) | 400 × 136, transparent | — |
| **Screen position** | **TOP-CENTER** — horizontally centered, pill ~8 px below the top edge of the work area | ❌ **Windows puts it at the BOTTOM** — move to top-center |
| Background fill | `rgb(0.10, 0.10, 0.12)` @ 0.94 opacity | ⚠️ close |
| Shadow | radius 16–18, y-offset 6–7, black @ 0.45–0.5 | ⚠️ |
| Border | state-color halo, 0.75 pt, @ 0.28 opacity | ⬜ |

### Layout, left → right
`[16pt] icon (13pt) [10pt] · bars (w36) · live-text (12pt) · status-dot (22pt) [12pt]`

### Soundwave bars — *the motion matters*
- **7 bars**, width **3**, spacing **3**, corner radius 2, white @ 0.75, container height 36.
- Height = `4 + micLevel * 32 * normalised`, where
  `normalised = 0.65 + 0.35 * sin(phase + i*0.75)` and `phase += 0.35` every **50 ms**.
- → a smooth **sine wave that swells with your actual mic level**.
- ❌ **Windows uses random heights with no mic level** — replace with the formula above + feed real RMS level.

### Live transcript text (`ScrollingLiveText`)
- 12 pt, single line. Types **one char at a time, 70 ms/char** (the typing *is* the animation).
- Auto-scrolls to keep the latest char in view; **left & right edges fade** to transparent (gradient mask: clear→opaque at 12%, opaque→clear at 82%).
- The **just-completed word** is brighter (white @ 0.85) than the rest (@ 0.40).
- ⬜ **Missing on Windows entirely.** Requires live transcription (§1.3) feeding text.

### Status dot (`AnimatedDot`)
- 22 pt LED-style dot, color by state. During recording a **light-streak shimmer sweeps across it every 2.8 s** (linear, repeating). Stays lit otherwise.
- ⬜ **Missing on Windows.**

### Leading icon, by state
`mic.fill` (recording) · spinner (transcribing) · `checkmark.circle.fill` green (copied) · `waveform.slash` (no result) · `exclamationmark.triangle.fill` red (error). ⬜ *Windows has no leading icon.*

### State colors
recording = blue `rgb(.25,.55,1.0)` · transcribing = violet `rgb(.65,.50,1.0)` · copied = green · noResult = white @ 0.3 · error = red `rgb(1.0,.45,.45)`.

### Entry / exit animation
- Entry: spring(response 0.25, damping 0.75) — scale 0.78→1.0, offset y −18→0, opacity 0→1.
- Exit: spring(response 0.22, damping 0.88), then remove window after 0.3 s.
- ⬜ *Windows: plain show/hide, no spring.*

### State text & auto-hide
| State | Text | Auto-hide |
|---|---|---|
| recording | (bars + live text) | — |
| transcribing | "Transcribing…" + spinner | none (until replaced) ⬜ |
| copied | **"Copied! ⌘V to paste"** | 1.0 s ✅ (⚠️ Windows says "Copied ✓" — use "Copied! Ctrl+V to paste") |
| noResult | "No speech detected" @ 0.7 | 1.0 s ✅ |
| error | message, red, 1 line | 1.6 s ✅ |

---

## 4. Sounds
- One scribble sound (`shhhcribble-scribble-sound.mp3`), played at **rate 1.4** the
  instant the hotkey is released (before transcription). The only audible feedback.
- ⬜ **Missing on Windows.**

---

## 5. Audio capture details
| Detail | macOS | Windows |
|---|---|---|
| Fresh capture per recording (rebind default device) | yes | ✅ |
| 16 kHz mono float | yes | ✅ |
| Live RMS level callback → bars | yes | ⬜ not exposed |
| 350 ms tail after release before stopping | yes | ⬜ (only a 500 ms buffer flush) |
| Mid-recording device-route rebuild | yes | ⬜ heals only on next press |

---

## 6. Paste details (`TextInserter`)
| Detail | macOS | Windows |
|---|---|---|
| **Auto-paste into focused field** | Cmd+V (after AX attempt) | ✅ Ctrl+V via SendInput |
| Capture target app **at paste time** (start-in-A, paste-in-B) | yes | ⚠️ relies on current focus |
| 150 ms focus-settle delay before paste | yes | ⬜ pastes immediately |
| Restore prior clipboard after 2 s (if unchanged) | yes | ✅ |

---

## 7. Quick-fix list for the Windows port (cheap parity wins)
1. Move the pill to **top-center** (it's at the bottom). — `LozengeWindow.Reposition()`
2. Change hold threshold **0.3 s → 0.5 s**. — `AppController.TapThresholdMs`
3. Replace **random bars with the sine+level formula**; feed real mic RMS. — `LozengeWindow` + `MicRecorder`
4. Copied text → **"Copied! Ctrl+V to paste"**. — `LozengeWindow.ShowCopied`

## 8. Bigger items (need design + Windows runtime testing)
- Live transcript typing in the pill (§3 + live transcription §1.3)
- Animated LED status dot + leading state icon + spring entry/exit
- "Transcribing…" spinner state
- Completion sound
- 350 ms recording tail; mic RMS metering; mid-recording device rebuild

**Bottom line:** the engine + text pipeline + auto-paste + music-pause are
faithful; the **lozenge is the area that drifted** from the macOS original.
Build it against `SoundwaveView.swift`, not from memory.
