# Restores (locked), builds and tests Aqua Hub.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # Locked: the restore fails if any dependency differs from packages.lock.json; nuget.config requires signed packages.
    dotnet restore AquaHub.sln --locked-mode
    dotnet build AquaHub.sln -c Release --no-restore
    dotnet test tests/AquaHub.Tests/AquaHub.Tests.csproj -c Release --no-build
    Write-Host "`nVulnerability audit:" -ForegroundColor Cyan
    dotnet list AquaHub.sln package --vulnerable --include-transitive
}
finally { Pop-Location }
