#!/bin/bash
# Raspberry Pi 5 = North Pole HQ (web app, Claude, Santa's voice, MQTT broker).
# Run on the Pi as the normal user, from this folder:  bash setup.sh
# Assumes 64-bit Raspberry Pi OS (Bookworm or later) and that you've copied the published app to ~/northpole-hq
set -euo pipefail

echo "== Hostname 'np' so phones can reach http://np.local (keeps the QR code small enough for LEGO)"
sudo raspi-config nonint do_hostname np

echo "== .NET 10 (ASP.NET Core runtime)"
curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir /opt/dotnet
sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet

echo "== Piper text-to-speech with a warm, slow British male voice for Santa"
sudo apt-get update -qq && sudo apt-get install -y -qq python3-venv alsa-utils
sudo python3 -m venv /opt/piper
sudo /opt/piper/bin/pip install -q "piper-tts[http]"
sudo mkdir -p /opt/piper/voices
sudo /opt/piper/bin/python -m piper.download_voices en_GB-northern_english_male-medium --data-dir /opt/piper/voices

echo "== App folder (settings files you've already edited are kept)"
sudo mkdir -p /opt/northpole/hq /etc/northpole
for f in ~/northpole-hq/*; do
  name=$(basename "$f")
  if [[ $name == appsettings*.json && -f /opt/northpole/hq/$name ]]; then echo "   keeping your $name"; continue; fi
  sudo cp -r "$f" /opt/northpole/hq/
done
sudo chown -R "$USER" /opt/northpole

# Secrets live here, readable by root only; systemd hands them to HQ as environment variables.
if [ ! -f /etc/northpole/hq.env ]; then
  printf '%s\n' "ANTHROPIC_API_KEY=put-your-key-here" "# NorthPole__Elves__Pin=pick-a-pin" | sudo tee /etc/northpole/hq.env >/dev/null
  sudo chmod 600 /etc/northpole/hq.env
  echo "!! Edit /etc/northpole/hq.env: paste your Claude API key, and set the dashboard PIN there"
fi

echo "== Services"
sed "s/__USER__/$USER/" piper.service | sudo tee /etc/systemd/system/piper.service >/dev/null
sed "s/__USER__/$USER/" northpole-hq.service | sudo tee /etc/systemd/system/northpole-hq.service >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable --now piper northpole-hq

echo
echo "Done. Next:"
echo "  1. Edit /opt/northpole/hq/appsettings.json (children, family lore) and sudo nano /etc/northpole/hq.env (key, PIN),"
echo "     then: sudo systemctl restart northpole-hq"
echo "  2. Plug in a USB speaker (the Pi 5 has no headphone jack). Test with: speaker-test -t wav -c 2 -l 1"
echo "     Or use an Amazon Echo over Bluetooth: bash echo-speaker.sh"
echo "  3. Open http://np.local/elves on your phone, then 'QR cards & LEGO plans'"
echo "  Logs: journalctl -u northpole-hq -f"
