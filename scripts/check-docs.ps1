# Fast, offline checks for the maintained Markdown. No app build, network or profile access.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$prefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$issues = [Collections.Generic.List[string]]::new()

foreach ($archive in @('docs/handoff', 'docs/screenshots')) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $archive)) {
        $issues.Add("$archive is an archive location; keep handoffs, captures and scratch builds outside maintained docs.")
    }
}

$files = @('README.md', 'AGENTS.md') | ForEach-Object { Get-Item -LiteralPath (Join-Path $repoRoot $_) }
$files += @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter '*.md' -File -Recurse)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($repoRoot, $file.FullName)
    try { $body = $utf8.GetString([IO.File]::ReadAllBytes($file.FullName)) }
    catch { $issues.Add("${relative}: invalid UTF-8: $($_.Exception.Message)"); continue }
    if ($body.Contains("`r")) { $issues.Add("${relative}: use LF line endings.") }

    # Fenced examples may contain illustrative links. Check actual inline Markdown links/images only.
    $markdown = [regex]::Replace($body, '(?ms)^```[^\n]*\n.*?^```[ \t]*(?:\n|$)', '')
    foreach ($match in [regex]::Matches($markdown, '\[[^\]\r\n]*\]\((?<target><[^>]+>|[^)\r\n]+)\)')) {
        $href = $match.Groups['target'].Value.Trim()
        if ($href.StartsWith('<') -and $href.EndsWith('>')) { $href = $href.Substring(1, $href.Length - 2) }
        if ($href.StartsWith('#') -or $href -match '^[a-z][a-z0-9+.-]*:' -or $href.StartsWith('//')) { continue }
        $path = [Uri]::UnescapeDataString(($href -split '[#?]', 2)[0])
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        try {
            $target = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $path))
            if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $issues.Add("${relative}: local link leaves the repository: $href")
            }
            elseif (-not (Test-Path -LiteralPath $target)) {
                $issues.Add("${relative}: missing local link target: $href")
            }
        }
        catch { $issues.Add("${relative}: invalid local link: $href") }
    }
}

if ($issues.Count -gt 0) { throw ($issues -join [Environment]::NewLine) }
Write-Output "Checked $($files.Count) Markdown files: UTF-8/LF, local link targets and archive locations."
