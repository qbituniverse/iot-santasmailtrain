#!/bin/bash
# Run on your dev machine (Windows: use WSL). Builds both apps for 64-bit Raspberry Pi OS and copies them, with the
# setup files, to the Pi over SSH. Both apps go to one Pi 5 unless SIGNAL_HOST names a second Pi for the signal box.
set -euo pipefail
cd "$(dirname "$0")/.."

PI_HOST=${PI_HOST:-${HQ_HOST:-pi@np.local}}   # the Pi 5 that runs everything
SIGNAL_HOST=${SIGNAL_HOST:-$PI_HOST}           # only if the signal box runs on a second Pi

dotnet publish src/NorthPole.Hq -c Release -r linux-arm64 --self-contained false -o out/hq
dotnet publish src/NorthPole.SignalBox -c Release -r linux-arm64 --self-contained false -o out/signalbox

rsync -av --delete out/hq/ "$PI_HOST:~/northpole-hq/"
rsync -av deploy/pi/ "$PI_HOST:~/northpole-setup/"
rsync -av --delete out/signalbox/ "$SIGNAL_HOST:~/northpole-signalbox/"
if [[ $SIGNAL_HOST != "$PI_HOST" ]]; then rsync -av deploy/pi/ "$SIGNAL_HOST:~/northpole-setup/"; fi

echo "First time: ssh to the Pi and run 'cd ~/northpole-setup && bash setup.sh'."
if [[ $SIGNAL_HOST != "$PI_HOST" ]]; then
  echo "            Two Pis: 'bash setup.sh hq' on $PI_HOST, 'bash setup.sh signalbox' on $SIGNAL_HOST."
fi
echo "Updates:    sudo rsync -a --exclude 'appsettings*.json' ~/northpole-hq/ /opt/northpole/hq/ && sudo systemctl restart northpole-hq"
echo "            sudo rsync -a --exclude 'appsettings*.json' ~/northpole-signalbox/ /opt/northpole/signalbox/ && sudo systemctl restart northpole-signalbox"
echo "            (--exclude keeps your edited settings: the family, the speaker and the sensors)"
