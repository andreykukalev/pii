[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$infra = Split-Path $PSScriptRoot -Parent
. (Join-Path $infra 'scripts/Deployment.Common.ps1')
$checks = 0
$projectionRoot = Join-Path ([IO.Path]::GetTempPath()) ('pii-projections-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $projectionRoot 'parameters') -Force
try {
Copy-Item (Join-Path $infra 'stages') $projectionRoot -Recurse
Copy-Item (Join-Path $infra 'modules') $projectionRoot -Recurse
$synthetic = Read-PiiManifest (Join-Path $PSScriptRoot 'fixtures/manifest.json')
$synthetic | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $projectionRoot 'parameters/manifest.json') -Encoding UTF8
foreach ($file in Get-ChildItem (Join-Path $infra 'parameters') -Filter '*.bicepparam') {
    $projectionFile = Join-Path $projectionRoot "parameters/$($file.Name)"
    Copy-Item $file.FullName $projectionFile
    $output = Join-Path ([IO.Path]::GetTempPath()) ('pii-param-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $diagnostics = @(& az bicep build-params --file $projectionFile --outfile $output 2>&1)
        if ($LASTEXITCODE -ne 0 -or $diagnostics.Count -gt 0) { throw "Parameter compilation failed: $($file.Name): $($diagnostics -join '; ')" }
        $checks++
        $projected = Get-Content $output -Raw | ConvertFrom-Json
        foreach ($parameter in $projected.parameters.PSObject.Properties) {
            $stageParameter = $synthetic.stages.($file.BaseName).parameters.PSObject.Properties[$parameter.Name]
            if ($null -ne $stageParameter) { $expected = $stageParameter.Value } else { $expected = $synthetic.($parameter.Name) }
            $actualJson = ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $parameter.Value.value) -Depth 100 -Compress
            $expectedJson = ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $expected) -Depth 100 -Compress
            if ($actualJson -cne $expectedJson) { throw "Projection/resolver mismatch: $($file.BaseName).$($parameter.Name)" }
            $checks++
        }
    } finally { if (Test-Path $output) { Remove-Item $output -Force } }
}
} finally { Remove-Item $projectionRoot -Recurse -Force }
foreach ($file in Get-ChildItem (Join-Path $infra 'parameters/local') -Filter '*.json') {
    $null = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $checks++
}
$shared = Get-Content (Join-Path $infra 'parameters/local/appsettings.Development.json') -Raw | ConvertFrom-Json
if ($shared.Pii.Mode -ne 'FixtureReplay' -or $shared.Pii.Telemetry.Enabled -ne $false -or $shared.Pii.Blob.PSObject.Properties.Name -contains 'ConnectionString') { throw 'Local default must isolate fixtures, disable telemetry and avoid ambiguous Blob credentials.' }
$checks++
$web = Get-Content (Join-Path $infra 'parameters/local/web.secrets.json') -Raw
if ($web -match 'PII_.*API_KEY|Pii:Model|Pii:Sources') { throw 'Web must not receive worker secrets.' }
$checks++
$developer = Get-Content (Join-Path $infra 'sql/developer-users.sql') -Raw
if ($developer -match 'db_owner|db_ddladmin|GRANT ALTER|GRANT CREATE|GRANT CONTROL|\bDROP\b|TRUNCATE' -or -not $developer.Contains('DeveloperObjectId') -or -not $developer.Contains('DeveloperPrincipalType')) { throw 'Developer bootstrap must use explicit user/group IDs and no DDL/destruction.' }
if ($developer -match 'DELETE ON OBJECT::dbo.SourceCredit|UPDATE ON OBJECT::dbo.SourceCreditEntries') { throw 'Immutable source credit grants must remain narrow.' }
$checks++
Write-Output "Passed $checks compiled parameter, JSON template and developer-grant contracts. No SQL execution or authenticated Azure calls."