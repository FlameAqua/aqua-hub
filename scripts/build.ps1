# Restores (locked), builds and tests Aqua Hub, then audits the packages: what CI does. Stops at the first failure.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Invoke-Step([string]$what, [scriptblock]$step) {
        & $step
        if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
    }
    # Locked: the restore fails if any dependency differs from packages.lock.json; nuget.config requires signed packages.
    Invoke-Step 'Restore' { dotnet restore AquaHub.sln --locked-mode }
    Invoke-Step 'Build' { dotnet build AquaHub.sln -c Release --no-restore }
    Invoke-Step 'Tests' { dotnet test tests/AquaHub.Tests/AquaHub.Tests.csproj -c Release --no-build }
    Write-Host "`nVulnerability audit:" -ForegroundColor Cyan
    $audit = dotnet list AquaHub.sln package --vulnerable --include-transitive 2>&1 | Out-String
    Write-Host $audit
    if ($LASTEXITCODE -ne 0) { throw 'The audit could not run (see above).' }
    if ($audit -match 'has the following vulnerable packages') { throw 'Known-vulnerable packages (see above).' }
}
finally { Pop-Location }
