<#
.SYNOPSIS
    Runs the UI Automation end-to-end suite (tests/AquaHub.E2E) against a separate build of Aqua Hub.

.DESCRIPTION
    Everything lives in one folder (default %LOCALAPPDATA%\AquaHub.E2E), apart from your own Aqua Hub and its profile:
      e2e-bin\               the app under test, rebuilt from this checkout on every run
      e2e-profile-pristine\  a warm profile (onboarding done, news and markets cached); each test class gets a copy
      e2e-runs\<time>\       results: run.log, screenshots\, results\ (findings) and the copied profiles

    The first run creates the warm profile: Aqua Hub opens in its dry-run sandbox on an empty profile, you finish
    onboarding, and press Enter here once Today has loaded. Every copy of the app the suite starts runs with --e2e, so
    side effects (opening apps, media keys, the Start with Windows entry...) go to a journal instead of happening.

    Quit your own Aqua Hub first, and keep your hands off the mouse and keyboard while the suite runs: it clicks and
    types in real windows.

    Windows PowerShell blocks scripts by default; run it as:
      powershell -ExecutionPolicy Bypass -File .\scripts\e2e.ps1

.PARAMETER Filter
    Run part of the suite, e.g. -Filter A11 (one class) or -Filter A11_SettingsTests.T02_General (one test).

.PARAMETER Smoke
    A quick check (a few minutes, no model needed): start-up, every page, the command palette, Settings and its
    search, the update banner and quitting. Combines with -Filter.

.PARAMETER Setup
    Create the warm profile again (for example to change the model or place it uses).

.PARAMETER NoBuild
    Reuse the last build of the app under test.

.PARAMETER Root
    Folder for the build, the warm profile and the results.
#>
param(
    [string]$Filter = '',
    [switch]$Smoke,
    [switch]$Setup,
    [switch]$NoBuild,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'AquaHub.E2E')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# A full path (relative to where you ran this), so the leftover-process check below compares like with like.
$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
$bin = Join-Path $Root 'e2e-bin'
$exe = Join-Path $bin 'AquaHub.exe'
$pristine = Join-Path $Root 'e2e-profile-pristine'
$runs = Join-Path $Root 'e2e-runs'

function Test-SuiteProcess($p) { $p.Path -and $p.Path.StartsWith($bin + '\', [StringComparison]::OrdinalIgnoreCase) }
function Stop-SuiteProcess($p) { try { if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } } catch { } }

# Your own Aqua Hub would answer the suite's hotkeys and share the tray, so it has to be closed first.
$own = @(Get-Process AquaHub -ErrorAction SilentlyContinue | Where-Object { -not (Test-SuiteProcess $_) })
if ($own.Count -gt 0) {
    Write-Host "Aqua Hub is running (pid $($own.Id -join ', ')). Quit it from its tray icon (right-click, Quit) and run this again." -ForegroundColor Yellow
    exit 1
}
# Test instances left behind by an interrupted run (only ones started from the suite's own build folder).
Get-Process AquaHub -ErrorAction SilentlyContinue | Where-Object { Test-SuiteProcess $_ } | ForEach-Object {
    Write-Host "Closing a leftover test instance (pid $($_.Id))"
    Stop-SuiteProcess $_
}

if (-not $NoBuild -or -not (Test-Path $exe)) {
    Write-Host "Building the app under test into $bin" -ForegroundColor Cyan
    dotnet build (Join-Path $repo 'src\AquaHub\AquaHub.csproj') -c Release -nologo -v q "-p:OutDir=$bin/"
    if ($LASTEXITCODE -ne 0) { throw 'The app did not build (see the errors above).' }
}

# Compile the suite now, so a build error shows up before the (interactive) warm-profile step, not after it.
Write-Host 'Building the test suite' -ForegroundColor Cyan
dotnet build (Join-Path $repo 'tests\AquaHub.E2E\AquaHub.E2E.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The test suite did not build (see the errors above).' }

$settings = Join-Path $pristine 'settings.json'
$ready = (Test-Path $settings) -and ((Get-Content $settings -Raw) -match '"onboardingComplete":\s*true')
if ($Setup -or -not $ready) {
    if (Test-Path $pristine) { Remove-Item $pristine -Recurse -Force }
    New-Item -ItemType Directory $pristine | Out-Null
    Write-Host ''
    Write-Host 'Creating the warm test profile. Aqua Hub opens in its dry-run sandbox on a new, empty profile:' -ForegroundColor Cyan
    Write-Host '  1. Finish onboarding. At "Where are you?" search for Dublin and pick Dublin, Ireland (the tests expect it;'
    Write-Host '     a profile without a place gets Dublin anyway). Other answers can be anything; nothing leaves this folder.'
    Write-Host '  2. Optional: Settings > AI & models > Model, to pick the model the suite talks to (a fast one keeps it quick).'
    Write-Host '  3. Leave it on Today for a minute so news, markets and weather are cached.'
    Write-Host '  4. Come back here and press Enter (this closes that window).'
    $app = Start-Process -FilePath $exe -ArgumentList @('--e2e', '--data-dir', "`"$pristine`"") -PassThru
    Read-Host 'Press Enter when Today has loaded' | Out-Null
    if (-not $app.HasExited) {
        # Ask the running copy to quit (the same command the suite uses), then give it time to save.
        $quit = Start-Process -FilePath $exe -ArgumentList @('--e2e', '--data-dir', "`"$pristine`"", '--quit') -PassThru
        if (-not $quit.WaitForExit(15000)) { Stop-SuiteProcess $quit }
        if (-not $app.WaitForExit(20000)) {
            Write-Host 'The warm-profile copy did not quit within 20 s; closing it.' -ForegroundColor Yellow
            Stop-SuiteProcess $app
        }
    }
    if (-not ((Test-Path $settings) -and ((Get-Content $settings -Raw) -match '"onboardingComplete":\s*true'))) {
        throw 'Onboarding was not finished, so there is no warm profile yet. Run this again to retry.'
    }
    Write-Host "Warm profile saved in $pristine" -ForegroundColor Green
}

$env:AQUAHUB_E2E_EXE = $exe
$env:AQUAHUB_E2E_PRISTINE = $pristine
$env:AQUAHUB_E2E_RUNS = $runs
$testArgs = @('test', (Join-Path $repo 'tests\AquaHub.E2E\AquaHub.E2E.csproj'), '-c', 'Release', '--no-build', '--logger', 'console;verbosity=normal')
$filters = @()
if ($Smoke) { $filters += 'Category=Smoke' }
if ($Filter) { $filters += "FullyQualifiedName~$Filter" }
if ($filters.Count -gt 0) { $testArgs += @('--filter', ($filters -join '&')) }
Write-Host ''
Write-Host 'Running the suite: hands off the mouse and keyboard until it finishes.' -ForegroundColor Cyan
dotnet @testArgs
$code = $LASTEXITCODE
$last = Get-ChildItem $runs -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
if ($last) { Write-Host "Results: $($last.FullName)  (run.log, screenshots\, results\)" -ForegroundColor Green }
exit $code
