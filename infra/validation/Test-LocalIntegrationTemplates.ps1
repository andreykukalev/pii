[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$infra = Split-Path $PSScriptRoot -Parent
$checks = 0
foreach ($file in Get-ChildItem (Join-Path $infra 'parameters') -Filter '*.bicepparam') {
    $output = Join-Path $env:TEMP ('pii-param-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $diagnostics = @(& az bicep build-params --file $file.FullName --outfile $output 2>&1)
        if ($LASTEXITCODE -ne 0 -or $diagnostics.Count -gt 0) { throw "Parameter compilation failed: $($file.Name): $($diagnostics -join '; ')" }
        $checks++
    } finally { if (Test-Path $output) { Remove-Item $output -Force } }
}
foreach ($file in @(Get-ChildItem (Join-Path $infra 'local') -Filter '*.json.example') + @(Get-ChildItem (Join-Path $infra 'parameters') -Filter '*.json')) {
    $null = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $checks++
}
$shared = Get-Content (Join-Path $infra 'local/appsettings.Development.json.example') -Raw | ConvertFrom-Json
if ($shared.Pii.Mode -ne 'FixtureReplay' -or $shared.Pii.Telemetry.Enabled -ne $false -or $shared.Pii.Blob.PSObject.Properties.Name -contains 'ConnectionString') { throw 'Local default must isolate fixtures, disable telemetry and avoid ambiguous Blob credentials.' }
$checks++
$web = Get-Content (Join-Path $infra 'local/web.secrets.json.example') -Raw
if ($web -match 'PII_.*API_KEY|Pii:Model|Pii:Sources') { throw 'Web must not receive worker secrets.' }
$checks++
$developer = Get-Content (Join-Path $infra 'sql/developer-users.sql') -Raw
if ($developer -match 'db_owner|db_ddladmin|GRANT ALTER|GRANT CREATE|GRANT CONTROL|\bDROP\b|TRUNCATE' -or -not $developer.Contains('DeveloperObjectId') -or -not $developer.Contains('DeveloperPrincipalType')) { throw 'Developer bootstrap must use explicit user/group IDs and no DDL/destruction.' }
if ($developer -match 'DELETE ON OBJECT::dbo.SourceCredit|UPDATE ON OBJECT::dbo.SourceCreditEntries') { throw 'Immutable source credit grants must remain narrow.' }
$checks++
Write-Output "Passed $checks compiled parameter, JSON template and developer-grant contracts. No SQL execution or authenticated Azure calls."