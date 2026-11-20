<#
  After a fresh card or on a new Pi: copies a snapshot from backup.ps1 back to the Pi, and prints the one line to run
  there. Publish and run setup.sh first, so HQ and the signal box are installed for the snapshot to go back into.

    .\deploy\restore.ps1 -PiHost pi@np.local                         # the newest snapshot in the backup folder
    .\deploy\restore.ps1 -PiHost pi@np.local -Snapshot D:\northpole-np-20261215-210000.tar.gz

  The backup folder is -From, or else the NORTHPOLE_BACKUPS environment variable, or else Documents\NorthPole backups,
  as for backup.ps1.
#>
param(
    [string]$PiHost = "$env:USERNAME@np.local",
    [string]$Snapshot,
    [string]$From = $(if ($env:NORTHPOLE_BACKUPS) { $env:NORTHPOLE_BACKUPS }
                      else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NorthPole backups' })
)
$ErrorActionPreference = 'Stop'

if (-not $Snapshot) {
    # By the time in the name (northpole-np-20261215-031500.tar.gz), which a copy can't change.
    $newest = Get-ChildItem $From -Filter 'northpole-*.tar.gz' -ErrorAction SilentlyContinue |
        Sort-Object { if ($_.Name -match '-(\d{8}-\d{6})\.tar\.gz$') { $Matches[1] } else { '' } }, LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $newest) { throw "No snapshots in ${From}; name one with -Snapshot" }
    $Snapshot = $newest.FullName
}
$name = Split-Path $Snapshot -Leaf
Write-Host "Copying $name to $PiHost..."
scp -q $Snapshot "${PiHost}:$name"
if ($LASTEXITCODE -ne 0) { throw "Couldn't copy $Snapshot to the Pi" }

Write-Host ""
Write-Host "Now on the Pi (it asks for your password):"
Write-Host "  sudo bash ~/northpole-setup/restore.sh ~/$name"
Write-Host "Then put the Claude key and the PIN back in /etc/northpole/hq.env if this is a fresh card."
