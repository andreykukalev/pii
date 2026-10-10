[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pii-bicep-' + [guid]::NewGuid().ToString('N') + '.json')
$script:checks = 0
function Assert-Contract([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Infrastructure contract failed: $Description" }
    $script:checks++
}
function Find-Resource($Template, [string]$Type) {
    @($Template.resources | Where-Object { $_.type -eq $Type })
}
try {
    $diagnostics = @(& az bicep build --file (Join-Path $root 'infra/main.bicep') --outfile $temporary 2>&1)
    if ($LASTEXITCODE -ne 0 -or $diagnostics.Count -gt 0) { throw "Bicep compilation/lint must be clean: $($diagnostics -join [Environment]::NewLine)" }
    $template = Get-Content $temporary -Raw | ConvertFrom-Json
    foreach ($parameter in @('deployWorkloads', 'liveAuthorized', 'createFoundryAccount', 'createModelDeployment', 'modelProcessingAuthorized', 'modelSupportsStrictSchemaAndTools', 'secRightsAuthorized', 'secCompanyFactsLargerResponseAuthorized', 'ecbRightsAuthorized', 'twelveDataRightsAuthorized', 'twelveDataEvidenceRetentionAuthorized', 'twelveDataRequestCreditsAuthorized')) {
        Assert-Contract ($template.parameters.$parameter.defaultValue -eq $false) "$parameter defaults off"
    }
    Assert-Contract ($template.parameters.twelveDataDailyMaximumCredits.defaultValue -eq 0) 'daily source credits default deny, not inferred quota'
    Assert-Contract ($template.parameters.twelveDataMaximumCreditsPerRequest.defaultValue -eq 0) 'per-request source credits default deny'
    Assert-Contract ($template.parameters.twelveDataRateCardVersion.defaultValue -ceq '') 'no invented vendor rate-card reference'
    Assert-Contract (@($template.parameters.twelveDataEndpointWeights.defaultValue.PSObject.Properties).Count -eq 0) 'no inferred endpoint weights'
    Assert-Contract ($template.parameters.secCompanyFactsMaximumBytes.defaultValue -eq 1048576 -and $template.parameters.secCompanyFactsMaximumBytes.maxValue -eq 8388608) 'SEC companyfacts separate bounded response'
    foreach ($parameter in @('webClientSecret', 'webClientSecretKeyVaultUri', 'twelveDataApiKey', 'twelveDataApiKeyKeyVaultUri', 'secUserAgent')) {
        Assert-Contract ($template.parameters.$parameter.type -eq 'secureString') "$parameter is secure"
    }
    $web = (Find-Resource $template 'Microsoft.App/containerApps')[0]
    $job = (Find-Resource $template 'Microsoft.App/jobs')[0]
    Assert-Contract ($web.properties.configuration.ingress.allowInsecure -eq $false) 'HTTPS ingress'
    Assert-Contract ($web.properties.template.scale.minReplicas -eq 0) 'HTTP scales to zero'
    Assert-Contract ($web.properties.template.scale.maxReplicas -eq 1) 'HTTP bounded replicas'
    Assert-Contract ($web.identity.type -eq 'UserAssigned' -and $job.identity.type -eq 'UserAssigned') 'distinct user-assigned runtime identities'
    Assert-Contract ($job.properties.configuration.scheduleTriggerConfig.cronExpression -ceq '* * * * *') 'every-minute UTC trigger'
    Assert-Contract ($job.properties.template.containers[0].args[0] -ceq '--daily') 'daily due-key plus pending-work consumption'
    Assert-Contract ($job.properties.configuration.replicaRetryLimit -eq 0) 'platform retry does not multiply application attempts'
    Assert-Contract ($job.properties.configuration.replicaTimeout -eq 900) 'finite worker timeout'
    $webJson = $web | ConvertTo-Json -Depth 100
    $jobJson = $job | ConvertTo-Json -Depth 100
    Assert-Contract ($webJson -notmatch 'Pii__Model__|Pii__Sources__|PII_TWELVEDATA_API_KEY|PII_AZURE_OPENAI_API_KEY') 'web has no inference/provider configuration or secrets'
    foreach ($key in @('Pii__Model__ApiVersion', 'Pii__Model__SupportsStrictSchemaAndTools', 'Pii__Model__Authentication', 'Pii__Model__ProcessingAuthorized', 'Pii__Model__InputUsdPerMillion', 'Pii__Model__OutputUsdPerMillion', 'Pii__Model__RateCardVersion', 'Pii__LiveAuthorized', 'Pii__Worker__MaximumBatch', 'Pii__ScheduleVersion', 'PII_TWELVEDATA_API_KEY')) {
        Assert-Contract ($jobJson.Contains($key)) "worker binds $key"
    }
    foreach ($key in @('Pii__Sources__TwelveData__EvidenceRetentionAuthorized', 'Pii__Sources__TwelveData__RequestCreditAuthorization__Authorized', 'Pii__Sources__TwelveData__RequestCreditAuthorization__RateCardVersion', 'Pii__Sources__TwelveData__RequestCreditAuthorization__MaximumCreditsPerRequest', 'Pii__Sources__TwelveData__RequestCreditAuthorization__DailyMaximumCredits', 'Pii__Sources__Sec__CompanyFactsMaximumBytes', 'Pii__Sources__Sec__CompanyFactsLargerResponseAuthorized')) {
        Assert-Contract ($jobJson.Contains($key) -and -not $webJson.Contains($key)) "worker-only source gate $key"
    }
    Assert-Contract (($template.variables | ConvertTo-Json -Depth 100).Contains('Pii__Sources__TwelveData__RequestCreditAuthorization__EndpointWeights__')) 'reviewed endpoint-weight environment generation'
    Assert-Contract ($jobJson.Contains('creditWeightsEnvironment') -and -not $webJson.Contains('creditWeightsEnvironment')) 'weights supplied only to worker'
    foreach ($key in @('AzureAd__ClientSecret', 'AzureAd__TenantId', 'AzureAd__ClientId', 'Pii__Auth__AllowedTenantId', 'Pii__Auth__AllowedObjectId', 'Pii__DataProtection__BlobUri', 'AllowedHosts', 'AZURE_CLIENT_ID')) {
        Assert-Contract ($webJson.Contains($key)) "web binds $key"
    }
    Assert-Contract ($jobJson.Contains('2024-10-21')) 'released inference API binding'
    Assert-Contract ($jobJson -notmatch 'PII_AZURE_OPENAI_API_KEY') 'production model key forbidden'
    $modules = Find-Resource $template 'Microsoft.Resources/deployments'
    $sql = @($modules | Where-Object { $_.name -match '-sql' })[0].properties.template
    $storage = @($modules | Where-Object { $_.name -match '-storage' })[0].properties.template
    $foundry = @($modules | Where-Object { $_.name -match 'foundry-binding' })[0]
    $database = (Find-Resource $sql 'Microsoft.Sql/servers/databases')[0]
    Assert-Contract ($database.sku.name -ceq 'GP_S_Gen5') 'one SQL serverless database'
    Assert-Contract ($database.properties.minCapacity -eq '[json(''0.5'')]') 'SQL 0.5-vCore minimum'
    Assert-Contract ((Find-Resource $sql 'Microsoft.Sql/servers')[0].properties.administrators.azureADOnlyAuthentication -eq $true) 'SQL Entra-only admin'
    $account = (Find-Resource $storage 'Microsoft.Storage/storageAccounts')[0]
    Assert-Contract ($account.kind -ceq 'StorageV2' -and $account.sku.name -ceq 'Standard_LRS') 'GPv2 LRS storage'
    Assert-Contract ($account.properties.allowBlobPublicAccess -eq $false -and $account.properties.allowSharedKeyAccess -eq $false) 'no anonymous Blob or shared keys'
    $containers = Find-Resource $storage 'Microsoft.Storage/storageAccounts/blobServices/containers'
    Assert-Contract ($containers.Count -eq 2) 'documents and key ring only'
    foreach ($container in $containers) { Assert-Contract ($container.properties.publicAccess -ceq 'None') 'private container' }
    $roles = Find-Resource $storage 'Microsoft.Authorization/roleAssignments'
    Assert-Contract ($roles.Count -eq 2) 'documents loop and web-only key grant'
    Assert-Contract (($roles | ConvertTo-Json -Depth 100) -notmatch 'storageAccounts''\)\]') 'container-scoped Blob roles'
    $foundryJson = $foundry | ConvertTo-Json -Depth 100
    Assert-Contract ($null -ne $foundry.resourceGroup) 'Foundry binding supports separate resource group'
    Assert-Contract ($foundryJson.Contains('5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')) 'OpenAI User inference role'
    Assert-Contract ($foundryJson -notmatch 'hostedAgent|vectorStore|Microsoft.Search') 'no hosted agents or vector store'
    Assert-Contract (($template | ConvertTo-Json -Depth 100) -notmatch 'Microsoft.DocumentDB|Microsoft.ServiceBus|queueServices') 'no second DB or queue'
    foreach ($hostName in @('web', 'worker')) {
        $dockerfile = Get-Content (Join-Path $root "Dockerfile.$hostName") -Raw
        Assert-Contract ($dockerfile.Contains('mcr.microsoft.com/dotnet/sdk:10.0')) "$hostName SDK image"
        Assert-Contract ($dockerfile.Contains('mcr.microsoft.com/dotnet/aspnet:10.0')) "$hostName ASP.NET runtime"
        Assert-Contract ($dockerfile.Contains('SDK_VERSION=10.0.401')) "$hostName exact SDK check"
        Assert-Contract ($dockerfile.Contains('USER $APP_UID')) "$hostName non-root"
        Assert-Contract ($dockerfile.Contains('--runtime linux-x64') -and $dockerfile.Contains('ContinuousIntegrationBuild=true')) "$hostName deterministic Linux publish"
    }
    $schema = Get-Content (Join-Path $root 'infra/sql/schema-bootstrap.sql') -Raw
    $users = Get-Content (Join-Path $root 'infra/sql/runtime-users.sql') -Raw
    foreach ($table in @('WorkspaceHeads', 'PortfolioRevisions', 'ImportPreviews', 'LedgerIdentities', 'AnalysisRuns', 'BudgetMonths', 'BudgetReservations')) {
        Assert-Contract ($schema.Contains("CREATE TABLE dbo.$table (")) "schema includes $table"
        foreach ($principal in @('pii_web_runtime', 'pii_worker_runtime')) {
            Assert-Contract ($users.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.$table TO [$principal];")) "table-scoped DML: $principal / $table"
        }
    }
    foreach ($table in @('SourceCreditDays', 'SourceCreditEntries')) {
        Assert-Contract ($schema.Contains("CREATE TABLE dbo.$table (")) "native credit schema includes $table"
        Assert-Contract ($users -notmatch "OBJECT::dbo\.$table TO \[pii_web_runtime\]") "web has no $table grant"
        Assert-Contract ($users -notmatch "GRANT [^;]*DELETE[^;]*OBJECT::dbo\.$table") "no runtime credit deletion: $table"
    }
    Assert-Contract ($users.Contains('GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.SourceCreditDays TO [pii_worker_runtime];')) 'worker only needs mutable daily totals'
    Assert-Contract ($users.Contains('GRANT SELECT, INSERT ON OBJECT::dbo.SourceCreditEntries TO [pii_worker_runtime];')) 'worker entries are insert/read only'
    Assert-Contract ($schema.Contains('ConsumedCredits bigint NOT NULL') -and $schema.Contains('ReservationId uniqueidentifier NOT NULL') -and $schema.Contains('UtcDate date NOT NULL')) 'credit units/date/identity use native SQL types'
    $upgrade = Get-Content (Join-Path $root 'infra/sql/source-credit-upgrade.sql') -Raw
    Assert-Contract ($upgrade.Contains('sp_getapplock') -and $upgrade.Contains('SET XACT_ABORT ON') -and $upgrade -notmatch '\bDROP\b|TRUNCATE|DELETE FROM') 'explicit additive upgrade never resets existing budgets'
    Assert-Contract ($users -notmatch 'db_owner|db_ddladmin|GRANT CONTROL|GRANT ALTER|GRANT CREATE') 'no runtime DDL or database owner grants'
    Assert-Contract ($users.Contains('TYPE = E') -and $users.Contains('WebManagedIdentityClientId') -and $users.Contains('WorkerManagedIdentityClientId')) 'graph-free Entra contained users use verified managed identity client IDs'
    Assert-Contract ($users -notmatch 'FROM EXTERNAL PROVIDER') 'federated SQL admin bootstrap needs no SQL server directory-reader grant'
    Assert-Contract ($schema.Contains('SET XACT_ABORT ON') -and $schema.Contains('BEGIN TRANSACTION')) 'transactional schema bootstrap'
    Write-Output "Passed $script:checks offline IaC/container/schema contract checks. No Azure authentication, provisioning, SQL execution, or inference."
}
finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Force }
}