#!/bin/bash
# Raspberry Pi 4 = the signal box next to the track (LEGO hub over the Pi's own Bluetooth, IR sensors, servo, LEDs).
# Run on the Pi from this folder:  bash setup.sh
# Assumes 64-bit Raspberry Pi OS and that you've copied the published app to ~/northpole-signalbox
set -euo pipefail

echo "== .NET 10 (ASP.NET Core runtime, which includes the generic host)"
curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir /opt/dotnet
sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet

echo "== Hardware PWM on GPIO18 for the mailbox-flag servo"
CONFIG=/boot/firmware/config.txt
if ! grep -q "dtoverlay=pwm,pin=18,func=2" "$CONFIG"; then
  echo "dtoverlay=pwm,pin=18,func=2" | sudo tee -a "$CONFIG" >/dev/null
  echo "   (added to $CONFIG: reboot needed)"
fi

echo "== Bluetooth (BlueZ) for the LEGO hub, on the Pi's built-in radio"
sudo apt-get update -qq && sudo apt-get install -y -qq bluez
if command -v rfkill >/dev/null; then sudo rfkill unblock bluetooth; fi
sudo systemctl enable --now bluetooth
if grep -q "^dtoverlay=disable-bt" "$CONFIG"; then
  echo "!! $CONFIG turns Bluetooth off (dtoverlay=disable-bt): remove that line and reboot"
fi

echo "== App folder and service (settings files you've already edited are kept)"
sudo mkdir -p /opt/northpole/signalbox
for f in ~/northpole-signalbox/*; do
  name=$(basename "$f")
  if [[ $name == appsettings*.json && -f /opt/northpole/signalbox/$name ]]; then echo "   keeping your $name"; continue; fi
  sudo cp -r "$f" /opt/northpole/signalbox/
done
sudo cp northpole-signalbox.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable northpole-signalbox

echo
echo "Done. Reboot, check Bluetooth is on (bluetoothctl show: Powered: yes), then press the green button on the locomotive."
echo "  Logs: journalctl -u northpole-signalbox -f"
