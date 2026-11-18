#!/bin/bash
# Snapshots everything that lives only on this Pi, so a fresh card (or a new Pi) gets it all back with restore.sh:
#   - HQ's data/: the QR card codes (printed and LEGO cards keep working), every letter with its gift ideas and what's
#     been bought, the drawings, Santa's voice files, and what the dashboard saved (Santa's volume, the train's standby);
#   - HQ's appsettings.json: the family, Santa's family lore, his speakers' sound card;
#   - the signal box's appsettings files: speeds, sensors and timings, if you've tuned them.
# Not /etc/northpole/hq.env: the Claude key and the PIN are secrets, so keep them somewhere safe of your own.
#
#   bash backup.sh [folder]     a snapshot in folder (default ~/northpole-backups), keeping the newest 14;
#                               prints the snapshot's path, which deploy/backup.ps1 copies to the PC
# setup.sh installs it as northpole-backup, with a timer that runs it every night.
set -euo pipefail

DEST=${1:-${NORTHPOLE_BACKUPS:-$HOME/northpole-backups}}
KEEP=${NORTHPOLE_KEEP:-14}
HQ=/opt/northpole/hq
SIGNALBOX=/opt/northpole/signalbox

paths=()
for p in "$HQ/data" "$HQ/appsettings.json" "$SIGNALBOX/appsettings.json" "$SIGNALBOX/appsettings.Pi.json"; do
  if [ -e "$p" ]; then paths+=("${p#/}"); fi
done
if [ ${#paths[@]} -eq 0 ]; then echo "Nothing to back up: HQ isn't installed in $HQ" >&2; exit 1; fi

mkdir -p "$DEST"
chmod 700 "$DEST"
file="$DEST/northpole-$(hostname)-$(date +%Y%m%d-%H%M%S).tar.gz"
# Exit code 1 means a file changed while it was read (HQ saving a letter); that file is still whole in the next one.
tar -czf "$file.part" -C / --warning=no-file-changed "${paths[@]}" || [ $? -eq 1 ]
mv "$file.part" "$file"
chmod 600 "$file"
ln -sfn "$(basename "$file")" "$DEST/latest.tar.gz"
ls -1t "$DEST"/northpole-*.tar.gz | tail -n +$((KEEP + 1)) | xargs -r rm --
echo "$file"
