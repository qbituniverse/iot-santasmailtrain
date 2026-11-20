#!/bin/bash
# Sets up a Raspberry Pi 5 to run all of Santa's Mail Train: HQ (the web pages, Claude, Santa's voice and the MQTT
# broker) and the signal box (the LEGO train over Bluetooth, the sensors, flag, signal and station board on the GPIO).
# Run it on the Pi as your normal user, from this folder:
#   bash setup.sh              everything on this Pi (the usual way)
#   bash setup.sh hq           only HQ, if the signal box runs on a second Pi
#   bash setup.sh signalbox    only the signal box, on that second Pi (a Pi 4 works too)
#   bash setup.sh backup       only the nightly snapshots (part of everything and of hq)
# Assumes 64-bit Raspberry Pi OS (Bookworm or later), with the apps already copied to ~/northpole-hq and
# ~/northpole-signalbox by publish.sh. Running it again is safe: it keeps the settings you've edited.
set -euo pipefail

ROLE=${1:-all}
case "$ROLE" in all | hq | signalbox | backup) ;; *) echo "Usage: bash setup.sh [all|hq|signalbox|backup]"; exit 1 ;; esac
want() { [[ $ROLE == all || $ROLE == "$1" ]] || [[ $1 == backup && $ROLE == hq ]]; }
CONFIG=/boot/firmware/config.txt

# Copies a published app in, but keeps any settings file already there, so a second run loses no edits.
install_app() {
  sudo mkdir -p "$2"
  for f in "$1"/*; do
    name=$(basename "$f")
    if [[ $name == appsettings*.json && -f $2/$name ]]; then echo "   keeping your $name"; continue; fi
    sudo cp -r "$f" "$2/"
  done
}

if want hq || want signalbox; then
  echo "== .NET 10 (ASP.NET Core runtime, which also hosts the signal box)"
  curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir /opt/dotnet
  sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
  sudo apt-get update -qq
fi

if want hq; then
  echo "== Hostname 'np' so phones can reach http://np.local (keeps the QR code small enough for LEGO)"
  sudo raspi-config nonint do_hostname np

  echo "== Piper text-to-speech with a warm, slow British male voice for Santa"
  sudo apt-get install -y -qq python3-venv alsa-utils
  sudo python3 -m venv /opt/piper
  sudo /opt/piper/bin/pip install -q "piper-tts[http]"
  sudo mkdir -p /opt/piper/voices
  sudo /opt/piper/bin/python -m piper.download_voices en_GB-northern_english_male-medium --data-dir /opt/piper/voices

  echo "== HQ app (settings files you've already edited are kept)"
  sudo mkdir -p /etc/northpole
  install_app ~/northpole-hq /opt/northpole/hq
  sudo chown -R "$USER" /opt/northpole/hq

  # Secrets live here, readable by root only; systemd hands them to HQ as environment variables.
  if [ ! -f /etc/northpole/hq.env ]; then
    printf '%s\n' "ANTHROPIC_API_KEY=put-your-key-here" "# NorthPole__Elves__Pin=pick-a-pin" | sudo tee /etc/northpole/hq.env >/dev/null
    sudo chmod 600 /etc/northpole/hq.env
    echo "!! Edit /etc/northpole/hq.env: paste your Claude API key, and set the dashboard PIN there"
  fi

  # The dashboard's danger zone: HQ, running as you, may reboot or shut down the Pi, and run nothing else as root.
  # visudo checks the rule before it goes in, because a broken file in sudoers.d stops sudo working at all.
  rule=$(mktemp)
  echo "$USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot, /usr/bin/systemctl poweroff" >"$rule"
  if sudo visudo -cqf "$rule"; then sudo install -m 440 "$rule" /etc/sudoers.d/northpole-power
  else echo "!! The reboot rule didn't pass visudo, so the dashboard's Reboot and Shut down won't work"; fi
  rm -f "$rule"

  sed "s/__USER__/$USER/" piper.service | sudo tee /etc/systemd/system/piper.service >/dev/null
  sed "s/__USER__/$USER/" northpole-hq.service | sudo tee /etc/systemd/system/northpole-hq.service >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable --now piper northpole-hq
fi

if want signalbox; then
  echo "== GPIO through libgpiod (a Pi 5 reaches its pins through it), and BlueZ for the LEGO hub"
  sudo apt-get install -y -qq bluez
  sudo apt-get install -y -qq libgpiod2 2>/dev/null || sudo apt-get install -y -qq libgpiod3
  if command -v rfkill >/dev/null; then sudo rfkill unblock bluetooth; fi
  sudo systemctl enable --now bluetooth
  if grep -q "^dtoverlay=disable-bt" "$CONFIG"; then
    echo "!! $CONFIG turns Bluetooth off (dtoverlay=disable-bt): remove that line and reboot"
  fi

  echo "== Hardware PWM on GPIO18 for the mailbox-flag servo (the same line on a Pi 4 and a Pi 5)"
  if ! grep -q "^dtoverlay=pwm,pin=18,func=2" "$CONFIG"; then
    echo "dtoverlay=pwm,pin=18,func=2" | sudo tee -a "$CONFIG" >/dev/null
    echo "   (added to $CONFIG: reboot needed)"
  fi

  echo "== Signal box app (settings files you've already edited are kept)"
  install_app ~/northpole-signalbox /opt/northpole/signalbox
  if [[ $ROLE == signalbox ]] && grep -q '"Host": "localhost"' /opt/northpole/signalbox/appsettings.Pi.json; then
    # On its own Pi, the signal box reaches HQ's broker over the network.
    sudo sed -i 's/"Host": "localhost"/"Host": "np.local"/' /opt/northpole/signalbox/appsettings.Pi.json
    echo "   pointed it at HQ on np.local (change Mqtt:Host in appsettings.Pi.json if yours differs)"
  fi
  sudo cp northpole-signalbox.service /etc/systemd/system/
  sudo systemctl daemon-reload
  sudo systemctl enable northpole-signalbox
fi

if want backup; then
  echo "== Nightly snapshots of the Pi's own data (card codes, letters, the family's settings) in ~/northpole-backups"
  sudo install -m 755 backup.sh /usr/local/bin/northpole-backup
  sed -e "s|__USER__|$USER|" -e "s|__HOME__|$HOME|" northpole-backup.service |
    sudo tee /etc/systemd/system/northpole-backup.service >/dev/null
  sudo cp northpole-backup.timer /etc/systemd/system/
  sudo systemctl daemon-reload
  sudo systemctl enable --now northpole-backup.timer
fi

echo
echo "Done. Next:"
if want hq; then
  echo "  - Edit /opt/northpole/hq/appsettings.json (children, family lore) and sudo nano /etc/northpole/hq.env (key, PIN)."
  echo "  - Plug Santa's speakers' USB sound card into a black USB port (the Pi 5 has no headphone jack), and set"
  echo "    Voice:SoundCard to its id, the name in brackets in: cat /proc/asound/cards"
fi
if want signalbox; then
  echo "  - Reboot (for the servo's PWM), check Bluetooth (bluetoothctl show: Powered: yes),"
  echo "    then press the green button on the locomotive."
fi
if want hq; then echo "  - Open http://np.local/elves on your phone, then 'QR cards & LEGO plans'."; fi
if want backup; then
  echo "  - Copy the snapshots off the Pi: on the PC, .\deploy\backup.ps1 (add -Schedule to run it daily)."
  echo "    After a fresh card: setup.sh, then .\deploy\restore.ps1 and the sudo line it prints."
fi
echo "  Logs: journalctl -u northpole-hq -u northpole-signalbox -f"
