[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$path = Join-Path ([IO.Path]::GetTempPath()) ('pii-parameter-test-' + [guid]::NewGuid().ToString('N') + '.json')
$baseline = @{
    location = 'northeurope'
    tenantId = [guid]::NewGuid().ToString()
    allowedObjectId = [guid]::NewGuid().ToString()
    webClientId = [guid]::NewGuid().ToString()
    sqlAdminObjectId = [guid]::NewGuid().ToString()
    sqlAdminName = 'Offline validation only, not an actual administrator'
}
$cases = @(
    @{ Name = 'staged, disabled activation'; Changes = @{}; Pass = $true },
    @{ Name = 'workload configuration only, no activation'; Changes = @{ deployWorkloads = $true; webClientSecret = 'offline-validator-not-a-credential'; sqlAllowAzureServices = $true; webImage = 'test.azurecr.io/pii/web@sha256:' + ('a' * 64); workerImage = 'test.azurecr.io/pii/worker@sha256:' + ('b' * 64) }; Pass = $true },
    @{ Name = 'model configuration only, synthetic rates not a quote'; Changes = @{ liveAuthorized = $true; modelProcessingAuthorized = $true; modelSupportsStrictSchemaAndTools = $true; foundryAccountName = 'offline-test-only'; modelDeploymentName = 'offline-deployment'; modelVersion = 'offline-version'; modelRateCardVersion = 'synthetic-test-rate-card'; modelProcessingRegion = 'offline-test'; modelInputUsdPerMillion = '1'; modelOutputUsdPerMillion = '1' }; Pass = $true },
    @{ Name = 'empty tenant'; Changes = @{ tenantId = [guid]::Empty.ToString() }; Pass = $false },
    @{ Name = 'invalid prefix'; Changes = @{ prefix = 'UPPER_CASE' }; Pass = $false },
    @{ Name = 'connection string injection'; Changes = @{ databaseName = 'pii;User Id=other' }; Pass = $false },
    @{ Name = 'string authorization flag'; Changes = @{ liveAuthorized = 'false' }; Pass = $false },
    @{ Name = 'live missing rights'; Changes = @{ liveAuthorized = $true }; Pass = $false },
    @{ Name = 'unpriced model'; Changes = @{ liveAuthorized = $true; modelProcessingAuthorized = $true; modelSupportsStrictSchemaAndTools = $true; foundryAccountName = 'offline-test-only'; modelDeploymentName = 'offline-deployment'; modelVersion = 'offline-version'; modelRateCardVersion = 'synthetic-test-rate-card'; modelProcessingRegion = 'offline-test'; modelInputUsdPerMillion = '0'; modelOutputUsdPerMillion = '1' }; Pass = $false },
    @{ Name = 'model creation missing account'; Changes = @{ createModelDeployment = $true }; Pass = $false },
    @{ Name = 'model creation missing model fields'; Changes = @{ createModelDeployment = $true; foundryAccountName = 'offline-test-only' }; Pass = $false },
    @{ Name = 'processing without source rights'; Changes = @{ secModelProcessingAllowed = $true }; Pass = $false },
    @{ Name = 'SEC missing identified contact'; Changes = @{ secRightsAuthorized = $true }; Pass = $false },
    @{ Name = 'Twelve Data missing secret'; Changes = @{ twelveDataRightsAuthorized = $true }; Pass = $false },
    @{ Name = 'synthetic candidate'; Changes = @{ candidateCatalog = @(@{ Id = 'SAMPLE:XNAS:USD'; Symbol = 'SAMPLE'; Exchange = 'XNAS'; Currency = 'USD'; Kind = 'Stock'; Sector = 'Synthetic Technology' }) }; Pass = $false },
    @{ Name = 'unsupported provider endpoint'; Changes = @{ twelveDataEntitlements = @('statistics') }; Pass = $false },
    @{ Name = 'wrong vault host'; Changes = @{ webClientSecretKeyVaultUri = 'https://localhost/secrets/auth' }; Pass = $false },
    @{ Name = 'workload without protected auth'; Changes = @{ deployWorkloads = $true }; Pass = $false },
    @{ Name = 'tag instead of image digest'; Changes = @{ deployWorkloads = $true; webClientSecret = 'offline-validator-not-a-credential'; webImage = 'test.azurecr.io/pii/web:latest'; workerImage = 'test.azurecr.io/pii/worker:latest' }; Pass = $false },
    @{ Name = 'registry role missing external identity'; Changes = @{ grantRegistryPush = $true }; Pass = $false },
    @{ Name = 'nonpositive monetary cap'; Changes = @{ perRunUsd = '-1' }; Pass = $false }
)
$source = @{
    twelveDataRightsAuthorized = $true; twelveDataApiKey = 'synthetic-offline-not-a-credential'; twelveDataEvidenceRetentionAuthorized = $true
    twelveDataRequestCreditsAuthorized = $true; twelveDataRateCardVersion = 'synthetic-offline-not-vendor-pricing'
    twelveDataMaximumCreditsPerRequest = 5; twelveDataDailyMaximumCredits = 20; twelveDataEndpointWeights = @{ eod = 5 }; twelveDataEntitlements = @('eod')
}
$missingDaily = @{} + $source
$missingDaily.Remove('twelveDataDailyMaximumCredits')
$cases += @(
    @{ Name = 'reviewed synthetic credit configuration'; Changes = $source; Pass = $true },
    @{ Name = 'missing daily quota'; Changes = $missingDaily; Pass = $false },
    @{ Name = 'SEC separately approved 8MiB'; Changes = @{ secRightsAuthorized = $true; secUserAgent = 'Offline contact@example.test'; secCompanyFactsLargerResponseAuthorized = $true; secCompanyFactsMaximumBytes = 8388608 }; Pass = $true },
    @{ Name = 'SEC larger bound without permission'; Changes = @{ secCompanyFactsMaximumBytes = 2097152 }; Pass = $false },
    @{ Name = 'SEC oversized bound'; Changes = @{ secRightsAuthorized = $true; secUserAgent = 'Offline contact@example.test'; secCompanyFactsLargerResponseAuthorized = $true; secCompanyFactsMaximumBytes = 8388609 }; Pass = $false },
    @{ Name = 'SEC zero bound'; Changes = @{ secCompanyFactsMaximumBytes = 0 }; Pass = $false },
    @{ Name = 'retention without rights'; Changes = @{ twelveDataEvidenceRetentionAuthorized = $true }; Pass = $false },
    @{ Name = 'credits without rights'; Changes = @{ twelveDataRequestCreditsAuthorized = $true }; Pass = $false }
)
foreach ($variation in @(
    @{ twelveDataDailyMaximumCredits = 0 }, @{ twelveDataDailyMaximumCredits = -1 }, @{ twelveDataDailyMaximumCredits = '20' },
    @{ twelveDataDailyMaximumCredits = 1.5 }, @{ twelveDataRateCardVersion = '' }, @{ twelveDataRequestCreditsAuthorized = $false },
    @{ twelveDataEvidenceRetentionAuthorized = $false }, @{ twelveDataEndpointWeights = @{} }, @{ twelveDataEndpointWeights = @{ eod = 0 } },
    @{ twelveDataEndpointWeights = @{ eod = -1 } }, @{ twelveDataEndpointWeights = @{ eod = 6 } }, @{ twelveDataEndpointWeights = @{ eod = '5' } },
    @{ twelveDataEndpointWeights = @{ statistics = 1 } }, @{ twelveDataEntitlements = @('quote') }, @{ twelveDataEntitlements = @() },
    @{ twelveDataMaximumCreditsPerRequest = 0 }
)) {
    $changed = @{} + $source
    foreach ($key in $variation.Keys) { $changed[$key] = $variation[$key] }
    $cases += @{ Name = 'invalid source credit ' + ($variation.Keys -join ','); Changes = $changed; Pass = $false }
}
$checked = 0
try {
    foreach ($case in $cases) {
        $values = @{} + $baseline
        foreach ($key in $case.Changes.Keys) { $values[$key] = $case.Changes[$key] }
        $parameters = @{}
        foreach ($key in $values.Keys) { $parameters[$key] = @{ value = $values[$key] } }
        @{ parameters = $parameters } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
        $passed = $true
        try { & (Join-Path $PSScriptRoot 'Test-DeploymentParameters.ps1') -Path $path | Out-Null }
        catch { $passed = $false }
        if ($passed -ne $case.Pass) { throw "Unexpected parameter gate result: $($case.Name)" }
        $checked++
    }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Test-DeploymentParameters.ps1') -Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'parameters.example.json') | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Unconfigured example must not be deployable.' }
    $checked++
    Write-Output "Passed $checked offline parameter gate checks, including rejection of the unconfigured example. Random test IDs are not Azure identities; no network/resource actions."
}
finally { if (Test-Path $path) { Remove-Item -LiteralPath $path -Force } }