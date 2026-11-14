#!/bin/bash
# Santa's voice on a Bluetooth speaker such as an Amazon Echo. HQ runs it as Voice:PlayCommand and adds the WAV:
#   "PlayCommand": "santa-play AA:BB:CC:DD:EE:FF"
# Wakes the link first if the speaker dropped it while idle, then plays the file with half a second of silence in
# front, so a speaker that is still waking up doesn't swallow Santa's first word. echo-speaker.sh installs it.
set -uo pipefail
MAC=${1:?usage: santa-play <speaker address> <file.wav>}
FILE=${2:?usage: santa-play <speaker address> <file.wav>}
DEVICE="bluealsa:DEV=$MAC,PROFILE=a2dp"

if ! bluetoothctl info "$MAC" 2>/dev/null | grep -q "Connected: yes"; then
  if ! bluetoothctl connect "$MAC" >/dev/null 2>&1; then
    echo "santa-play: could not connect to the speaker $MAC (is it on and in range?)" >&2
    exit 1
  fi
  sleep 2 # give the audio link a moment to settle after connecting
fi

if command -v sox >/dev/null; then
  sox -q "$FILE" -t wav - pad 0.5 | aplay -q -D "$DEVICE"
else
  aplay -q -D "$DEVICE" "$FILE"
fi
