#!/bin/bash
# Puts a snapshot from backup.sh back, after a fresh card or on a new Pi. Run setup.sh first, copy the snapshot over
# (deploy/restore.ps1 does both the copy and the reminder), then on the Pi:
#   sudo bash restore.sh ~/northpole-np-20261215-031500.tar.gz
# It stops HQ and the signal box, sets aside what's there now, unpacks the snapshot over it, and starts them again.
# The Claude key and the PIN aren't in snapshots: put them back in /etc/northpole/hq.env yourself.
set -euo pipefail

snap=${1:?Usage: sudo bash restore.sh <snapshot.tar.gz>}
if [ "$(id -u)" -ne 0 ]; then echo "Run it with sudo: sudo bash restore.sh $snap" >&2; exit 1; fi
owner=${SUDO_USER:?Run it with sudo from your own account, so the files go back to you}
tar -tzf "$snap" >/dev/null # a damaged snapshot stops here, before anything changes

echo "== Stopping HQ and the signal box"
systemctl stop northpole-hq northpole-signalbox 2>/dev/null || true

aside=/var/backups/northpole-before-restore-$(date +%Y%m%d-%H%M%S).tar.gz
mkdir -p /var/backups
tar -czf "$aside" -C / --ignore-failed-read \
  opt/northpole/hq/data opt/northpole/hq/appsettings.json \
  opt/northpole/signalbox/appsettings.json opt/northpole/signalbox/appsettings.Pi.json 2>/dev/null || true
echo "   what was here is set aside in $aside"

echo "== Unpacking $snap"
tar -xzf "$snap" -C /
if [ -d /opt/northpole/hq/data ]; then chown -R "$owner:" /opt/northpole/hq/data; fi
if [ -f /opt/northpole/hq/appsettings.json ]; then chown "$owner" /opt/northpole/hq/appsettings.json; fi

echo "== Starting them again"
for service in northpole-hq northpole-signalbox; do
  if systemctl is-enabled --quiet "$service" 2>/dev/null; then systemctl start "$service"; fi
done
echo "Done. Check the Claude key and the PIN in /etc/northpole/hq.env (sudo nano /etc/northpole/hq.env), then restart"
echo "HQ if you changed them: sudo systemctl restart northpole-hq"
