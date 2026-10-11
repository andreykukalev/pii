[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$infra = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pii-modular-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
$script:checks = 0
function Assert-Stage([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Modular contract failed: $Description" }
    $script:checks++
}
function Get-StageResources($Template) {
    foreach ($resource in $Template.resources) {
        $resource
        if ($resource.type -eq 'Microsoft.Resources/deployments') { Get-StageResources $resource.properties.template }
    }
}
try {
    $templates = @{}
    foreach ($file in Get-ChildItem $infra -Filter '*.bicep' -Recurse) {
        $output = Join-Path $temporary ($file.BaseName + '-' + [guid]::NewGuid().ToString('N') + '.json')
        $diagnostics = @(& az bicep build --file $file.FullName --outfile $output 2>&1)
        if ($LASTEXITCODE -ne 0 -or $diagnostics.Count -gt 0) { throw "Compilation/lint failed: $($file.FullName): $($diagnostics -join [Environment]::NewLine)" }
        if ($file.Directory.Name -eq 'stages') { $templates[$file.BaseName] = Get-Content $output -Raw | ConvertFrom-Json }
        if ($file.Name -eq 'runtime-registry-access.bicep') { $registryAccess = Get-Content $output -Raw | ConvertFrom-Json }
        $script:checks++
    }
    Assert-Stage ($templates.foundation.resources.Count -eq 1 -and $templates.foundation.resources[0].type -eq 'Microsoft.Resources/resourceGroups') 'foundation owns RG only'
    foreach ($stage in @('foundation','foundry','storage','data','monitoring')) {
        Assert-Stage (@(Get-StageResources $templates[$stage] | Where-Object { $_.type -match 'Microsoft.App/|Microsoft.ContainerRegistry/|Microsoft.ManagedIdentity/' }).Count -eq 0) "$stage contains no compute"
    }
    $foundry = $templates.foundry
    $account = @($foundry.resources | Where-Object { $_.type -eq 'Microsoft.CognitiveServices/accounts' })[0]
    Assert-Stage ($account.apiVersion -ceq '2025-06-01' -and $account.kind -ceq 'AIServices' -and $account.properties.allowProjectManagement) 'stable modern project-enabled account'
    Assert-Stage ($foundry.parameters.createModelDeployment.defaultValue -eq $false) 'model purchases default off'
    Assert-Stage (($foundry.parameters.deploymentSku.allowedValues -join ',') -ceq 'Standard,GlobalStandard,DataZoneStandard') 'no PTU SKU'
    Assert-Stage ($foundry.outputs.openAiRootEndpoint.value.Contains('OpenAI Language Model Instance')) 'resource SDK endpoint, not project endpoint'
    Assert-Stage ($foundry.outputs.projectId.value.Contains('/projects/')) 'project identity exported'
    $storage = $templates.storage
    $account = @($storage.resources | Where-Object { $_.type -eq 'Microsoft.Storage/storageAccounts' })[0]
    Assert-Stage ($storage.parameters.sku.defaultValue -eq 'Standard_LRS' -and $storage.parameters.accessTier.defaultValue -eq 'Hot') 'cost-conscious compatible storage defaults'
    Assert-Stage (-not $account.properties.allowBlobPublicAccess -and $account.properties.supportsHttpsTrafficOnly) 'private HTTPS Blob'
    Assert-Stage (-not $storage.parameters.developmentSharedKeyOptIn.defaultValue) 'shared key off'
    Assert-Stage ($account.properties.encryption.keySource -eq 'Microsoft.Storage') 'storage service encryption'
    $data = $templates.data
    Assert-Stage ($data.parameters.computeModel.defaultValue -eq 'Serverless' -and $data.parameters.maximumVcores.defaultValue -eq 1) 'SQL native serverless default'
    Assert-Stage ($data.parameters.sqlAdministratorPassword.type -eq 'secureString' -and -not $data.parameters.developmentPasswordOptIn.defaultValue) 'secure opt-in SQL password'
    Assert-Stage (-not $data.parameters.allowAzureServices.defaultValue) 'no broad SQL firewall by default'
    $computeResources = @(Get-StageResources $templates.compute)
    Assert-Stage (@($computeResources | Where-Object { $_.type -in @('Microsoft.App/containerApps','Microsoft.App/jobs') }).Count -eq 0) 'compute never deploys workloads'
    Assert-Stage ($templates.compute.parameters.registrySku.defaultValue -ceq 'Basic' -and -not $templates.compute.parameters.monitoringEnabled.defaultValue) 'no default premium registry or logs'
    $pullGrant = @($registryAccess.resources | Where-Object { $_.type -eq 'Microsoft.Authorization/roleAssignments' })[0]
    Assert-Stage ($pullGrant.name.StartsWith('[guid(') -and $pullGrant.name.Contains("resourceId('Microsoft.ContainerRegistry/registries', parameters('registryName'))") -and $pullGrant.name.Contains("parameters('principalIds')[copyIndex()]") -and $pullGrant.name.Contains("variables('role')")) 'registry role GUID uses scope, current principal ID and role'
    Assert-Stage ($registryAccess.parameters.PSObject.Properties.Name -notcontains 'identityIds') 'registry module has no identity resource ID parameter'
    $pullModule = @($templates.compute.resources | Where-Object { $_.type -eq 'Microsoft.Resources/deployments' -and $_.name -match 'runtime-registry-access' })[0]
    Assert-Stage ($pullModule.properties.parameters.PSObject.Properties.Name -notcontains 'identityIds' -and ($pullModule.properties.parameters.principalIds | ConvertTo-Json -Depth 20).Contains('principalId')) 'compute passes only current registry principal IDs'
    Assert-Stage (-not $templates.monitoring.parameters.enabled.defaultValue) 'monitoring optional off'
    $app = $templates.application
    Assert-Stage (@($app.resources | Where-Object { $_.type -notin @('Microsoft.App/containerApps','Microsoft.App/jobs') }).Count -eq 0) 'application binds infrastructure without owning it'
    Assert-Stage (-not $app.parameters.deployWorkloads.defaultValue -and -not $app.parameters.scheduleEnabled.defaultValue -and -not $app.parameters.telemetryEnabled.defaultValue) 'activation and exporters off'
    $web = @($app.resources | Where-Object { $_.type -eq 'Microsoft.App/containerApps' })[0]
    $worker = @($app.resources | Where-Object { $_.type -eq 'Microsoft.App/jobs' })[0]
    $webJson = $web | ConvertTo-Json -Depth 100
    $workerJson = $worker | ConvertTo-Json -Depth 100
    Assert-Stage ($webJson -notmatch 'Pii__Model__|Pii__Sources__|PII_TWELVEDATA_API_KEY|PII_AZURE_OPENAI_API_KEY') 'worker-only inference and source secrets'
    Assert-Stage ($workerJson.Contains('2024-10-21') -and $workerJson.Contains('DefaultAzureCredential')) 'current SDK API/auth contract'
    Assert-Stage ($worker.properties.configuration.replicaRetryLimit -eq 0 -and $worker.properties.template.containers[0].args[0] -eq '--daily') 'bounded finite worker'
    Assert-Stage (-not $web.properties.configuration.ingress.allowInsecure -and $web.properties.configuration.ingress.targetPort -eq 8080) 'HTTPS 8080 auth host'
    foreach ($hostResource in @($web,$worker)) {
        $json = $hostResource | ConvertTo-Json -Depth 100
        foreach ($key in @('AZURE_CLIENT_ID','Pii__Sql__ConnectionString','telemetryEnvironment')) { Assert-Stage ($json.Contains($key)) "host contract $key" }
    }
    Assert-Stage (($app.variables | ConvertTo-Json -Depth 100).Contains('APPLICATIONINSIGHTS_CONNECTION_STRING')) 'shared opt-in telemetry secret reference'
    $appSource = Get-Content (Join-Path $infra 'stages/application.bicep') -Raw
    foreach ($key in Get-Content (Join-Path $PSScriptRoot 'fixtures/application-environment-keys.txt')) {
        Assert-Stage ($appSource.Contains($key)) "required environment key $key"
    }
    foreach ($stage in $templates.Keys) {
        $json = $templates[$stage] | ConvertTo-Json -Depth 100
        Assert-Stage ($json -notmatch 'utcNow\(|newGuid\(|Microsoft.DocumentDB|Microsoft.Search|queueServices|ProvisionedManaged') "$stage stable names and no excluded services"
        if ($templates[$stage].PSObject.Properties.Name -contains 'outputs') {
            foreach ($output in $templates[$stage].outputs.PSObject.Properties) {
                Assert-Stage ($output.Name -notmatch 'password|secret|connectionString|sharedKey|apiKey|token') "$stage no secret output"
            }
        }
    }
    $manifest = Get-Content (Join-Path $PSScriptRoot 'fixtures/manifest.json') -Raw | ConvertFrom-Json
    Assert-Stage (-not $manifest.stages.compute.enabled -and -not $manifest.stages.application.enabled -and -not $manifest.stages.monitoring.enabled) 'example no default compute/application/monitoring'
    Write-Output "Passed $script:checks local Bicep/module and ARM contract checks. No authenticated Azure calls."
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }