# Headless visual QA: renders every page (dark, light, narrow, flyout, palette, onboarding) to PNGs
# using live data in an isolated profile. No windows are shown.
param(
    [string]$Out = (Join-Path $env:TEMP 'aquahub-shots'),
    [string]$Profile = (Join-Path $env:TEMP 'aquahub-qa-profile'),
    [string]$Pages = '',
    [int]$Wait = 300
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src/AquaHub/bin') -Recurse -Filter AquaHub.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'Build the app first (scripts/build.ps1).' }
$argList = @('--snapshot', "`"$Out`"", '--data-dir', "`"$Profile`"", '--wait', $Wait)
if ($Pages) { $argList += @('--pages', $Pages) }
$p = Start-Process $exe.FullName -ArgumentList $argList -PassThru -WindowStyle Hidden
$p.WaitForExit()
Get-ChildItem $Out | Format-Table Name, Length
