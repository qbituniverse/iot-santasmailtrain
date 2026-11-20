<#
  Copies the Pi's nightly snapshots to this PC: the QR card codes, every letter with its gift ideas, the drawings,
  Santa's voice files, and the settings that hold the family and the speaker. deploy/pi/backup.sh makes one on the Pi
  every night at 03:15 and keeps the newest 14 there; the copies here outlive the Pi's disk.

    .\deploy\backup.ps1 -PiHost pi@np.local              # copy the snapshots this PC doesn't have yet, and tidy up
    .\deploy\backup.ps1 -PiHost pi@np.local -Now         # make a snapshot first, then copy: before you reflash the Pi
    .\deploy\backup.ps1 -PiHost pi@np.local -Schedule    # a Windows task that copies daily at 08:00 and at each sign-in

  Kept here: the last 7 days, the Sundays of the last 4 weeks, the 1st of each of the last 6 months, and always the
  newest 7, however old, so a train packed away for the summer keeps its last week. The rest are deleted (a OneDrive
  folder keeps them 30 days more in its recycle bin). Each run adds to fetch.log in the folder, and warns when the
  newest snapshot is over 48 hours old, which means the Pi's nightly snapshot or this copy has stopped. The exit code
  says the same to Task Scheduler: 0 fine, 2 a copy failed, 3 no snapshot or an old one.

  The backup folder is -To, or else the NORTHPOLE_BACKUPS environment variable, or else Documents\NorthPole backups.
  Set the variable once, and this, the daily task and restore.ps1 all use your folder:
    [Environment]::SetEnvironmentVariable('NORTHPOLE_BACKUPS', 'D:\Backups\NorthPole', 'User')

  Copying needs your SSH key on the Pi and never waits for a password, so the task can run on its own. Snapshots hold
  the family's letters but never the Claude key or the PIN.
#>
param(
    [string]$PiHost = "$env:USERNAME@np.local",
    [string]$To = $(if ($env:NORTHPOLE_BACKUPS) { $env:NORTHPOLE_BACKUPS }
                    else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NorthPole backups' }),
    [switch]$Now,
    [switch]$Schedule
)
$ErrorActionPreference = 'Stop'
$OnPi = 'northpole-backups'   # in your home folder on the Pi, where the nightly timer writes
$StaleHours = 48

if ($Schedule) {
    # A hidden window (it may flash for a moment) that keeps the exit code, so Task Scheduler's Last Run Result shows
    # a warning too. A PC that was off at 08:00 catches up when it's next on.
    $arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -PiHost $PiHost -To `"$To`""
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $daily = New-ScheduledTaskTrigger -Daily -At 08:00
    $signIn = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 15)
    $about = "Copies Santa's Mail Train snapshots from $PiHost into $To and tidies old ones (deploy/backup.ps1)."
    try {
        Register-ScheduledTask -TaskName 'NorthPole backup' -Description $about -Action $action -Trigger @($daily, $signIn) `
            -Settings $settings -Force -ErrorAction Stop | Out-Null
        Write-Host "Registered 'NorthPole backup': daily at 08:00, and at each sign-in."
    } catch {
        Register-ScheduledTask -TaskName 'NorthPole backup' -Description $about -Action $action -Trigger $daily `
            -Settings $settings -Force | Out-Null
        Write-Host ("Registered 'NorthPole backup': daily at 08:00, catching up when the PC is next on. " +
            "Windows wanted administrator rights for the sign-in trigger.")
    }
    Write-Host "Its log is $(Join-Path $To 'fetch.log'). Run it now from Task Scheduler, or: .\deploy\backup.ps1 -PiHost $PiHost"
    return
}

# When a snapshot was made, from its name: northpole-np-20261120-031500.tar.gz. Nothing for any other file.
function Get-SnapshotTime([string]$Name) {
    if ($Name -match '^northpole-.+-(\d{8}-\d{6})\.tar\.gz$') {
        [datetime]::ParseExact($Matches[1], 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
    }
}

# Which of these snapshots to keep on this PC, by the rule at the top.
function Select-Kept([string[]]$Names, [datetime]$Today) {
    $dated = @($Names | ForEach-Object {
            $time = Get-SnapshotTime $_
            if ($time) { [pscustomobject]@{ Name = $_; Time = $time } }
        } | Sort-Object Time -Descending)
    $newest = @($dated | Select-Object -First 7 | ForEach-Object Name)
    foreach ($snapshot in $dated) {
        $day = $snapshot.Time.Date
        $age = ($Today - $day).Days
        $months = ($Today.Year * 12 + $Today.Month) - ($day.Year * 12 + $day.Month)
        if ($age -lt 7 -or ($day.DayOfWeek -eq 'Sunday' -and $age -lt 28) -or ($day.Day -eq 1 -and $months -lt 6) -or
            $newest -contains $snapshot.Name) { $snapshot.Name }
    }
}

# ssh and scp never ask for a password here, and their own messages don't stop the script: the exit code says how
# it went.
function Invoke-Pi([string]$Command) {
    $ErrorActionPreference = 'Continue'
    ssh -o BatchMode=yes -o ConnectTimeout=15 $PiHost $Command 2>$null
}
function Copy-FromPi([string]$Path, [string]$Local) {
    $ErrorActionPreference = 'Continue'
    scp -p -q -o BatchMode=yes -o ConnectTimeout=15 "${PiHost}:$Path" $Local 2>$null
}

New-Item -ItemType Directory -Force -Path $To | Out-Null
$log = Join-Path $To 'fetch.log'
if (Test-Path $log) { $tail = @(Get-Content $log -Tail 1000); Set-Content -Path $log -Value $tail -Encoding UTF8 }
function Write-Log([string]$Line) {
    Write-Host $Line
    Add-Content -Path $log -Value $Line -Encoding UTF8
}

$code = 0
$today = (Get-Date).Date
Write-Log "== $(Get-Date -Format 'yyyy-MM-dd HH:mm'): copying from $PiHost into $To"
Get-ChildItem $To -Filter '.northpole-*.part' -Force | Remove-Item -Force   # left by a copy that was cut off

if ($Now) {
    $made = Invoke-Pi 'bash ~/northpole-setup/backup.sh' | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or -not $made) { Write-Log "!! The Pi couldn't make a snapshot"; exit 1 }
    Write-Log "   made $(Split-Path $made -Leaf) on the Pi"
}

$remote = @(Invoke-Pi "ls -1 $OnPi 2>/dev/null; true")
if ($LASTEXITCODE -eq 0) {
    foreach ($name in (Select-Kept $remote $today)) {
        $local = Join-Path $To $name
        if (Test-Path $local) { continue }
        $part = Join-Path $To ".$name.part"
        Copy-FromPi "$OnPi/$name" $part
        if ($LASTEXITCODE -eq 0) {
            Move-Item $part $local -Force
            Write-Log "   copied $name"
        } else {
            Write-Log "!! Couldn't copy $name"
            $code = 2
        }
    }
} else {
    Write-Log "   $PiHost didn't answer (switched off, or packed away?), so nothing new was copied"
}

$here = @(Get-ChildItem $To -Filter 'northpole-*.tar.gz' -File | ForEach-Object Name)
$kept = @(Select-Kept $here $today)
foreach ($name in $here) {
    if ((Get-SnapshotTime $name) -and $kept -notcontains $name) {
        Remove-Item (Join-Path $To $name)
        Write-Log "   removed $name, past the retention"
    }
}

$newest = $kept | Sort-Object { Get-SnapshotTime $_ } | Select-Object -Last 1
if (-not $newest) {
    Write-Log "!! There's no snapshot in $To yet."
    $code = 3
} else {
    $hours = [int][math]::Floor(((Get-Date) - (Get-SnapshotTime $newest)).TotalHours)
    if ($hours -gt $StaleHours) {
        Write-Log ("!! The newest snapshot, $newest, is $hours hours old: check the Pi's nightly snapshot " +
            "(ssh $PiHost northpole-backup check), or switch the task off while the train is packed away.")
        $code = 3
    } else {
        Write-Log "   newest: $newest, $hours hours old; $($kept.Count) kept"
    }
}
exit $code
