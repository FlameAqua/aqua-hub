# Pure selection logic shared by the runner and its offline checks. Does not build, launch or change profiles.
function Resolve-E2ESelection {
    param([string[]]$Component, [switch]$All, [switch]$Legacy, [switch]$Smoke, [string]$Filter = '')
    if ($All -and $Component.Count -gt 0) { throw '-All and -Component are mutually exclusive.' }
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'e2e-components.json') -Raw | ConvertFrom-Json
    $names = @($catalog.PSObject.Properties.Name)
    $chosen = @($Component | Where-Object { $_ } | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    if (-not $All -and $chosen.Count -eq 0 -and -not $Legacy) { $chosen = @('Phase4') }
    $expanded = @()
    foreach ($name in $chosen) {
        if ($name -notin $names) { throw "Unknown component '$name'. Use -ListComponents." }
        $entry = $catalog.$name
        if ($entry.legacy -and -not $Legacy) { throw "'$name' requires -Legacy; it may use desktop input, network or a configured model." }
        if ($entry.components) { $expanded += @($entry.components) } else { $expanded += $name }
    }
    $parts = @()
    if (-not $All) { $parts += $(if ($Legacy) { 'Execution=Legacy' } else { 'Execution=Offline' }) }
    if ($expanded.Count -gt 0) { $parts += '(' + ((@($expanded | Select-Object -Unique) | ForEach-Object { "Component=$_" }) -join '|') + ')' }
    if ($Smoke) { $parts += 'Category=Smoke' }
    if ($Filter) {
        if ($Filter -match '[&|()]') { throw '-Filter accepts a test/class name fragment, not a raw expression.' }
        $parts += "FullyQualifiedName~$Filter"
    }
    [pscustomobject]@{
        Components = $(if ($All) { @('All') } elseif ($chosen.Count) { $chosen } else { @('Legacy') })
        Execution = $(if ($All -or $Legacy) { 'Legacy' } else { 'Offline' })
        Filter = $parts -join '&'
    }
}
