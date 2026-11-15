#!/usr/bin/env python3
"""
Laptop bridge between the CPR-XR headset app (this repo) and two team projects, both used read-only:
  ../immerse_the_bay_2026      speech -> facts -> briefing (their transcriber / session / dispatcher code)
  ../QuestCPR-Full/backend     the demo dispatcher CALL (their SignalWire code, .env credentials, demo number only)

Implements the team's api/openapi.yaml subset on http://127.0.0.1:8077/v1 by calling THEIR code:
    POST /v1/sessions                       create a session (stored in bridge/sessions/<id>.json)
    POST /v1/sessions/{id}/events           append a source-labelled event (their session.append_event)
    GET  /v1/sessions/{id}/summary          their dispatcher.compose_summary(reduce_events(...))
    POST /v1/sessions/{id}/calls/test       simulated only - no call is ever placed
Bridge-only extension (headset audio -> their local speech-to-text path):
    POST /v1/sessions/{id}/speech?source=operator_report   body: 16 kHz mono PCM16 WAV
         -> faster-whisper (same settings as their scripts/transcribe_audio.py)
         -> their session.append_speech (facts from their transcriber.py)
         -> JSON: transcript, confidence, facts, unresolved, spoken_text
    POST /v1/sessions/{id}/dispatch         short summary + coordinates -> QuestCPR call to the demo phone
    GET  /v1/health                         includes the QuestCPR server's current https tunnel URL

Wi-Fi (default): listens on the local network and announces itself with a UDP broadcast on port 8078,
so the headset finds it without an IP address or a cable. Requests from other machines must carry the
shared key (header X-CPRXR-Key) from bridge/config.json; this Mac itself needs no key.
    POST /v1/log                            headset log lines -> bridge/headset.log (debug without USB)
    POST /v1/feed                           one JPEG camera frame from the headset (SOS live feed)
    GET  /v1/feed.mjpg, /live               live feed + status page for the laptop (http://localhost:8077/live)
    GET  /v1/status                         latest session transcripts, call, feed age
Any-Wi-Fi fallback (networks that block device-to-device traffic, e.g. guest Wi-Fi): the bridge opens a
Cloudflare quick tunnel and posts its (changing) https address to ntfy.sh under a secret topic derived
from the device key; the address is XOR-scrambled with the key. The headset reads it from ntfy.sh and
connects through the tunnel. Tunnelled requests ALWAYS need the key. Set CPRXR_RELAY=0 to disable.
Set CPRXR_LAN=0 for localhost-only (USB `adb reverse` still works).
Run with bridge/start_bridge.sh.
"""

import base64
import hashlib
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
import wave
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

HERE = Path(__file__).resolve().parent
def sibling(name):
    """A teammate's project folder: next to this repo, or in ~/Documents/Personal-GitHub ('<name>.nosync' too).
    CPRXR_TEAM_DIR overrides where to look."""
    bases = [Path(os.environ["CPRXR_TEAM_DIR"])] if os.environ.get("CPRXR_TEAM_DIR") else []
    bases += [(HERE / ".." / "..").resolve(), Path.home() / "Documents" / "Personal-GitHub"]
    for base in bases:
        for candidate in (base / f"{name}.nosync", base / name):
            if candidate.exists():
                return candidate
    return bases[0] / name


TEAM_REPO = sibling("immerse_the_bay_2026")
def _call_backend():
    """The team's call server folder: $CPRXR_CALL_BACKEND, else config.json "call_backend", else next to the repos."""
    path = os.environ.get("CPRXR_CALL_BACKEND")
    if not path:
        try:
            path = json.loads((HERE / "config.json").read_text()).get("call_backend")
        except Exception:
            path = None
    return Path(path).expanduser() if path else sibling("QuestCPR-Full") / "backend"


CALL_BACKEND = _call_backend()
SESSIONS = HERE / "sessions"
CONFIG = HERE / "config.json"   # optional, local only: {"fallback_location": {"lat":..,"lon":..,"accuracy_m":..}}
PORT = 8077
BEACON_PORT = 8078
HOST = "127.0.0.1" if os.environ.get("CPRXR_LAN", "1") == "0" else "0.0.0.0"
HEADSET_LOG = HERE / "headset.log"


def load_config():
    """bridge/config.json (local, gitignored). Creates the shared device key on first run."""
    cfg = json.loads(CONFIG.read_text()) if CONFIG.exists() else {}
    if not cfg.get("device_key"):
        cfg["device_key"] = secrets.token_hex(16)
        CONFIG.write_text(json.dumps(cfg, indent=2))
    return cfg


CFG = load_config()
DEVICE_KEY = CFG["device_key"]
RELAY_TOPIC = "cprxr-" + hashlib.sha256(("topic:" + DEVICE_KEY).encode()).hexdigest()[:32]
TUNNEL = {"url": None}
FEED = {"jpg": None, "t": 0.0, "frames": 0}
FEED_COND = threading.Condition()
LAST = {"session": None, "dispatch": None}

if not (TEAM_REPO / "scripts" / "session.py").exists():
    sys.exit(f"Team repo not found at {TEAM_REPO} - clone immerse_the_bay_2026 next to CPR-XR.")
sys.path.insert(0, str(TEAM_REPO / "scripts"))

# ---- the team's code (read-only) ----
from session import LOCAL_STT_PROVIDER, SPEECH_SOURCES, append_event, append_speech, load_events, reduce_events  # noqa: E402
from dispatcher import compose_summary  # noqa: E402
from transcriber import extract_transcript_facts  # noqa: E402
from transcribe_audio import DEFAULT_MODEL, _mean_word_confidence  # noqa: E402

EVENT_TYPES = {"speech_transcript", "help_request", "location_update", "aed_telemetry", "call_status"}
EVENT_SOURCES = {"operator_report", "dispatcher_confirmed", "companion_phone", "phone_server", "headset_service", "aed_telemetry", "app"}
SAFE_ID = re.compile(r"^[A-Za-z0-9_\-]{1,64}$")


_TEAM_COMMIT = None


def team_commit():
    """Team repo version - looked up once (with a timeout) so a stuck git can never block requests."""
    global _TEAM_COMMIT
    if _TEAM_COMMIT is None:
        try:
            _TEAM_COMMIT = subprocess.run(["git", "-C", str(TEAM_REPO), "rev-parse", "--short", "HEAD"],
                                          capture_output=True, text=True, timeout=5).stdout.strip() or "unknown"
        except Exception:
            _TEAM_COMMIT = "unknown"
    return _TEAM_COMMIT


def utc_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def log(msg):
    print(f"{datetime.now():%H:%M:%S} {msg}", flush=True)


# ---- speech-to-text: one model, loaded once in the background ----
class Stt:
    def __init__(self, model_name=DEFAULT_MODEL):
        self.model_name = model_name
        self.model = None
        self.error = None
        self.lock = threading.Lock()
        threading.Thread(target=self._load, daemon=True).start()

    def _load(self):
        try:
            t0 = time.time()
            from faster_whisper import WhisperModel
            log(f"loading speech model {self.model_name} (first run downloads ~460 MB)...")
            self.model = WhisperModel(self.model_name, device="cpu", compute_type="int8")
            log(f"speech model ready in {time.time() - t0:.1f}s")
        except Exception as exc:  # keep the bridge up for non-audio events
            self.error = str(exc)
            log(f"speech model failed to load: {exc}")

    def transcribe(self, wav_path):
        if self.model is None:
            raise RuntimeError(self.error or "speech model still loading")
        audio = read_wav_16k_mono(wav_path)  # decode ourselves: PyAV's decoder breaks on newer Pythons
        with self.lock:  # one transcription at a time
            # Same settings as the team's scripts/transcribe_audio.py transcribe_wav().
            segments, _ = self.model.transcribe(audio, language="en", vad_filter=True, beam_size=5, word_timestamps=True)
            segments = list(segments)
        text = " ".join(s.text.strip() for s in segments if s.text.strip()).strip()
        return text, _mean_word_confidence(segments)


def read_wav_16k_mono(path):
    """PCM16 WAV -> float32 samples in [-1, 1] at 16 kHz mono (what Whisper expects)."""
    import numpy as np
    with wave.open(str(path), "rb") as w:
        if w.getsampwidth() != 2:
            raise ValueError("WAV must be 16-bit PCM")
        rate, channels = w.getframerate(), w.getnchannels()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    if rate != 16000:  # simple linear resample (headset already sends 16 kHz)
        n = int(len(data) * 16000 / rate)
        data = np.interp(np.linspace(0, len(data) - 1, n), np.arange(len(data)), data).astype(np.float32)
    return data


STT = Stt()


# ---------------- location ----------------
_LOC_CACHE = {"t": 0, "value": None}


def get_location():
    """This Mac's position (headset is tethered to it), else config fallback (announced as simulated)."""
    # Ask macOS once per run: reuse a found location, and after a "no" don't ask (or prompt) again.
    if _LOC_CACHE["value"] and _LOC_CACHE["value"].get("location_source") == "device":
        return _LOC_CACHE["value"]
    if _LOC_CACHE.get("denied") or _LOC_CACHE.get("busy"):
        return _LOC_CACHE["value"]
    app = HERE / ".bin" / "CPRXRLocation.app"
    helper = HERE / ".bin" / "cprxr-location"
    loc = None
    if app.exists() or helper.exists():
        try:
            if app.exists():  # run as an app so macOS Location Services can authorise it (System Settings)
                out_file = Path(tempfile.gettempdir()) / "cprxr_loc.txt"
                out_file.unlink(missing_ok=True)
                _LOC_CACHE["busy"] = True
                try:
                    subprocess.run(["open", "-W", "-n", "--stdout", str(out_file), "--stderr", str(out_file), str(app)], timeout=70)
                finally:
                    _LOC_CACHE["busy"] = False
                out = out_file.read_text().strip() if out_file.exists() else ""
            else:
                out = subprocess.run([str(helper)], capture_output=True, text=True, timeout=15).stdout.strip()
            if out and not out.startswith("error"):
                lat, lon, acc = (float(v) for v in out.split(","))
                loc = {"latitude": lat, "longitude": lon, "location_accuracy_m": acc, "location_source": "device"}
            else:
                log(f"Mac location unavailable: {out or 'no output'}")
                if "denied" in out or "kCLErrorDomain error 1" in out:
                    _LOC_CACHE["denied"] = True
                    log("Mac location: not asking again this run. To use it: System Settings > Privacy & Security > "
                        "Location Services > turn on 'CPR-XR Location', then restart the bridge.")
        except Exception as exc:
            log(f"Mac location failed: {exc}")
    if loc is None and CONFIG.exists():
        fb = json.loads(CONFIG.read_text()).get("fallback_location") or {}
        if fb.get("lat") is not None and fb.get("lon") is not None:
            loc = {"latitude": float(fb["lat"]), "longitude": float(fb["lon"]),
                   "location_accuracy_m": fb.get("accuracy_m"), "location_source": "simulated"}
    _LOC_CACHE.update(t=time.time(), value=loc)
    return loc


# ---------------- short summary for the dispatcher ----------------
PULSE_PHRASES = ("no pulse", "don't feel a pulse", "can't feel a pulse", "can't find a pulse", "cannot find a pulse",
                 "no heartbeat", "not beating", "heart stopped")
# Looser negations the team's extractor misses ("they don't have a pulse", "don't seem to be breathing").
NO_PULSE_RE = re.compile(r"(\bno\b|n't|\bnot\b|\bwithout\b)[^.?!]{0,20}\b(pulse|heartbeat)")
NO_BREATH_RE = re.compile(r"(n't|\bnot\b|\bno longer\b|\bstopped\b)[^.?!]{0,25}\bbreathing")

# A phrase is passed to the dispatcher verbatim if it mentions the patient or the emergency;
# side talk ("let's see my phone") is left out.
DETAIL_RE = re.compile(
    r"\b(he|she|they|them|his|her|their|him|victim|person|patient|man|woman|guy|girl|boy|kid|child|baby|someone|somebody|"
    r"collaps\w*|fell|fall\w*|faint\w*|passed out|unconscious|conscious|respon\w*|breath\w*|pulse|heart\w*|chest|"
    r"bleed\w*|blood|head|hit|seizure|chok\w*|drown\w*|overdose|drug\w*|pills?|allerg\w*|diabet\w*|asthma|pain|"
    r"accident|crash\w*|car|bike|years? old|age|pregnant|cpr|aed|happened|injur\w*|hurt|wound\w*|vomit\w*|blue|pale)\b")
SWEAR_RE = re.compile(r"\b(fuck\w*|shit\w*|damn\w*|bitch\w*|crap)\b[,!]?\s*", re.I)
FILLER_RE = re.compile(r"\b(um+|uh+|erm|you know)\b,?\s*", re.I)


def speakable(text, limit=420):
    """Plain text the phone's text-to-speech can't choke on: contractions expanded, only letters,
    digits and basic punctuation kept, length capped (calls with long/odd text were failing)."""
    t = text.replace("\u2019", "'").replace("\u2018", "'")
    for a, b in (("won't", "will not"), ("can't", "cannot"), ("n't", " not"), ("'re", " are"), ("'m", " am"),
                 ("'ve", " have"), ("'ll", " will"), ("'d", " would"), ("'s", "s")):
        t = re.sub(re.escape(a), b, t, flags=re.I)
    t = re.sub(r"[^A-Za-z0-9 .,?\-]", " ", t)
    t = re.sub(r"\s+", " ", t).strip()
    return t if len(t) <= limit else t[:limit].rsplit(" ", 1)[0] + "."


def events_since_sos(events):
    """The session starts when the app opens, so drop everything said before the last "SOS"."""
    for i in range(len(events) - 1, -1, -1):
        e = events[i]
        text = (e.get("payload") or {}).get("transcript", "") if e.get("type") == "speech_transcript" else ""
        if re.search(r"\bs\.?\s?o\.?\s?s\b|\bpractice\b", text.lower()):  # the trigger word starts the run
            return events[i:]
    return events


def rescuer_details(transcripts, limit=220):
    """Everything relevant the rescuer said, cleaned of swearing/filler, for the first call message."""
    kept = []
    for t in transcripts:
        text = (t.get("text") or "").strip()
        if not text or re.fullmatch(r"\W*(\w+\W+){0,2}(s\.?\s?o\.?\s?s|practice)\W*", text.lower()):
            continue  # the trigger itself ("SOS", "um, SOS")
        if not DETAIL_RE.search(text.lower().replace("\u2019", "'")):
            continue
        text = FILLER_RE.sub("", SWEAR_RE.sub("", text)).strip(" ,")
        if text:
            kept.append(text.rstrip(".!? ") + ".")
    out = " ".join(kept)
    return out if len(out) <= limit else out[:limit].rsplit(" ", 1)[0] + "..."


def short_report(sid):
    """Facts sentence (team extractor + phrases it misses), then the rescuer's own relevant words."""
    events = events_since_sos(load_events(session_path(sid)))
    if not events:
        return "", []
    summary = compose_summary(reduce_events(events), created_at=utc_now())
    facts = {f["field"]: f["value"] for f in summary["facts"]}
    words = " ".join(t["text"] for t in summary["transcripts"]).lower().replace("\u2019", "'")
    parts = []
    age = facts.get("patient.age_group")
    sex = facts.get("patient.sex")
    who = " ".join(x for x in [age, {"male": "man", "female": "woman"}.get(sex) if age in ("adult", None) else None] if x)
    if facts.get("patient.responsive") is False:
        parts.append("is unresponsive")
    elif facts.get("patient.responsive") is True:
        parts.append("is responsive")
    breathing = facts.get("patient.breathing")
    if breathing == "absent" or NO_BREATH_RE.search(words):
        parts.append("is not breathing")
    elif breathing == "gasping":
        parts.append("is only gasping")
    if any(p in words for p in PULSE_PHRASES) or NO_PULSE_RE.search(words):
        parts.append("has no pulse")
    extras = []
    if facts.get("response.cpr_started") is True:
        extras.append("CPR has been started")
    if facts.get("aed.available") is True:
        extras.append("an AED is available")
    if not parts and not extras:
        sentence = "A bystander has activated the CPR system."
    else:
        subject = f"the patient{', an ' + who + ',' if who else ''}"
        sentence = "A bystander reports " + subject + " " + ", ".join(parts[:-1]) + (" and " if len(parts) > 1 else "") + (parts[-1] if parts else "needs help") + "."
        if extras:
            sentence += " " + " and ".join(extras).capitalize() + "."
    said = rescuer_details(summary["transcripts"])
    if said:
        sentence += " The bystander said: " + said
    return sentence, sorted(facts)


# ---------------- the team's call code (QuestCPR backend, read-only) ----------------
_CALL = {"module": None, "error": None}


def call_module():
    """Import QuestCPR-Full/backend/server.py (loads its own .env). Their demo-number safety checks apply."""
    if _CALL["module"] or _CALL["error"]:
        return _CALL["module"]
    try:
        if not (CALL_BACKEND / "server.py").exists():
            raise FileNotFoundError(f"{CALL_BACKEND} not found")
        sys.path.insert(0, str(CALL_BACKEND))
        import importlib
        _CALL["module"] = importlib.import_module("server")
        log(f"call backend loaded ({_CALL['module'].Config.CALL_PROVIDER}, mock={_CALL['module'].Config.MOCK_TWILIO})")
    except Exception as exc:
        _CALL["error"] = str(exc)
        log(f"call backend unavailable: {exc}")
    return _CALL["module"]


def call_server_url():
    """The QuestCPR server's current public https address (changes each time its start.sh runs)."""
    try:
        text = (CALL_BACKEND / "tunnel.log").read_text(errors="ignore")
        urls = re.findall(r"https://[a-z0-9-]+\.trycloudflare\.com", text)
        return urls[0] if urls else None
    except OSError:
        return None


_DISPATCHED = {}  # session id -> result (one call per SOS session)


def session_path(sid):
    if not SAFE_ID.match(sid or ""):
        raise ValueError("invalid session id")
    return SESSIONS / f"{sid}.json"


def summary_for(sid):
    events = load_events(session_path(sid))
    if not events:
        return None
    return compose_summary(reduce_events(events), created_at=utc_now())


class Handler(BaseHTTPRequestHandler):
    server_version = "CPRXRBridge/1.0"

    def log_message(self, *_):  # quiet default access log; we log our own lines
        pass

    def _authorized(self, url):
        """This Mac needs no key; anything else on the network must send the shared key."""
        tunnelled = bool(self.headers.get("Cf-Ray") or self.headers.get("Cf-Connecting-Ip"))
        if self.client_address[0] in ("127.0.0.1", "::1") and not tunnelled:
            return True
        key = self.headers.get("X-CPRXR-Key") or parse_qs(url.query).get("key", [""])[0]
        return secrets.compare_digest(key or "", DEVICE_KEY)

    def _send(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > 5_000_000:
            raise ValueError("body too large")
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split("/") if p]
        try:
            authed = self._authorized(url)
            if parts == ["v1", "health"] and not authed:
                return self._send(200, {"ok": True})  # no details for strangers
            if not authed:
                return self._send(401, {"error": "missing or wrong X-CPRXR-Key"})
            if parts == ["live"]:
                return self._live_page()
            if parts == ["v1", "feed.mjpg"]:
                return self._mjpeg()
            if parts == ["v1", "status"]:
                return self._send(200, self._status())
            if parts == ["v1", "health"]:
                mod = call_module()
                return self._send(200, {"ok": True, "speech_model_ready": STT.model is not None, "speech_model": STT.model_name,
                                        "speech_error": STT.error, "team_repo": str(TEAM_REPO), "team_commit": team_commit(),
                                        "call_server_url": call_server_url() or "", "call_ready": mod is not None,
                                        "call_mock": bool(mod and mod.Config.MOCK_TWILIO), "call_error": _CALL["error"] or ""})
            if len(parts) == 4 and parts[:2] == ["v1", "sessions"] and parts[3] == "summary":
                s = summary_for(parts[2])
                return self._send(200, s) if s else self._send(404, {"error": "no events in session"})
            self._send(404, {"error": "not found"})
        except ValueError as exc:
            self._send(400, {"error": str(exc)})
        except Exception as exc:
            log(f"GET {url.path} failed: {exc}")
            self._send(500, {"error": str(exc)})

    def do_POST(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split("/") if p]
        try:
            if not self._authorized(url):
                return self._send(401, {"error": "missing or wrong X-CPRXR-Key"})
            if parts == ["v1", "log"]:
                lines = self._body().decode("utf-8", "replace")
                with HEADSET_LOG.open("a", encoding="utf-8") as f:
                    f.write(lines if lines.endswith("\n") else lines + "\n")
                return self._send(200, {"ok": True})
            if parts == ["v1", "feed"]:
                jpg = self._body()
                if not FEED["t"] or time.time() - FEED["t"] > 10:
                    log(f"live feed: camera frames arriving from the headset ({len(jpg) // 1000} kB each)")
                with FEED_COND:
                    FEED.update(jpg=jpg, t=time.time(), frames=FEED["frames"] + 1)
                    FEED_COND.notify_all()
                return self._send(200, {"ok": True})
            if parts == ["v1", "sessions"]:
                return self._create_session(json.loads(self._body() or b"{}"))
            if len(parts) == 4 and parts[:2] == ["v1", "sessions"] and parts[3] == "events":
                return self._post_event(parts[2], json.loads(self._body() or b"{}"))
            if len(parts) == 4 and parts[:2] == ["v1", "sessions"] and parts[3] == "speech":
                source = parse_qs(url.query).get("source", ["operator_report"])[0]
                return self._post_speech(parts[2], source, self._body())
            if len(parts) == 4 and parts[:2] == ["v1", "sessions"] and parts[3] == "dispatch":
                return self._dispatch(parts[2], json.loads(self._body() or b"{}"))
            if len(parts) == 5 and parts[:2] == ["v1", "sessions"] and parts[3:] == ["calls", "test"]:
                log(f"[{parts[2]}] test call requested - SIMULATED, no call placed")
                return self._send(202, {"status": "simulated", "destination_id": "test_dispatcher", "note": "bridge never places calls"})
            self._send(404, {"error": "not found"})
        except (ValueError, json.JSONDecodeError) as exc:
            self._send(400, {"error": str(exc)})
        except Exception as exc:
            log(f"POST {url.path} failed: {exc}")
            self._send(500, {"error": str(exc)})

    # ---- handlers ----

    def _create_session(self, body):
        sid = body.get("client_session_id") or f"sim_{datetime.now():%Y%m%d_%H%M%S}"
        path = session_path(sid)
        SESSIONS.mkdir(exist_ok=True)
        if not path.exists():
            path.write_text("[]", encoding="utf-8")
        log(f"[{sid}] session created ({body.get('mode', 'emergency_simulation')})")
        self._send(201, {"session_id": sid, "simulation_only": True})

    def _post_event(self, sid, event):
        path = session_path(sid)
        if event.get("type") not in EVENT_TYPES or event.get("source") not in EVENT_SOURCES:
            raise ValueError(f"type/source not allowed: {event.get('type')}/{event.get('source')}")
        if event.get("type") == "speech_transcript":
            # The backend adapter's job (Unity never sends facts): extract with the team's transcriber.
            p = event.setdefault("payload", {})
            x = extract_transcript_facts(p.get("transcript", ""), source=event.get("source"))
            p["facts"], p["unresolved"] = x["facts"], x["unresolved"]
        # Validate with the team's reducer before storing: a rejected event never lands in the file.
        reduce_events(load_events(path) + [event])
        append_event(path, event, sid)
        log(f"[{sid}] event {event['type']}/{event['source']} accepted")
        self._send(202, {"accepted": True, "summary": summary_for(sid)})

    def _dispatch(self, sid, body):
        """Short summary + coordinates -> the team's SignalWire call to the demo phone (once per session)."""
        session_path(sid)  # validates the id
        if sid in _DISPATCHED and not body.get("again"):
            return self._send(200, dict(_DISPATCHED[sid], duplicate=True))
        mod = call_module()
        if mod is None:
            return self._send(503, {"ok": False, "error": f"call backend unavailable: {_CALL['error']}"})
        report, fields = short_report(sid)
        LAST["session"] = sid
        lat, lon = body.get("latitude"), body.get("longitude")
        if isinstance(lat, (int, float)) and isinstance(lon, (int, float)) and (lat or lon):
            loc = {"latitude": float(lat), "longitude": float(lon),
                   "location_accuracy_m": body.get("accuracy_m"), "location_source": "device"}
            log(f"[{sid}] location from the headset (accuracy {body.get('accuracy_m')} m)")
        else:
            loc = None
        loc = loc or get_location() or {"latitude": None, "longitude": None, "location_accuracy_m": None, "location_source": "simulated"}
        payload = {"event": "SOS", "patient_status": "unknown", "breathing": False, "age_category": "unknown", **loc}
        # Their wording for the coordinates; our summary of what the bystander said.
        announcement = speakable(f"Hello, this is an automated CPR system. {report} {mod.short_location(payload)}")
        if body.get("practice"):  # practice mode: same message, never dialed
            log(f"[{sid}] PRACTICE call (simulated, nothing dialed): \"{announcement}\"")
            out = {"ok": True, "announcement": announcement, "call_sid": None, "mock": True, "status": "practice",
                   "location_source": loc["location_source"], "facts_used": fields}
            LAST["dispatch"] = dict(out, at=utc_now())
            return self._send(200, out)
        log(f"[{sid}] DISPATCH -> demo phone: \"{announcement}\"")
        try:
            result = mod.place_demo_call(mod.build_twiml(announcement), announcement)
        except mod.CallError as exc:
            log(f"[{sid}] call failed: {exc}")
            return self._send(502, {"ok": False, "error": str(exc), "announcement": announcement})
        out = {"ok": True, "announcement": announcement, "call_sid": result.get("call_sid"), "mock": result.get("mock"),
               "status": result.get("status"), "location_source": loc["location_source"], "facts_used": fields}
        _DISPATCHED[sid] = out
        LAST["dispatch"] = dict(out, at=utc_now())
        log(f"[{sid}] call placed: status={out['status']} mock={out['mock']}")
        self._send(200, out)

    # ---- live feed + status for the laptop ----

    def _status(self):
        sid = LAST["session"]
        transcripts = []
        if sid and session_path(sid).exists():
            try:
                transcripts = [e["payload"]["transcript"] for e in load_events(session_path(sid)) if e.get("type") == "speech_transcript"]
            except Exception:
                pass
        return {"session": sid, "transcripts": transcripts[-12:], "dispatch": LAST["dispatch"],
                "feed_frames": FEED["frames"], "feed_age_s": round(time.time() - FEED["t"], 1) if FEED["t"] else None}

    def _mjpeg(self):
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        last = 0.0
        log(f"live feed: viewer connected ({FEED['frames']} frames so far)")
        try:
            while True:
                with FEED_COND:
                    FEED_COND.wait_for(lambda: FEED["t"] > last, timeout=10)
                    jpg, last = FEED["jpg"], FEED["t"]
                if not jpg:
                    continue
                self.wfile.write(b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + str(len(jpg)).encode() + b"\r\n\r\n" + jpg + b"\r\n")
        except (BrokenPipeError, ConnectionResetError):
            log("live feed: viewer disconnected")

    def _live_page(self):
        html = LIVE_PAGE.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(html)))
        self.end_headers()
        self.wfile.write(html)

    def _post_speech(self, sid, source, wav_bytes):
        if source not in SPEECH_SOURCES:
            raise ValueError(f"speech source must be one of {sorted(SPEECH_SOURCES)}")
        if len(wav_bytes) < 1000:
            raise ValueError("audio too short")
        path = session_path(sid)
        LAST["session"] = sid
        if not path.exists():
            SESSIONS.mkdir(exist_ok=True)
            path.write_text("[]", encoding="utf-8")
        t0 = time.time()
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=True) as f:
            f.write(wav_bytes)
            f.flush()
            text, confidence = STT.transcribe(f.name)
        stt_ms = int((time.time() - t0) * 1000)
        # Test hook (local file only): each line of bridge/inject.txt replaces the next heard
        # phrase, so the voice flow can be tested without anyone speaking the words.
        inject = HERE / "inject.txt"
        if inject.exists():
            lines = inject.read_text(encoding="utf-8").splitlines()
            if lines:
                log(f"[{sid}] speech: test inject replaces {text!r} with {lines[0]!r}")
                text, confidence = lines[0], 1.0
                rest = lines[1:]
                inject.write_text("\n".join(rest), encoding="utf-8") if rest else inject.unlink()
        if not text:
            log(f"[{sid}] speech: (nothing recognised) {len(wav_bytes) // 1000} kB in {stt_ms} ms")
            return self._send(200, {"transcript": "", "facts": [], "unresolved": [], "stt_ms": stt_ms})
        # The team's own path: transcript -> facts -> stored event (stt_provider = faster-whisper).
        events = append_speech(path, text, source, sid, stt_provider=LOCAL_STT_PROVIDER, confidence=confidence)
        payload = events[-1]["payload"]
        summary = summary_for(sid)
        log(f"[{sid}] speech ({stt_ms} ms): \"{text}\" -> facts {[(f['field'], f['value']) for f in payload['facts']]}")
        fact_strings = [f"{f['field']}={json.dumps(f['value'])}".replace('"', "") for f in payload["facts"]]  # Unity-friendly
        self._send(200, {"transcript": text, "confidence": confidence, "facts": payload["facts"], "fact_strings": fact_strings,
                         "unresolved": payload["unresolved"], "stt_ms": stt_ms,
                         "spoken_text": summary.get("spoken_text") if summary else None})


def find_adb():
    import glob
    import shutil
    return shutil.which("adb") or next(iter(sorted(glob.glob(
        "/Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"))), None)


def keep_usb_link(adb, interval=4.0):
    """Android drops `adb reverse` when the headset sleeps or USB reconnects: restore it automatically."""
    linked = None
    while True:
        try:
            out = subprocess.run([adb, "reverse", "--list"], capture_output=True, text=True, timeout=5).stdout
            ok = f"tcp:{PORT}" in out
            if not ok:
                state = subprocess.run([adb, "get-state"], capture_output=True, text=True, timeout=5).stdout.strip()
                if state == "device":
                    ok = subprocess.run([adb, "reverse", f"tcp:{PORT}", f"tcp:{PORT}"], capture_output=True, timeout=5).returncode == 0
            if ok != linked:
                log("USB link to headset: " + ("ready" if ok else "waiting for headset (plug in / wake it)"))
                linked = ok
        except Exception:
            pass
        time.sleep(interval)


LIVE_PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>CPR-XR live feed</title>
<style>body{margin:0;background:#0d1117;color:#e6edf3;font:15px -apple-system,system-ui,sans-serif;display:flex;gap:16px;padding:16px}
img{width:66%;border-radius:10px;background:#000;min-height:300px}aside{flex:1}h1{font-size:18px;margin:0 0 8px}
.box{background:#161b22;border:1px solid #30363d;border-radius:10px;padding:12px;margin-bottom:12px}.muted{color:#8b949e}</style></head>
<body><img id="v" src="/v1/feed.mjpg" alt="waiting for the headset camera..." onerror="setTimeout(()=>this.src='/v1/feed.mjpg?'+Date.now(),2000)"><aside>
<h1>CPR-XR live (demo)</h1><div class="box" id="call">No call yet</div><div class="box"><b>What the rescuer said</b><div id="t" class="muted">-</div></div>
<div class="muted" id="feed"></div></aside>
<script>async function poll(){try{const s=await (await fetch('/v1/status')).json();
document.getElementById('t').innerHTML=(s.transcripts||[]).map(x=>'&ldquo;'+x.replace(/</g,'&lt;')+'&rdquo;').join('<br>')||'-';
document.getElementById('call').innerHTML=s.dispatch?('<b>Call placed '+s.dispatch.at+'</b><br>'+s.dispatch.announcement.replace(/</g,'&lt;')):'No call yet';
if(window.down){window.down=false;document.getElementById('v').src='/v1/feed.mjpg?'+Date.now()} // bridge came back: reopen the video
document.getElementById('feed').textContent=s.feed_frames?('camera frames: '+s.feed_frames+', last '+s.feed_age_s+' s ago'):'waiting for camera...';}catch(e){window.down=true}}
setInterval(poll,1000);poll();</script></body></html>"""


def beacon():
    """Announce the bridge on the local network so the headset can find it (no IP typing, no cable)."""
    msg = json.dumps({"service": "cprxr-bridge", "port": PORT}).encode()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    while True:
        try:
            sock.sendto(msg, ("255.255.255.255", BEACON_PORT))
        except OSError:
            pass
        time.sleep(2)


def scramble(text):
    """XOR with a SHA-256 keystream from the device key, base64 - ntfy.sh never sees a readable address."""
    data = text.encode()
    stream = b""
    i = 0
    while len(stream) < len(data):
        stream += hashlib.sha256(f"relay:{DEVICE_KEY}:{i}".encode()).digest()
        i += 1
    return base64.b64encode(bytes(a ^ b for a, b in zip(data, stream))).decode()


def find_cloudflared():
    import shutil
    team_copy = CALL_BACKEND / "tools" / "cloudflared"
    return shutil.which("cloudflared") or (str(team_copy) if team_copy.exists() else None)


def run_tunnel_and_relay():
    """Keep a Cloudflare quick tunnel to this bridge open and its address published (scrambled) on ntfy.sh."""
    exe = find_cloudflared()
    if not exe:
        log("relay: cloudflared not found (run ../QuestCPR-Full/start.sh once, or brew install cloudflared)")
        return
    while True:
        proc = subprocess.Popen([exe, "tunnel", "--no-autoupdate", "--url", f"http://localhost:{PORT}"],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        url, last_publish = None, 0.0
        for line in proc.stdout:
            m = re.search(r"https://[a-z0-9-]+\.trycloudflare\.com", line)
            if m and not url:
                url = m.group(0)
                TUNNEL["url"] = url
                log(f"relay: tunnel open at {url} (key required)")
            if url and "Registered tunnel connection" in line and time.time() - last_publish > 30:
                try:
                    import requests  # bundles its own CA certificates (python.org Python on macOS has none)
                    body = json.dumps({"v": 1, "u": scramble(url), "t": int(time.time())})
                    requests.post(f"https://ntfy.sh/{RELAY_TOPIC}", data=body.encode(), timeout=10).raise_for_status()
                    last_publish = time.time()
                    log("relay: address published (scrambled) for the headset")
                except Exception as exc:
                    log(f"relay: publish failed: {exc}")
        TUNNEL["url"] = None
        log("relay: tunnel closed - reopening in 5 s")
        time.sleep(5)


def lan_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return "?"


def main():
    SESSIONS.mkdir(exist_ok=True)
    # Look up the Mac's location once at startup (any macOS prompt shows now, not during an emergency call).
    threading.Thread(target=lambda: log(f"Mac location: {'found' if (get_location() or {}).get('location_source') == 'device' else 'not available'}"),
                     daemon=True).start()
    if HOST == "0.0.0.0":
        threading.Thread(target=beacon, daemon=True).start()
        log(f"Wi-Fi mode: reachable at http://{lan_ip()}:{PORT}/v1 (key required), announced on UDP {BEACON_PORT}")
    log(f"Live feed for this laptop: http://localhost:{PORT}/live")
    if os.environ.get("CPRXR_RELAY", "1") != "0":
        threading.Thread(target=run_tunnel_and_relay, daemon=True).start()
    adb = find_adb()
    if adb:
        threading.Thread(target=keep_usb_link, args=(adb,), daemon=True).start()
    log(f"CPR-XR bridge on http://{HOST}:{PORT}/v1  (team repo {TEAM_REPO.name} @ {team_commit()})")
    call_module()
    log(f"QuestCPR call server: {call_server_url() or 'not running (run ../QuestCPR-Full/start.sh)'}")
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
