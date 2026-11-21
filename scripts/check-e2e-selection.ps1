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
Assert-Selection ((Resolve-E2ESelection -Component Streams).Filter -eq 'Execution=Offline&(Component=Streams)') 'Streams must be independently selectable.'
Assert-Selection ((Resolve-E2ESelection -Component Reminders -Filter T02).Filter -eq 'Execution=Offline&(Component=Reminders)&FullyQualifiedName~T02') 'Select Reminders by case.'
Assert-Selection ((Resolve-E2ESelection -Legacy -Component Settings).Filter -eq 'Execution=Legacy&(Component=Settings)') 'Legacy module selection failed.'
Assert-Rejected { Resolve-E2ESelection -Component Unknown }
Assert-Rejected { Resolve-E2ESelection -Component Settings }
Assert-Rejected { Resolve-E2ESelection -All -Component Models }
Assert-Rejected { Resolve-E2ESelection -Component Models -Filter 'T01|T02' }
Assert-Selection ((Resolve-E2ESelection -Component Search -Filter T03).Filter -eq 'Execution=Offline&(Component=Search)&FullyQualifiedName~T03') 'Select Search by individual case.'
Assert-Selection ((Resolve-E2ESelection -Component Creativity -Filter T01).Filter -eq 'Execution=Offline&(Component=Creativity)&FullyQualifiedName~T01') 'Select the off-screen Creativity component.'
Assert-Selection ((Resolve-E2ESelection -Component Recipes -Filter T01).Filter -eq 'Execution=Offline&(Component=Recipes)&FullyQualifiedName~T01') 'Select the off-screen Recipes component.'
'E2E selection checks passed (13 cases).'
