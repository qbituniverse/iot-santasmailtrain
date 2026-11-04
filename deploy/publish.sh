#!/bin/bash
# Run on your dev machine (Windows: use Git Bash or WSL). Builds both apps for 64-bit Raspberry Pi OS
# and copies them over SSH. Change the hostnames if yours differ.
set -euo pipefail
cd "$(dirname "$0")/.."

HQ_HOST=${HQ_HOST:-pi@np.local}            # the Pi 5
SIGNAL_HOST=${SIGNAL_HOST:-pi@signalbox.local}  # the Pi 4

dotnet publish src/NorthPole.Hq -c Release -r linux-arm64 --self-contained false -o out/hq
dotnet publish src/NorthPole.SignalBox -c Release -r linux-arm64 --self-contained false -o out/signalbox

rsync -av --delete out/hq/ "$HQ_HOST:~/northpole-hq/"
rsync -av deploy/pi5-hq/ "$HQ_HOST:~/northpole-setup/"
rsync -av --delete out/signalbox/ "$SIGNAL_HOST:~/northpole-signalbox/"
rsync -av deploy/pi4-signalbox/ "$SIGNAL_HOST:~/northpole-setup/"

echo "First time: ssh to each Pi and run 'cd ~/northpole-setup && bash setup.sh'."
echo "Updates:    sudo rsync -a --exclude 'appsettings*.json' ~/northpole-hq/ /opt/northpole/hq/ && sudo systemctl restart northpole-hq"
echo "            sudo rsync -a --exclude 'appsettings*.json' ~/northpole-signalbox/ /opt/northpole/signalbox/ && sudo systemctl restart northpole-signalbox"
echo "            (--exclude keeps your edited settings: the family, the speaker, the sensors and the Pi 4's broker address)"
