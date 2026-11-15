<#
  Copies a fresh snapshot of the Pi's own data to this PC: the QR card codes, every letter with its gift ideas, the
  drawings, Santa's voice files, and the settings that hold the family and the speaker (deploy/pi/backup.sh makes it).
  The Pi keeps its own nightly snapshots too, but those go with its card; these don't.

    .\deploy\backup.ps1 -PiHost pi@np.local              # a snapshot now, into Documents\NorthPole backups
    .\deploy\backup.ps1 -PiHost pi@np.local -Schedule    # adds a daily Windows task that runs this script

  Run it by hand before reflashing the Pi. The newest -Keep snapshots stay; a cloud-synced folder for -To gives a copy
  away from the house as well. Snapshots hold the family's letters but never the Claude key or the PIN.
#>
param(
    [string]$PiHost = "$env:USERNAME@np.local",
    [string]$To = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NorthPole backups'),
    [int]$Keep = 60,
    [switch]$Schedule
)
$ErrorActionPreference = 'Stop'

if ($Schedule) {
    # Daily at 21:00, when the Pi is on and the day's letters are in; a PC that was off catches up when it starts.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -PiHost $PiHost -To `"$To`" -Keep $Keep"
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -Daily -At 21:00
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable
    Register-ScheduledTask -TaskName 'NorthPole backup' -Action $action -Trigger $trigger -Settings $settings `
        -Description "Copies a snapshot of Santa's Mail Train from $PiHost to $To" -Force | Out-Null
    Write-Host "Added 'NorthPole backup' to Task Scheduler: daily at 21:00, or as soon as the PC is on after that."
    return
}

New-Item -ItemType Directory -Force -Path $To | Out-Null
Write-Host "Making a snapshot on $PiHost..."
$remote = ssh $PiHost 'bash ~/northpole-setup/backup.sh' | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or -not $remote) { throw "The Pi couldn't make a snapshot" }

$local = Join-Path $To (Split-Path $remote -Leaf)
scp -q "${PiHost}:$remote" $local
if ($LASTEXITCODE -ne 0) { throw "Couldn't copy $remote from the Pi" }

Get-ChildItem $To -Filter 'northpole-*.tar.gz' | Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $Keep | Remove-Item
Write-Host "Saved $local ($([math]::Round((Get-Item $local).Length / 1KB)) KB)"
