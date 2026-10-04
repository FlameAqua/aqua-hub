# Restores (locked), builds and tests Aqua Hub, builds the UI tests, then audits the packages: what CI does (but its
# runs under other cultures). Stops at the first failure.
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
    # The UI tests aren't in the solution (and aren't locked): built here as CI builds them, not run.
    Invoke-Step 'Restore the UI tests' { dotnet restore tests/AquaHub.E2E/AquaHub.E2E.csproj }
    Invoke-Step 'Build the UI tests' { dotnet build tests/AquaHub.E2E/AquaHub.E2E.csproj -c Release --no-restore }
    # A run that finds no tests fails rather than passes.
    Invoke-Step 'Tests' { dotnet test tests/AquaHub.Tests/AquaHub.Tests.csproj -c Release --no-build -- RunConfiguration.TreatNoTestsAsError=true }
    Write-Host "`nVulnerability audit:" -ForegroundColor Cyan
    $audit = dotnet list AquaHub.sln package --vulnerable --include-transitive 2>&1 | Out-String
    Write-Host $audit
    if ($LASTEXITCODE -ne 0) { throw 'The audit could not run (see above).' }
    if ($audit -match 'has the following vulnerable packages') { throw 'Known-vulnerable packages (see above).' }
}
finally { Pop-Location }
