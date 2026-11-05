# Offline runner regression checks. No UI, profiles, network, dependencies or owner state.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'e2e-selection.ps1')
function Assert-Selection($condition, [string]$message) { if (-not $condition) { throw $message } }
function Assert-Rejected([scriptblock]$action) { try { & $action | Out-Null } catch { return }; throw 'Expected invalid selection to be rejected.' }
$default = Resolve-E2ESelection
Assert-Selection ($default.Execution -eq 'Offline' -and $default.Filter -match 'Component=Models' -and $default.Filter -notmatch 'Execution=Legacy') 'Default must be offline Phase4.'
$pair = Resolve-E2ESelection -Component @('Models', 'Images') -Smoke -Filter 'T01'
Assert-Selection ($pair.Filter -eq 'Execution=Offline&(Component=Models|Component=Images)&Category=Smoke&FullyQualifiedName~T01') 'Component OR and narrowing AND filters changed.'
Assert-Selection ((Resolve-E2ESelection -Component 'Models,Images').Filter -eq 'Execution=Offline&(Component=Models|Component=Images)') 'Comma-separated components failed.'
Assert-Selection ((Resolve-E2ESelection -All).Filter -eq '') 'Explicit All must retain the complete suite.'
Assert-Selection ((Resolve-E2ESelection -Legacy -Component Settings).Filter -eq 'Execution=Legacy&(Component=Settings)') 'Legacy module selection failed.'
Assert-Rejected { Resolve-E2ESelection -Component Unknown }
Assert-Rejected { Resolve-E2ESelection -Component Settings }
Assert-Rejected { Resolve-E2ESelection -All -Component Models }
Assert-Rejected { Resolve-E2ESelection -Component Models -Filter 'T01|T02' }
'E2E selection checks passed (9 cases).'
