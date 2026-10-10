[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$documents = @(Get-ChildItem (Join-Path $root 'docs') -Recurse -Filter '*.md')
if ($documents.Count -eq 0) { throw 'Documentation files required.' }
$checked = 0
foreach ($document in $documents) {
    $text = Get-Content -LiteralPath $document.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^\)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $path = [Uri]::UnescapeDataString(($target -split '#')[0])
        if (-not (Test-Path -LiteralPath (Join-Path $document.DirectoryName $path))) { throw "Broken local document link in $($document.Name): $target" }
        $checked++
    }
}
foreach ($script in Get-ChildItem (Join-Path $root 'infra/validation') -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell syntax error in $($script.Name): $($errors.Message -join '; ')" }
}
Write-Output "Passed $checked local links across $($documents.Count) documents and parsed all validation scripts. Link checks do not establish feature or live-service readiness."