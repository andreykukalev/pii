[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$documents = @(Get-ChildItem (Join-Path $root 'docs') -Recurse -Filter '*.md') + @(Get-ChildItem (Join-Path $root 'infra') -Recurse -Filter '*.md') + @(Get-Item (Join-Path $root 'README.md'))
if ($documents.Count -eq 0) { throw 'Documentation files required.' }
$checked = 0
$configurationChecks = 0
$configurationPaths = @('manifest.json','secure.json','foundation.bicepparam','foundry.bicepparam','storage.bicepparam','data.bicepparam','monitoring.bicepparam','compute.bicepparam','application.bicepparam','local/appsettings.Development.json','local/worker.settings.json','local/web.secrets.json','local/worker.secrets.json')
foreach ($configurationPath in $configurationPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "infra/parameters/$configurationPath") -PathType Leaf)) { throw "Missing renamed configuration: $configurationPath" }
    $configurationChecks++
}
if (@(Get-ChildItem (Join-Path $root 'infra') -File -Recurse | Where-Object { $_.Name -match '\.example(\.|$)' }).Count -ne 0) { throw 'Obsolete .example configuration filename remains.' }
$configurationChecks++
foreach ($document in $documents) {
    $text = Get-Content -LiteralPath $document.FullName -Raw
    if ($text -match 'PII_SUBSCRIPTION_ID|PII_MANIFEST_PATH|PiiSubscriptionId|deploy-[\w-]+\.ps1[^\r\n]*-SubscriptionId|\$(subscription|tenant)\s*=\s*[''\"]<actual-') { throw "Obsolete deployment selector or independent subscription/tenant example in $($document.Name)." }
    $configurationChecks++
    foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^\)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $path = [Uri]::UnescapeDataString(($target -split '#')[0])
        if (-not (Test-Path -LiteralPath (Join-Path $document.DirectoryName $path))) { throw "Broken local document link in $($document.Name): $target" }
        $checked++
    }
}
foreach ($guide in @('infra/README.md','infra/local/README.md')) {
    $text = Get-Content (Join-Path $root $guide) -Raw
    if (-not $text.Contains('$subscription = (Get-Content $manifest -Raw | ConvertFrom-Json).subscriptionId')) { throw "Raw CLI subscription must derive from selected manifest: $guide" }
    $configurationChecks++
}
$localGuide = Get-Content (Join-Path $root 'infra/local/README.md') -Raw
if (-not $localGuide.Contains('$tenant = (Get-Content $manifest -Raw | ConvertFrom-Json).shared.auth.tenantId')) { throw 'Login tenant must derive from selected manifest.' }
$configurationChecks++
foreach ($script in Get-ChildItem (Join-Path $root 'infra') -Filter '*.ps1' -Recurse) {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell syntax error in $($script.Name): $($errors.Message -join '; ')" }
}
Write-Output "Passed $checked local links across $($documents.Count) documents, $configurationChecks configuration filename checks, and parsed all validation scripts. Link checks do not establish feature or live-service readiness."