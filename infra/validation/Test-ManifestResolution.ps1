[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../scripts/Deployment.Common.ps1"
$savedPath = $env:PII_MANIFEST_PATH
$savedSubscription = $env:PII_SUBSCRIPTION_ID
$savedTenant = $env:AZURE_TENANT_ID
$script:checks = 0
function Assert-Resolution([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Manifest resolution failed: $Description" }
    $script:checks++
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pii-resolution-' + [guid]::NewGuid().ToString('N') + '.json')
function Write-ResolutionFixture($Configuration) { $Configuration | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding UTF8 }
function Assert-ResolutionRejected([scriptblock]$Operation, [string]$Expected) {
    $rejected = $false
    try { $null = & $Operation } catch { $rejected = $_.Exception.Message.Contains($Expected) }
    Assert-Resolution $rejected $Expected
}
try {
    $fixturePath = Join-Path $PSScriptRoot 'fixtures/manifest.json'
    $env:PII_MANIFEST_PATH = $fixturePath
    $env:PII_SUBSCRIPTION_ID = '55555555-5555-5555-5555-555555555555'
    $env:AZURE_TENANT_ID = '99999999-9999-9999-9999-999999999999'
    Assert-ResolutionRejected { Read-PiiManifest } 'must explicitly select an existing manifest'
    $resolved = Read-PiiManifest $fixturePath
    Assert-Resolution ($resolved.subscriptionId -eq (Get-Content $fixturePath -Raw | ConvertFrom-Json).subscriptionId) 'environment mismatch ignored'
    Assert-Resolution ($resolved.shared.auth.tenantId -eq (Get-Content $fixturePath -Raw | ConvertFrom-Json).stages.data.parameters.tenantId) 'external tenant mismatch ignored'
    Assert-Resolution ($resolved.selection.manifestPath -eq (Resolve-Path $fixturePath).Path -and @($resolved.selection.PSObject.Properties).Count -eq 1) 'manifest path is sole provenance'
    $legacyText = Get-Content $fixturePath -Raw
    $legacy = Read-PiiManifest $fixturePath
    $central = $legacy | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $central.schemaVersion = 2
    $central.PSObject.Properties.Remove('selection')
    $duplicateNames = @('tenantId','allowedObjectId','webClientId','accountName','projectName','serverName','databaseName','documentsName','keysName','storageAccountName','foundryAccountName','managedEnvironmentName','registryName','registryResourceGroup','webIdentityName','workerIdentityName','workspaceName','workspaceResourceGroup','insightsName','storageResourceGroup','foundryResourceGroup','sqlServerFqdn','blobServiceUri','keyRingUri')
    foreach ($stage in $central.stages.PSObject.Properties) {
        foreach ($name in $duplicateNames) { $stage.Value.parameters.PSObject.Properties.Remove($name) }
        $stage.Value.PSObject.Properties.Remove('resourceGroupName')
    }
    Write-ResolutionFixture $central
    $resolved = Read-PiiManifest $temporary
    foreach ($stage in $legacy.stages.PSObject.Properties) {
        $expected = ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $stage.Value.parameters) -Depth 100 -Compress
        $actual = ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $resolved.stages.($stage.Name).parameters) -Depth 100 -Compress
        Assert-Resolution ($actual -ceq $expected) "legacy/central parameter equivalence $($stage.Name)"
    }
    Assert-Resolution ((Get-Content $fixturePath -Raw) -ceq $legacyText) 'legacy file never rewritten'
    $central.shared.storage.accountName = 'stsyntheticchanged'
    Write-ResolutionFixture $central
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.stages.storage.parameters.accountName -eq 'stsyntheticchanged' -and $resolved.stages.compute.parameters.storageAccountName -eq 'stsyntheticchanged' -and $resolved.stages.application.parameters.blobServiceUri -eq 'https://stsyntheticchanged.blob.core.windows.net/') 'one storage edit propagates to all consumers'
    $central.stages.compute.parameters | Add-Member storageAccountName 'stconflicting'
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'shared.storage.accountName and stages.compute.parameters.storageAccountName'
    $central.stages.compute.parameters.PSObject.Properties.Remove('storageAccountName')
    $central.shared.storage.resourceGroupName = 'pii-storage-rg'
    Write-ResolutionFixture $central
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.stages.storage.resourceGroupName -eq 'pii-storage-rg' -and $resolved.stages.compute.parameters.storageResourceGroup -eq 'pii-storage-rg') 'same-sub cross-RG propagated'
    $central.stages.compute.parameters | Add-Member storageResourceGroup 'pii-wrong-rg'
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'shared.storage.resourceGroupName and stages.compute.parameters.storageResourceGroup'
    $central.stages.compute.parameters.PSObject.Properties.Remove('storageResourceGroup')
    $central.shared.auth | Add-Member clientSecret 'test-only'
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'Unknown shared field: shared.auth.clientSecret'
    $central.shared.auth.PSObject.Properties.Remove('clientSecret')
    $central.shared.storage.PSObject.Properties.Remove('accountName')
    Write-ResolutionFixture $central
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($null -eq $resolved.stages.storage.parameters.PSObject.Properties['accountName'] -and $null -eq $resolved.stages.compute.parameters.PSObject.Properties['storageAccountName'] -and $null -eq $resolved.stages.application.parameters.PSObject.Properties['blobServiceUri']) 'unresolved Bicep resource names never invented'
    $env:PII_SUBSCRIPTION_ID = ''
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.subscriptionId -eq $central.subscriptionId) 'manifest subscription sole truth'
    $env:PII_MANIFEST_PATH = ''
    Assert-ResolutionRejected { Read-PiiManifest } 'must explicitly select an existing manifest'
    $env:PII_MANIFEST_PATH = 'not-an-existing-file.json'
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.selection.manifestPath -eq (Resolve-Path $temporary).Path) 'explicit path ignores invalid environment selector'
    $central.subscriptionId = 'invalid'
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'Manifest subscriptionId'
    $central.PSObject.Properties.Remove('subscriptionId')
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'Manifest subscriptionId'
    $central | Add-Member subscriptionId '00000000-0000-0000-0000-000000000000'
    Write-ResolutionFixture $central
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'Manifest subscriptionId'
    Assert-Resolution ((Get-Content $fixturePath -Raw) -ceq $legacyText) 'synthetic tests never edit source fixture'
    $partial = Get-Content $fixturePath -Raw | ConvertFrom-Json
    $partial.stages.PSObject.Properties.Remove('storage')
    $partial.stages.compute.parameters | Add-Member storageResourceGroup 'pii-other-rg'
    Write-ResolutionFixture $partial
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.shared.storage.resourceGroupName -eq 'pii-other-rg' -and $resolved.shared.storage.accountName -eq 'stpiidevreplace') 'missing owner infers explicit legacy consumer name/scope'
    $partial | Add-Member shared ([pscustomobject]@{ storage = [pscustomobject]@{ resourceGroupName = 'pii-other-rg' } })
    Write-ResolutionFixture $partial
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.stages.compute.parameters.storageResourceGroup -eq 'pii-other-rg') 'shared scope reaches consumer without owner stage'
    $partial.PSObject.Properties.Remove('prefix')
    $partial.PSObject.Properties.Remove('environmentName')
    $partial.PSObject.Properties.Remove('tags')
    Write-ResolutionFixture $partial
    $resolved = Read-PiiManifest $temporary
    Assert-Resolution ($resolved.prefix -eq 'pii' -and $resolved.environmentName -eq 'dev') 'existing literal Bicep global defaults retained'
    $partial.stages.compute.parameters | Add-Member location 'westeurope'
    Write-ResolutionFixture $partial
    Assert-ResolutionRejected { Read-PiiManifest $temporary } 'location and stages.compute.parameters.location'
    Write-Output "Passed $script:checks manifest resolution checks. No Azure calls."
} finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Force }
    $env:PII_MANIFEST_PATH = $savedPath
    $env:PII_SUBSCRIPTION_ID = $savedSubscription
    $env:AZURE_TENANT_ID = $savedTenant
}