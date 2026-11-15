#!/bin/bash
# Start the CPR-XR laptop bridge and connect the Quest to it over USB.
#   ./bridge/start_bridge.sh
# Needs: the Quest plugged in (USB debugging allowed), ../immerse_the_bay_2026 cloned next to CPR-XR.
set -e
cd "$(dirname "$0")"

# adb: from PATH, or the one bundled with Unity's Android support.
ADB="$(command -v adb || true)"
[ -z "$ADB" ] && ADB="$(ls /Applications/Unity/Hub/Editor/*/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb 2>/dev/null | tail -1)"

if [ -n "$ADB" ] && "$ADB" get-state >/dev/null 2>&1; then
  # Quest's localhost:8077 -> this Mac's localhost:8077 (over the USB cable; nothing exposed on Wi-Fi).
  "$ADB" reverse tcp:8077 tcp:8077 && echo "USB link ready: Quest localhost:8077 -> this Mac"
else
  echo "Quest not connected over USB - the bridge still runs; plug in and re-run to link the headset."
fi

# Python packages live outside ~/Documents: iCloud ("Optimize Mac Storage") evicts files there, even in
# .nosync folders, which makes every import/HTTPS call crawl (calls then fail with SSLError).
VENV="${CPRXR_VENV:-$HOME/.cprxr/venv}"
[ -x "$VENV/bin/python" ] || { mkdir -p "$(dirname "$VENV")" && python3 -m venv "$VENV" && "$VENV/bin/pip" install -q "faster-whisper>=1.1.0" flask twilio python-dotenv requests; }
# macOS location helper (needs Location Services allowed for it; otherwise config.json fallback is used)
[ -x .bin/cprxr-location ] || { mkdir -p .bin && swiftc -O location.swift -o .bin/cprxr-location 2>/dev/null || true; }
exec "$VENV/bin/python" xr_aed_bridge.py
