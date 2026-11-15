<#
  Builds both apps for 64-bit Raspberry Pi OS and copies them, with the setup files, to the Pi over SSH, straight
  from Windows: the same as publish.sh, without WSL or rsync. Both apps go to one Pi 5 unless -SignalHost names a
  second Pi for the signal box.

    .\deploy\publish.ps1                                  # to you@np.local, your Windows user name
    .\deploy\publish.ps1 -PiHost pi@np.local
    .\deploy\publish.ps1 -PiHost pi@np.local -SignalHost pi@signalbox.local

  The apps need the .NET 10 runtime on the Pi, which setup.sh installs. Each copy replaces the folder in your home
  directory on the Pi (~/northpole-hq, ~/northpole-signalbox, ~/northpole-setup); the running services read from
  /opt/northpole, so nothing changes for them until setup.sh or the update lines it prints copy the new build in.
#>
param(
    [string]$PiHost = "$env:USERNAME@np.local",
    [string]$SignalHost = $PiHost
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $env:TEMP 'northpole-publish'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

function Build([string]$project, [string]$name) {
    Write-Host "Building $project for linux-arm64..."
    $out = Join-Path $work $name
    dotnet publish (Join-Path $root "src/$project") -c Release -r linux-arm64 --self-contained false -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $project failed" }
    $out
}

# Packs a folder and swaps it in for ~/<name> on the Pi, so no stale files stay behind (like rsync --delete).
function Copy-ToPi([string]$folder, [string]$pi, [string]$name) {
    Write-Host "Copying it to ${pi}:~/$name ..."
    $tgz = Join-Path $work "$name.tgz"
    tar -C $folder -czf $tgz .
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
    scp -q $tgz "${pi}:$name.tgz"
    if ($LASTEXITCODE -ne 0) { throw "scp to $pi failed" }
    ssh $pi "rm -rf ~/$name && mkdir ~/$name && tar -xzf ~/$name.tgz -C ~/$name && rm ~/$name.tgz && chmod -R u+rwX,go+rX ~/$name"
    if ($LASTEXITCODE -ne 0) { throw "unpacking on $pi failed" }
}

$hq = Build 'NorthPole.Hq' 'hq'
$signalbox = Build 'NorthPole.SignalBox' 'signalbox'
$setup = Join-Path $root 'deploy/pi'

Copy-ToPi $hq $PiHost 'northpole-hq'
Copy-ToPi $setup $PiHost 'northpole-setup'
Copy-ToPi $signalbox $SignalHost 'northpole-signalbox'
if ($SignalHost -ne $PiHost) { Copy-ToPi $setup $SignalHost 'northpole-setup' }
Remove-Item $work -Recurse -Force

Write-Host ""
Write-Host "Done. First time: ssh to the Pi and run 'cd ~/northpole-setup && bash setup.sh', then reboot."
if ($SignalHost -ne $PiHost) {
    Write-Host "      Two Pis: 'bash setup.sh hq' on $PiHost, 'bash setup.sh signalbox' on $SignalHost."
}
Write-Host "Updates: sudo rsync -a --exclude 'appsettings*.json' ~/northpole-hq/ /opt/northpole/hq/ && sudo systemctl restart northpole-hq"
Write-Host "         sudo rsync -a --exclude 'appsettings*.json' ~/northpole-signalbox/ /opt/northpole/signalbox/ && sudo systemctl restart northpole-signalbox"
Write-Host "         (--exclude keeps your edited settings: the family, the speaker and the sensors)"
