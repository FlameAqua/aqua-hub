# Produces a framework-dependent, ReadyToRun Release build in ./publish (uses the installed .NET Desktop runtime).
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish'
$args = @('publish', (Join-Path $root 'src/AquaHub/AquaHub.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $out,
          '-p:PublishReadyToRun=true', '-p:DebugType=none')
if ($SelfContained) { $args += @('--self-contained', 'true') } else { $args += @('--self-contained', 'false') }
dotnet @args
Write-Host "Published to $out" -ForegroundColor Green
