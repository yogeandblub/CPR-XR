# CPR XR

Mixed-reality CPR guidance for **Meta Quest 3**. A bystander says **"SOS"**: the headset calls a
dispatcher with a spoken summary of what the bystander described, then walks them through hands-only
CPR on the real person in front of them, step by step, with markers placed on the person's body.
A **Practice** mode runs the same protocol on a virtual mannequin.

Built for the Immerse the Bay 2026 hackathon.

> **Prototype — not a medical device.** It calls one preset **demo** phone number, never real
> emergency services. In a real emergency, call your local emergency number yourself.

---

## Contents

- [What it does](#what-it-does)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Setup](#setup)
- [Running a session](#running-a-session)
- [Using the app](#using-the-app)
- [Configuration](#configuration)
- [Logs and debugging](#logs-and-debugging)
- [Troubleshooting](#troubleshooting)
- [Project layout](#project-layout)
- [Privacy and safety](#privacy-and-safety)
- [Credits and licenses](#credits-and-licenses)

---

## What it does

### SOS mode (real emergency flow)

1. **Voice trigger.** Say "SOS". Until then the headset shows nothing but passthrough.
2. **Describe the scene.** A short question appears ("What happened to them?"). The headset listens
   for about 8 seconds; speech is transcribed on the laptop *while* you talk.
3. **Dispatcher call.** A call is placed to the demo phone. The dispatcher hears an automated summary:
   the facts extracted from your words (e.g. "not breathing", "unresponsive"), your relevant
   sentences, and your location. The phone is then joined to a live audio conference with the
   headset, so the dispatcher can talk to the rescuer. A small head-locked box shows the call status
   and elapsed time.
4. **Guided CPR protocol**, in order:
   1. **Kneel** at the green box beside the person (found with on-device body tracking).
   2. **Check for responsiveness** — 4 seconds, with a looping 2-second demo of tapping the shoulders
      and shouting "Are you OK?".
   3. **Hand placement** — ghost hands show where and how to interlock your hands; a button marks the
      compression point on the lower half of the breastbone, centred on the body's midline.
   4. **Compressions** with a 120 bpm metronome for 30 seconds (hidden timer). The HUD shows the
      compression count, the depth in inches (Deeper / Shallower / Perfect) and arm form
      ("Lock your elbows", "Slipping…").
   5. **Recovery position** — an outline of a person in the recovery position appears where the patient
      lies. The step completes when the person is seen rolled onto their side (generous check).
   6. **Done** — "Keep a close eye on the victim while waiting for first responders to arrive."
5. **Live feed.** The headset camera streams to the laptop at `http://localhost:8077/live`, together
   with the transcript and the call message.

### Practice mode (training)

Say **"PRACTICE"**. A menu follows you about two feet in front; pick **Adult**, **Child** or **Infant**
and a realistic mannequin appears on the floor (point + pinch or the controller trigger to press
buttons or move the mannequin). The same protocol runs, with the same head-locked HUD, but the call
is **simulated**: the laptop builds the exact message the dispatcher would hear and nothing is dialed.

### Anywhere

- **Kill switch:** hold the left **☰ menu button for 1.5 s** (or the left-hand palm pinch), or say
  **"restart"**. It hangs up any call and restarts the app in Idle.

---

## How it works

```
┌──────────────── Meta Quest 3 ────────────────┐          ┌──────────────── Laptop ────────────────────┐
│ Unity app (com.cprxr.prototype)              │          │ bridge/xr_aed_bridge.py  (port 8077)        │
│  • passthrough camera → BlazePose (on GPU)   │  speech  │  • faster-whisper speech-to-text            │
│    → lying person, body size, chest point    │ ───────► │  • team pipeline: transcript → facts        │
│  • kneel box, compression button, ghost hands│  camera  │  • builds the dispatcher message            │
│  • voice activity detection → WAV chunks     │ ───────► │  • live feed page  /live                    │
│  • SOS / Practice HUD, metronome, steps      │   logs   │  • places the call via the call server code │
│  • hidden WebView voice client (call bridge) │ ───────► │                                             │
└──────────────────────────────────────────────┘          │ team call server (port 8080, Twilio)        │
          ▲  Wi-Fi: UDP discovery on 8078, or an          │  • calls the demo phone, speaks the report  │
          │  internet relay when the network blocks       │  • voice conference: phone ↔ headset        │
          └─ device-to-device traffic (no cable needed)   └─────────────────────────────────────────────┘
```

- **On the headset:** everything visual — person detection (Unity Inference Engine running BlazePose),
  the kneel spot, compression point, ghost hands, compression depth/rate from hand tracking, the HUD
  and the metronome. No cloud is needed for guidance.
- **On the laptop:** speech-to-text, the team's fact extraction and summary, call placement, the live
  feed page, and a log sink so the headset can be debugged without a cable.
- **Finding the laptop:** the headset listens for a UDP broadcast on the local network. On networks that
  block device-to-device traffic (e.g. campus guest Wi-Fi) the bridge opens a Cloudflare quick tunnel and
  posts its address, scrambled with a shared key, to an ntfy.sh topic the headset polls. Every request
  carries the shared key from `bridge/config.json`.

---

## Requirements

| What | Version / notes |
|---|---|
| Headset | Meta Quest 3, developer mode on |
| Unity | **6000.3.10f1** with Android Build Support (IL2CPP, ARM64) |
| Laptop | macOS (tested); Python 3.12+ from python.org; ~2 GB free for the speech model |
| Call server | The team's Quest CPR call server (`QuestCPR-Full`) with its `backend/.env` (Twilio) |
| Team pipeline | The team repo `immerse_the_bay_2026` (fact extraction), cloned next to this one or in `~/Documents/Personal-GitHub` |
| Git LFS | Required: models, textures and the mannequin are stored in LFS (`git lfs install`) |

> **Keep the project out of iCloud Drive.** Do not put it in `~/Documents` or `~/Desktop` when
> "Optimize Mac Storage" is on: macOS replaces files with cloud placeholders, which freezes Unity and
> breaks the bridge's HTTPS calls. `~/Developer/` is a good place.

---

## Setup

### 1. Clone

```bash
git lfs install
git clone https://github.com/yogeandblub/CPR-XR.git ~/Developer/CPR-XR
```

### 2. Build and install the headset app

1. Open `~/Developer/CPR-XR` in Unity 6000.3.10f1 (Unity Hub → Add → Add project from disk).
2. Switch the platform to **Android** if asked.
3. Plug in the Quest and allow USB debugging.
4. Menu **CPR → Build Pose Test APK**. The APK is written to `Builds/CPR-PoseTest.apk`.
5. Install and grant permissions:

```bash
ADB=$(ls /Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb | tail -1)
"$ADB" install -r Builds/CPR-PoseTest.apk
for p in com.oculus.permission.USE_SCENE horizonos.permission.HEADSET_CAMERA android.permission.RECORD_AUDIO \
         android.permission.ACCESS_FINE_LOCATION android.permission.ACCESS_COARSE_LOCATION; do
  "$ADB" shell pm grant com.cprxr.prototype $p
done
```

On the headset the app is under **Library → Unknown Sources → CPR XR**.

### 3. Laptop bridge configuration

Create `bridge/config.json` (local only, git-ignored):

```json
{
  "device_key": "<the shared key baked into the scene's XrAedClient>",
  "call_backend": "~/path/to/QuestCPR-Full/backend"
}
```

- `device_key` must match the key on the `XrAedClient` component in `Assets/CPR/Scenes/PoseTest.unity`.
- `call_backend` points at the team call server's `backend/` folder (optional if `QuestCPR-Full` sits
  next to this repo or in `~/Documents/Personal-GitHub`). `CPRXR_CALL_BACKEND` overrides it.
- Optional `fallback_location`: `{"lat": .., "lon": .., "accuracy_m": ..}` announced as *simulated*
  when no real location is available.

The first run of `start_bridge.sh` creates a Python environment at `~/.cprxr/venv` (outside iCloud) and
downloads the `small.en` speech model (~460 MB).

---

## Running a session

Start two things on the laptop, in two terminals:

```bash
# 1. Team call server (Twilio) + its public tunnel — prints a "Quest 3 browser" link
cd ~/path/to/QuestCPR-Full && ./start.sh

# 2. CPR-XR laptop bridge (speech, summary, call dispatch, live feed, relay)
cd ~/Developer/CPR-XR && ./bridge/start_bridge.sh
```

The bridge picks up the call server's current address automatically (it changes on every `start.sh`).

Then on the headset open **CPR XR**, wait ~10 s for it to reach the laptop, and say **"SOS"** or
**"PRACTICE"**. Watch `http://localhost:8077/live` on the laptop for the camera feed, transcript and call
message.

**Test without dialing:** `MOCK_TWILIO=true ./bridge/start_bridge.sh` runs everything except the real
call — use it for repeated runs to avoid provider rate limits.

---

## Using the app

| You do | Result |
|---|---|
| Say "SOS" | SOS mode: question → listen 8 s → dispatcher call → CPR protocol |
| Say "PRACTICE" | Practice menu; pick Adult / Child / Infant to spawn the mannequin and start |
| Point + pinch / trigger | Press menu buttons; move the practice mannequin |
| Menu ☰ (short press) | Open / close the practice menu |
| Menu ☰ held 1.5 s, or say "restart" | Kill switch: hang up and restart in Idle |
| Kneel at the green box | Starts the protocol steps |
| Interlock hands like the ghost hands | Starts compressions + metronome |

Compression detection is deliberately sensitive (a 1 cm push counts) so light practice pushes register.

---

## Configuration

Most tuning lives on components in `Assets/CPR/Scenes/PoseTest.unity`:

| Component | Field | Default | Meaning |
|---|---|---|---|
| `SOSController` | `listenSeconds` | 8 | How long to listen after "SOS" before calling |
| `SOSController` | `finishPhraseGrace` | 3 | Extra wait if the rescuer is mid-sentence |
| `CprStepFlow` | `responsivenessSeconds` | 4 | "Check for responsiveness" duration |
| `CprStepFlow` | `compressionTestSeconds` | 30 | Compressions before the recovery step (hidden timer) |
| `CprStepFlow` | `recoveryTimeoutSeconds` | 60 | Completes anyway if the roll is never recognised |
| `CompressionDisc` | `minStroke` / `strokeStart` | 0.01 / 0.006 m | Compression sensitivity |
| `PoseOverlayTest` | `adultCrownFraction` | 0.28 | Compression point: fraction of body length from the crown |
| `VoiceCommand` | `chunkAfter` / `chunkGap` | 2.5 s / 0.3 s | Send speech while the rescuer is still talking |
| `Metronome` | bpm | 120 | Compression rate |

Bridge environment variables: `MOCK_TWILIO=true` (no real calls), `CPRXR_LAN=0` (localhost only),
`CPRXR_RELAY=0` (no internet relay), `CPRXR_VENV` (Python env path), `CPRXR_CALL_BACKEND`,
`CPRXR_TEAM_DIR`.

---

## Logs and debugging

| Where | What |
|---|---|
| `bridge/headset.log` | Every `CPR…` log line from the headset, sent over Wi-Fi (headset clock) |
| Bridge terminal | Speech results, dispatch messages, call results, relay and location status |
| `http://localhost:8077/live` | Camera feed, transcript, call message, frame counter |
| `adb logcat -s Unity` | Full headset log (USB) |
| Headset file | `/sdcard/Android/data/com.cprxr.prototype/files/cpr-logs/` |

Useful log tags: `CPRSOS` (call flow), `CPRSteps` (protocol steps and HUD text), `CPRVoice` (what was
heard), `CPRCompress` (each compression with depth), `CPRKneel`, `CPRForm` (hand placement),
`CPRFeed`, `CPRNet`, `CPRKill`.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| App opens to a dark void and closes | Launcher activity missing from the build. The manifest must use `com.unity3d.player.UnityPlayerGameActivity` (see `Assets/Plugins/Android/AndroidManifest.xml`). |
| Unity freezes on open / compile | Project files evicted by iCloud (`find . -flags +dataless`). Move the project out of `~/Documents`, delete `Library/`, reopen. |
| "Call problem — check Wi-Fi" | Usually the provider, not Wi-Fi: check the bridge terminal. `SSLError` → Python env in iCloud (use `~/.cprxr/venv`); provider "rate limited" → wait or use `MOCK_TWILIO=true`. |
| Headset never says "laptop bridge reached" | Bridge not running, or `device_key` mismatch. On blocked networks it takes ~5–15 s via the relay. |
| Voice commands ignored | The bridge does speech-to-text; check it is running and the bridge log shows `CPRVoice: heard …`. |
| Live feed page stays "waiting for camera" | The feed only runs in SOS / Practice. Check `CPRFeed:` lines in `bridge/headset.log`. |
| Location "not available" in the call | Turn on location on the Quest, or allow "CPR-XR Location" in macOS Privacy & Security → Location Services. |

---

## Project layout

```
Assets/CPR/
  Scenes/PoseTest.unity          the app scene
  Scripts/
    CPRModeManager.cs            Idle / SOS / Practice modes, practice menu
    CprStepFlow.cs               protocol steps, HUD text, recovery placement and check
    PoseOverlayTest.cs           BlazePose on the passthrough camera; lying person, size, chest point
    KneelIndicator.cs            kneel box beside the patient
    CompressionDisc.cs           compression button, stroke/depth detection, posture feedback
    GhostHands.cs, HandPose*.cs  ghost-hand placement guide and recorder
    PracticeMannequin.cs         virtual patient for practice
    MannequinModel.cs            realistic mannequin model, posed in code
    RecoveryGhost.cs             recovery-position outline
    ResponsivenessDemo.cs        2-second responsiveness demo
    SosHud.cs / CPRHud.cs        head-locked HUD / world panel
    Metronome.cs                 120 bpm click
    KillSwitch.cs                restart gesture / voice
    Integration/
      SOSController.cs           SOS + practice voice flow, listening window, call
      VoiceCommand.cs            microphone, voice activity detection, chunked upload
      XrAedClient.cs             HTTP client for the bridge
      BridgeDiscovery.cs         UDP discovery + internet relay
      LiveFeed.cs, LogForwarder.cs, HeadsetLocation.cs
  Resources/Mannequin/           mannequin model, textures, material
  Editor/CPRProjectTools.cs      scene setup and the "Build Pose Test APK" menu
bridge/
  xr_aed_bridge.py               laptop bridge (HTTP server on 8077)
  start_bridge.sh                creates the Python env and starts the bridge
  location.swift                 macOS location helper
Assets/BlazePose/                Unity's BlazePose sample and models
Assets/PassthroughCameraApiSamples/   Meta's Passthrough Camera API samples (reference)
ThirdPartyNotices/               third-party licenses
INTEGRATION.md                   original contract notes with the team pipeline repo
```

---

## Privacy and safety

- **Never calls emergency services.** The call server only dials one configured demo number and refuses
  emergency numbers.
- **Secrets stay local.** `bridge/config.json` (device key) and the call server's `backend/.env`
  (provider credentials) are not in this repository. Don't commit them.
- **Voice and camera data:** speech is transcribed on the laptop; transcripts are kept in
  `bridge/sessions/` and `bridge/headset.log`, both git-ignored. The camera feed is only shown on the
  laptop's local page; it is not stored.
- **Relay:** the bridge's tunnel address is published scrambled with the shared key; requests without
  the key are refused.
- Guidance follows hands-only CPR basics for a training prototype. It has not been clinically validated.

---

## Credits and licenses

- Team project for **Immerse the Bay 2026**. Fact extraction and dispatcher summary: the team's
  `immerse_the_bay_2026` pipeline. Phone call and voice conference: the team's Quest CPR call server.
- **BlazePose** models and sample: Unity Technologies (Apache-2.0) — see `ThirdPartyNotices/`.
- **Passthrough Camera API samples:** Meta (Oculus SDK License) — see `ThirdPartyNotices/`.
- **Meta XR SDK / MR Utility Kit**, **Unity Inference Engine**: under their respective licenses.
- **Speech-to-text:** faster-whisper (`small.en`).
- **Mannequin model** (`Assets/CPR/Resources/Mannequin/`): third-party character model; check its
  license before redistributing.
