<#
  Copies the signal box to the Pi for the bench test mode, straight from Windows: no WSL or rsync needed.
  It builds a self-contained linux-arm64 copy, so the Pi needs no .NET installed, and puts it in ~/northpole-bench.
  Nothing is installed as a service; the full setup is still deploy/publish.sh and setup.sh.

    .\deploy\bench.ps1                         # to you@np.local, your Windows user name
    .\deploy\bench.ps1 -PiHost pi@np.local

  Then test one part at a time, on the Pi:
    ~/northpole-bench/NorthPole.SignalBox bench signal
    ~/northpole-bench/NorthPole.SignalBox bench wait 30
#>
param([string]$PiHost = "$env:USERNAME@np.local")
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $env:TEMP 'northpole-bench'
$tgz = Join-Path $env:TEMP 'northpole-bench.tgz'

Write-Host "Building the signal box for linux-arm64 (self-contained)..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src/NorthPole.SignalBox') -c Release -r linux-arm64 --self-contained true -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host "Copying it to ${PiHost}:~/northpole-bench ..."
tar -C $out -czf $tgz .
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
scp -q $tgz "${PiHost}:northpole-bench.tgz"
if ($LASTEXITCODE -ne 0) { throw "scp to $PiHost failed" }
ssh $PiHost 'rm -rf ~/northpole-bench && mkdir ~/northpole-bench && tar -xzf ~/northpole-bench.tgz -C ~/northpole-bench && rm ~/northpole-bench.tgz && chmod +x ~/northpole-bench/NorthPole.SignalBox'
if ($LASTEXITCODE -ne 0) { throw "unpacking on $PiHost failed" }
Remove-Item $tgz

Write-Host "Done. On the Pi: ~/northpole-bench/NorthPole.SignalBox bench signal"
