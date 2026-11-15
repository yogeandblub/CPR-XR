# Integration with the team pipeline

Two separate repositories, side by side:

```
~/Documents/Personal-GitHub/
├── immerse_the_bay_2026/   team repo: transcript -> facts -> dispatcher briefing (Python, laptop/AWS)
└── CPR-XR/                 this repo: the Quest 3 Unity app (CPR guidance on the headset)
```

This repo does not modify the team repo. It follows their published contract.

## Contract version

`Assets/CPR/Scripts/Integration/XrAedClient.cs` matches `immerse_the_bay_2026` at commit **44374ec**
(`api/openapi.yaml`, `api/request-examples.json`, `docs/TEAM_HANDOFF.md`).
When their contract changes: pull their repo, diff those files, update the client and the commit id here.

## What the headset sends (and doesn't)

| Sends | Type / source | When |
|---|---|---|
| Final Meta speech-to-text line | `speech_transcript` / `operator_report` | after a final transcript (not wired yet: needs Meta `SpeechToTextAgent`) |
| Explicit help request | `help_request` / `app` | a deliberate UI action only |
| Location | `location_update` / `companion_phone` | only an authorized fix; otherwise unknown |
| Test call | `POST /sessions/{id}/calls/test` -> `test_dispatcher` | simulation only |

Never sent: patient facts, BlazePose landmarks, body-size estimates, compression data, camera frames.
On-device CPR guidance (button, kneel spot, ghost hands, coaching) stays on the headset.

The client is **offline by default** (`baseUrl` empty): each request is logged as
`CPRNet: would POST ...` with its exact JSON. No token is stored in the project.

## Open item for the team

Their `VISION.md` stage 2 expects a **YOLOv9 person count** from the Quest before spending a moondream2
call. This app removed YOLO (battery) and uses BlazePose's own detector, which tracks one person:
it can provide `person_visible` (yes/no) but not a count. Options: (a) accept person_visible only,
(b) run YOLO at a low rate only after an emergency starts, (c) count from BlazePose detector boxes.

## Not connected yet (either side)

- No server receives events: the team code reads `runtime/session.json` on a laptop; `openapi.yaml` is a spec.
- Meta `SpeechToTextAgent` (voice trigger + transcripts) is not in this project yet.
- Quest -> laptop over plain HTTP would need Android cleartext permission; prefer HTTPS.
