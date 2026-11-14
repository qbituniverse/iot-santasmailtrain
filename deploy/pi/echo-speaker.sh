#!/bin/bash
# Optional: Santa speaks through an Amazon Echo (or any Bluetooth speaker) instead of a USB speaker.
# Run on the Pi as the normal user, from this folder, after setup.sh:
#   1. Say "Alexa, pair Bluetooth", then:   bash echo-speaker.sh                     (lists the speakers it can see)
#   2. Pair it and switch Santa over:       bash echo-speaker.sh AA:BB:CC:DD:EE:FF
# Assumes Raspberry Pi OS Lite. The desktop edition's PipeWire already handles Bluetooth audio and would clash.
set -euo pipefail
cd "$(dirname "$0")"
SETTINGS=/opt/northpole/hq/appsettings.json

if dpkg -s libspa-0.2-bluetooth >/dev/null 2>&1; then
  echo "!! PipeWire handles Bluetooth audio on this Pi (desktop edition), and BlueALSA would clash with it."
  echo "   Pair the speaker from the desktop's Bluetooth menu and set Voice:PlayCommand to \"pw-play\" instead."
  exit 1
fi

echo "== Bluetooth audio (BlueALSA), and sox for the half second of silence in front of Santa's voice"
sudo apt-get update -qq && sudo apt-get install -y -qq bluez bluez-alsa-utils sox
if command -v rfkill >/dev/null; then sudo rfkill unblock bluetooth; fi
sudo systemctl enable --now bluetooth bluealsa
sudo usermod -aG bluetooth "$USER" # lets HQ, which runs as you, wake the speaker's link
sudo install -m 755 santa-play.sh /usr/local/bin/santa-play

# Pairing runs with sudo: the bluetooth group added above only applies from your next login.
MAC=${1:-}
if [ -z "$MAC" ]; then
  echo "== Looking for Bluetooth speakers for 20 s: say \"Alexa, pair Bluetooth\" now if you haven't"
  sudo bluetoothctl --timeout 20 scan on >/dev/null
  sudo bluetoothctl devices
  echo
  echo "Find your Echo above (its name starts with \"Echo\"), then run:  bash echo-speaker.sh <its address>"
  exit 0
fi

echo "== Pairing with $MAC (keep the Echo in pairing mode)"
sudo bluetoothctl --timeout 15 scan on >/dev/null
if ! sudo bluetoothctl info "$MAC" >/dev/null 2>&1; then
  echo "!! No speaker at $MAC. Say \"Alexa, pair Bluetooth\" and run this again."
  exit 1
fi
sudo bluetoothctl pair "$MAC" || echo "   (pairing reported an error: fine if it was already paired)"
sudo bluetoothctl trust "$MAC"
sudo bluetoothctl connect "$MAC" || echo "!! Couldn't connect yet. santa-play will try again before every reply."

echo "== Pointing Santa's voice at it"
if python3 - "$SETTINGS" "$MAC" <<'EOF'
import json, sys
path, mac = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as f:
    settings = json.load(f)
settings.setdefault("NorthPole", {}).setdefault("Voice", {})["PlayCommand"] = f"santa-play {mac}"
with open(path, "w", encoding="utf-8") as f:
    json.dump(settings, f, indent=2, ensure_ascii=False)
    f.write("\n")
EOF
then
  sudo systemctl restart northpole-hq
else
  echo "!! Couldn't edit $SETTINGS. Set NorthPole:Voice:PlayCommand to \"santa-play $MAC\" by hand, then:"
  echo "   sudo systemctl restart northpole-hq"
fi

echo
echo "Done. Hear it now:  sg bluetooth -c \"santa-play $MAC /usr/share/sounds/alsa/Front_Center.wav\""
echo "Back to a USB speaker: set Voice:PlayCommand to \"aplay -q\" in $SETTINGS and restart northpole-hq."
