<#
.SYNOPSIS
Runs named UI Automation components in isolated profiles. Defaults to offline Phase4.
.DESCRIPTION
Examples: -Component Models; -Component Ask,Images; -Component Runtime -Filter T02;
-ListComponents; -Component Images -ListTests; -Plan; -All (explicit long legacy suite).
Offline checks use UIA patterns, synthetic storage and owned HTTP fixtures, not live GPU acceptance.
Legacy checks require -Legacy/-All, interactive profile setup and an idle desktop with owner's Aqua closed.
Dependencies must already be restored; the runner uses --no-restore and never downloads artifacts.
#>
param(
    [string[]]$Component = @(), [switch]$All, [switch]$Legacy,
    [string]$Filter = '', [switch]$Smoke, [switch]$ListComponents,
    [switch]$ListTests, [switch]$Plan, [switch]$Setup, [switch]$NoBuild,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'AquaHub.E2E')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'e2e-selection.ps1')
if ($ListComponents) {
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'e2e-components.json') -Raw | ConvertFrom-Json
    $catalog.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{ Component = $_.Name; Mode = $(if ($_.Value.legacy) { 'Legacy' } else { 'Offline' }); Description = $_.Value.description }
    } | Format-Table -AutoSize
    exit 0
}
$selection = Resolve-E2ESelection -Component $Component -All:$All -Legacy:$Legacy -Smoke:$Smoke -Filter $Filter
if ($Plan) { $selection | ConvertTo-Json; exit 0 }
$offline = $selection.Execution -eq 'Offline'
if ($Setup -and $offline) { throw '-Setup is for legacy warm profiles. Offline profiles are created automatically.' }
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'tests\AquaHub.E2E\AquaHub.E2E.csproj'
$Root = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)).TrimEnd('\', '/')
function Test-Inside([string]$path, [string]$parent) {
    $path.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ($Root -eq [IO.Path]::GetPathRoot($Root).TrimEnd('\') -or (Test-Inside $repo $Root)) { throw 'Root must be a dedicated scratch directory, not a drive or repository ancestor.' }
$protected = @('src', 'tests', 'docs', '.git', 'publish', 'scripts') | ForEach-Object { Join-Path $repo $_ }
$protected += @((Join-Path $env:LOCALAPPDATA 'AquaHub'), (Join-Path $env:LOCALAPPDATA 'AquaHub.App'))
foreach ($path in $protected) {
    if ((Test-Inside $Root $path) -or (Test-Inside $path $Root)) { throw "Root overlaps a protected directory: $path" }
}
for ($path = $Root; $path; $path = Split-Path $path -Parent) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Root traverses a linked directory: $path" }
}
$bin = Join-Path $Root 'e2e-bin'
$exe = Join-Path $bin 'AquaHub.exe'
$runs = Join-Path $Root 'e2e-runs'
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runDir = Join-Path $runs $runId
$pristine = $(if ($offline) { Join-Path $runDir 'baseline' } else { Join-Path $Root 'e2e-profile-pristine' })
foreach ($path in @($bin, $runs, $pristine)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Scratch child is a linked directory: $path" }
}
if (-not $ListTests) {
    $running = @(Get-Process AquaHub -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        if ($process.Path -and (Test-Inside $process.Path $bin)) { throw "An instance uses this build (pid $($process.Id)). Close it or choose another -Root; the runner will not kill it." }
    }
    if (-not $offline -and $running.Count -gt 0) { throw 'Legacy tests use desktop input. Quit your own Aqua Hub before running them.' }
    if (-not $NoBuild -or -not (Test-Path -LiteralPath $exe)) {
        Write-Host "Building isolated app: $bin" -ForegroundColor Cyan
        dotnet build (Join-Path $repo 'src\AquaHub\AquaHub.csproj') -c Release --no-restore -nologo -v q "-p:OutDir=$bin/"
        if ($LASTEXITCODE -ne 0) { throw 'App build failed. Restore approved dependencies separately if needed.' }
    }
}
$suitePath = (& dotnet msbuild $project -nologo -getProperty:TargetPath -p:Configuration=Release | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $suitePath) { throw 'Could not resolve the E2E assembly path.' }
if (-not $NoBuild -or -not (Test-Path -LiteralPath $suitePath)) {
    dotnet build $project -c Release --no-restore -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'E2E build failed. Restore approved dependencies separately if needed.' }
}
$argsForTest = @('test', $project, '-c', 'Release', '--no-build', '--no-restore')
if ($selection.Filter) { $argsForTest += @('--filter', $selection.Filter) }
if ($ListTests) {
    $discovery = & dotnet @argsForTest --list-tests 2>&1
    $discovery | Write-Host
    if ($LASTEXITCODE -ne 0 -or -not ($discovery -match '^\s+AquaHub\.E2E\.Tests\.')) { throw 'Discovery failed or the selection matched no tests.' }
    exit 0
}
New-Item -ItemType Directory -Path $Root -Force | Out-Null
if ($offline) {
    New-Item -ItemType Directory -Path $pristine -Force | Out-Null
    $settingsJson = '{"onboardingComplete":true,"general":{"hotkeyFlyout":"","hotkeyPalette":"","launchAtStartup":false},"ai":{"enabled":false,"manageOllama":false,"startOllamaOnDemand":false,"llama":{"useOllamaModels":false,"useOllamaEngine":false}},"ask":{"web":false,"computer":false,"planWithModel":false}}'
    [IO.File]::WriteAllText((Join-Path $pristine 'settings.json'), $settingsJson, [Text.UTF8Encoding]::new($false))
}
else {
    $settings = Join-Path $pristine 'settings.json'
    $ready = (Test-Path -LiteralPath $settings) -and ((Get-Content -LiteralPath $settings -Raw) -match '"onboardingComplete":\s*true')
    if ($Setup -or -not $ready) {
        # Preserve old fixtures; never recursively delete a profile.
        if (Test-Path -LiteralPath $pristine) {
            $backup = Join-Path $Root ('e2e-profile-backup-' + [guid]::NewGuid().ToString('N'))
            if (-not (Test-Inside $pristine $Root) -or -not (Test-Inside $backup $Root)) { throw 'Invalid profile paths.' }
            Move-Item -LiteralPath $pristine -Destination $backup
        }
        New-Item -ItemType Directory -Path $pristine | Out-Null
        Write-Host 'Legacy setup: finish onboarding in the scratch app, choose Dublin, then return here. Optional model/caches are used only by legacy tests.'
        $app = Start-Process -FilePath $exe -ArgumentList @('--e2e', '--data-dir', "`"$pristine`"") -PassThru
        try {
            Read-Host 'Press Enter when the scratch profile is ready' | Out-Null
            if (-not $app.HasExited) {
                $quit = Start-Process -FilePath $exe -ArgumentList @('--e2e', '--data-dir', "`"$pristine`"", '--quit') -WindowStyle Hidden -PassThru
                try { if (-not $quit.WaitForExit(20000)) { $quit.Kill(); throw 'Quit signalling timed out.' } } finally { $quit.Dispose() }
                if (-not $app.WaitForExit(50000)) { $app.Kill(); throw 'Scratch setup did not shut down.' }
            }
        } finally { $app.Dispose() }
        if (-not ((Test-Path -LiteralPath $settings) -and ((Get-Content -LiteralPath $settings -Raw) -match '"onboardingComplete":\s*true'))) { throw 'Onboarding is incomplete.' }
    }
}
$resultsDir = Join-Path $runDir 'results'
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
$variables = @('AQUAHUB_E2E_ROOT', 'AQUAHUB_E2E_EXE', 'AQUAHUB_E2E_PRISTINE', 'AQUAHUB_E2E_RUNS', 'AQUAHUB_E2E_RUN_ID', 'AQUAHUB_E2E_OFFLINE', 'AQUAHUB_LIVE')
$saved = @{}
foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$started = [DateTimeOffset]::UtcNow
$code = 1
try {
    $env:AQUAHUB_E2E_ROOT = $Root
    $env:AQUAHUB_E2E_EXE = $exe
    $env:AQUAHUB_E2E_PRISTINE = $pristine
    $env:AQUAHUB_E2E_RUNS = $runs
    $env:AQUAHUB_E2E_RUN_ID = $runId
    $env:AQUAHUB_E2E_OFFLINE = $(if ($offline) { '1' } else { '' })
    if ($offline) { Remove-Item Env:AQUAHUB_LIVE -ErrorAction SilentlyContinue }
    Write-Host "Running $($selection.Components -join ', ') [$($selection.Execution)]: $($selection.Filter)" -ForegroundColor Cyan
    if (-not $offline) { Write-Host 'Legacy input: keep the desktop idle until this run finishes.' -ForegroundColor Yellow }
    & dotnet @argsForTest --logger 'console;verbosity=normal' --logger 'trx;LogFileName=e2e.trx' --results-directory $resultsDir -- RunConfiguration.TreatNoTestsAsError=true
    $code = $LASTEXITCODE
    $counts = $null
    if (Test-Path -LiteralPath (Join-Path $resultsDir 'e2e.trx')) {
        [xml]$report = Get-Content -LiteralPath (Join-Path $resultsDir 'e2e.trx') -Raw
        $counts = $report.TestRun.ResultSummary.Counters
    }
    if (-not $counts -or [int]$counts.executed -eq 0) { $code = 1; Write-Host 'No tests executed; this run fails.' -ForegroundColor Red }
    $manifest = [ordered]@{
        components = @($selection.Components); execution = $selection.Execution; filter = $selection.Filter
        startedUtc = $started.ToString('O'); seconds = [math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 2)
        exitCode = $code; executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        applicationSha256 = (Get-FileHash -LiteralPath (Join-Path $bin 'AquaHub.dll') -Algorithm SHA256).Hash
        coreSha256 = (Get-FileHash -LiteralPath (Join-Path $bin 'AquaHub.Core.dll') -Algorithm SHA256).Hash
        testAssemblySha256 = (Get-FileHash -LiteralPath $suitePath -Algorithm SHA256).Hash
        total = $(if ($counts) { [int]$counts.total } else { 0 }); passed = $(if ($counts) { [int]$counts.passed } else { 0 }); failed = $(if ($counts) { [int]$counts.failed } else { 0 })
        executed = $(if ($counts) { [int]$counts.executed } else { 0 }); skipped = $(if ($counts) { [int]$counts.notExecuted } else { 0 })
        evidence = $(if ($offline) { 'UIA, synthetic storage and owned HTTP fixtures; no live engine/GPU acceptance.' } else { 'Legacy UI flows; inspect journals for intercepted effects.' })
    }
    [IO.File]::WriteAllText((Join-Path $resultsDir 'run.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
}
finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    Write-Host "Results: $runDir" -ForegroundColor Cyan
}
exit $code
