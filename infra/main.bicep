targetScope = 'resourceGroup'

@minLength(3)
@maxLength(16)
param prefix string = 'pii'
@description('Explicitly selected, budget-qualified Azure location; no region or entitlement assumed.')
param location string
param tags object = { application: 'pii', environment: 'production' }
param deployWorkloads bool = false
@description('Immutable private ACR web image reference, including @sha256 digest, required when workloads are enabled.')
param webImage string = ''
param workerImage string = ''
param tenantId string
param allowedObjectId string
param webClientId string
@secure()
param webClientSecret string = ''
@description('Optional existing Key Vault secret URI. Web MI needs secret GET permission separately; no vault is created.')
@secure()
param webClientSecretKeyVaultUri string = ''
param sqlAdminObjectId string
param sqlAdminName string
param sqlAllowAzureServices bool = false
param sqlFirewallRules array = []
param databaseName string = 'pii'
@minValue(1)
@maxValue(4)
param sqlMaximumVcores int = 1
@allowed([60, 120, 240, 480, 720, 1440])
param sqlAutoPauseDelayMinutes int = 60
@minValue(1)
@maxValue(32)
param sqlMaximumStorageGb int = 2
@minValue(30)
@maxValue(90)
param logRetentionDays int = 30
@allowed(['0.1', '0.25', '0.5', '1'])
param logDailyQuotaGb string = '0.1'
@description('Existing external federated deploy principal, never a web or worker identity.')
param deployPrincipalId string = ''
param grantRegistryPush bool = false
param liveAuthorized bool = false
param modelProcessingAuthorized bool = false
param modelSupportsStrictSchemaAndTools bool = false
param foundryAccountName string = ''
@description('Same subscription; existing account can be in a different existing resource group.')
param foundryResourceGroup string = resourceGroup().name
param createFoundryAccount bool = false
param createModelDeployment bool = false
param modelDeploymentName string = ''
param modelName string = ''
param modelVersion string = ''
param modelLocation string = location
param modelProcessingRegion string = ''
@allowed(['Standard', 'GlobalStandard', 'DataZoneStandard'])
param modelDeploymentSku string = 'Standard'
@minValue(1)
param modelDeploymentCapacity int = 1
param modelRateCardVersion string = ''
@description('Approved positive decimal USD per million input tokens, not a sample price.')
param modelInputUsdPerMillion string = ''
param modelOutputUsdPerMillion string = ''
param secRightsAuthorized bool = false
param secModelProcessingAllowed bool = false
@minValue(1)
@maxValue(8388608)
param secCompanyFactsMaximumBytes int = 1048576
param secCompanyFactsLargerResponseAuthorized bool = false
@secure()
param secUserAgent string = ''
param ecbRightsAuthorized bool = false
param ecbModelProcessingAllowed bool = false
param twelveDataRightsAuthorized bool = false
param twelveDataModelProcessingAllowed bool = false
@description('Explicit permission for indefinite retained evidence/titles/derived history. No TTL/automatic expiry is implemented.')
param twelveDataEvidenceRetentionAuthorized bool = false
param twelveDataRequestCreditsAuthorized bool = false
param twelveDataRateCardVersion string = ''
@minValue(0)
param twelveDataMaximumCreditsPerRequest int = 0
@description('Reviewed positive UTC daily credit quota when authorized; zero denies. Native credits, not USD.')
@minValue(0)
param twelveDataDailyMaximumCredits int = 0
@description('Reviewed positive integer credits for each entitled fixed endpoint; no inferred weights.')
param twelveDataEndpointWeights object = {}
@secure()
param twelveDataApiKey string = ''
@secure()
param twelveDataApiKeyKeyVaultUri string = ''
param twelveDataEntitlements array = []
param exchangeTimeZones object = {}
@description('Real reviewed catalog, at most 32 canonical instruments. No synthetic defaults.')
@maxLength(32)
param candidateCatalog array = []
param excludedInstrumentIds array = []
@minValue(1)
param scheduleVersion int = 1
@description('Finite batch of one bounds one execution to one run plus startup; source default of three is overridden.')
@allowed([1])
param workerMaximumBatch int = 1
param maximumDecisions int = 12
param maximumResearchTasks int = 5
param maximumCriticPasses int = 2
param maximumTools int = 32
param maximumModelSubmissions int = 24
@minValue(1)
@maxValue(10)
param maximumRunMinutes int = 10
param maximumOutputTokens int = 2500
param maximumInputCharacters int = 40000
param maximumRunTokens int = 350000
param maximumCallTokens int = 12000
param perRunUsd string = '1'
param monthlyAiUsd string = '20'
param maximumCallUsd string = '0.15'

var suffix = uniqueString(resourceGroup().id, prefix)
var webName = '${prefix}-web'
var workerName = '${prefix}-worker'
var bindFoundry = !empty(foundryAccountName)
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-web-mi'
  location: location
  tags: tags
}
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-worker-mi'
  location: location
  tags: tags
}
module storage './modules/storage.bicep' = {
  name: '${prefix}-storage'
  params: {
    name: 'st${suffix}'
    location: location
    tags: tags
    webPrincipalId: webIdentity.properties.principalId
    workerPrincipalId: workerIdentity.properties.principalId
  }
}
module sql './modules/sql.bicep' = {
  name: '${prefix}-sql'
  params: {
    name: '${prefix}-sql-${suffix}'
    location: location
    databaseName: databaseName
    tags: tags
    tenantId: tenantId
    adminObjectId: sqlAdminObjectId
    adminName: sqlAdminName
    allowAzureServices: sqlAllowAzureServices
    firewallRules: sqlFirewallRules
    maximumVcores: sqlMaximumVcores
    autoPauseDelayMinutes: sqlAutoPauseDelayMinutes
    maximumStorageGb: sqlMaximumStorageGb
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'acr${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, publicNetworkAccess: 'Enabled' }
}
var pullRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource pullRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for host in ['web', 'worker']: {
  name: guid(registry.id, host == 'web' ? webIdentity.id : workerIdentity.id, pullRole)
  scope: registry
  properties: { roleDefinitionId: pullRole, principalId: host == 'web' ? webIdentity.properties.principalId : workerIdentity.properties.principalId, principalType: 'ServicePrincipal' }
}]
resource pushRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantRegistryPush) {
  name: guid(registry.id, deployPrincipalId, '8311e382-0749-4cb8-b61a-304f252e45ec')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8311e382-0749-4cb8-b61a-304f252e45ec')
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
    workspaceCapping: { dailyQuotaGb: json(logDailyQuotaGb) }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-environment'
  location: location
  tags: tags
  properties: {
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}
module foundry './modules/foundry.bicep' = if (bindFoundry) {
  name: '${prefix}-foundry-binding'
  scope: resourceGroup(foundryResourceGroup)
  params: {
    accountName: foundryAccountName
    location: modelLocation
    tags: tags
    workerPrincipalId: workerIdentity.properties.principalId
    createAccount: createFoundryAccount
    createDeployment: createModelDeployment
    deploymentName: modelDeploymentName
    modelName: modelName
    modelVersion: modelVersion
    deploymentSku: modelDeploymentSku
    deploymentCapacity: modelDeploymentCapacity
  }
}
var sqlRoot = 'Server=tcp:${sql.outputs.serverFqdn},1433;Database=${sql.outputs.databaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;'
var commonEnvironment = [
  { name: 'Pii__Mode', value: 'Live' }
  { name: 'Pii__AllowDevelopmentIdentity', value: 'false' }
  { name: 'Pii__Blob__ServiceUri', value: storage.outputs.serviceUri }
  { name: 'Pii__Blob__Container', value: storage.outputs.documentsName }
  { name: 'Logging__LogLevel__Default', value: 'Warning' }
  { name: 'Logging__LogLevel__Microsoft', value: 'Warning' }
]
var catalogEnvironmentGroups = [for (instrument, index) in candidateCatalog: [
  { name: 'Pii__CandidateCatalog__${index}__Id', value: instrument.Id }
  { name: 'Pii__CandidateCatalog__${index}__Symbol', value: instrument.Symbol }
  { name: 'Pii__CandidateCatalog__${index}__Exchange', value: instrument.Exchange }
  { name: 'Pii__CandidateCatalog__${index}__Currency', value: instrument.Currency }
  { name: 'Pii__CandidateCatalog__${index}__Kind', value: instrument.Kind }
  { name: 'Pii__CandidateCatalog__${index}__Sector', value: instrument.Sector }
  { name: 'Pii__CandidateCatalog__${index}__Cik', value: instrument.?Cik ?? '' }
]]
var catalogEnvironment = flatten(catalogEnvironmentGroups)
var exclusionsEnvironment = [for (instrumentId, index) in excludedInstrumentIds: { name: 'Pii__ExcludedInstrumentIds__${index}', value: instrumentId }]
var entitlementsEnvironment = [for (entitlement, index) in twelveDataEntitlements: { name: 'Pii__Sources__TwelveData__Entitlements__${index}', value: entitlement }]
var creditWeightsEnvironment = [for endpoint in items(twelveDataEndpointWeights): { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__EndpointWeights__${endpoint.key}', value: string(endpoint.value) }]
var exchangeEnvironment = [for exchange in items(exchangeTimeZones): { name: 'Pii__Sources__ExchangeTimeZones__${exchange.key}', value: exchange.value }]
var limitsEnvironment = [
  { name: 'Pii__Limits__MaximumDecisions', value: string(maximumDecisions) }
  { name: 'Pii__Limits__MaximumResearchTasks', value: string(maximumResearchTasks) }
  { name: 'Pii__Limits__MaximumCriticPasses', value: string(maximumCriticPasses) }
  { name: 'Pii__Limits__MaximumTools', value: string(maximumTools) }
  { name: 'Pii__Limits__MaximumModelSubmissions', value: string(maximumModelSubmissions) }
  { name: 'Pii__Limits__MaximumRunMinutes', value: string(maximumRunMinutes) }
  { name: 'Pii__Limits__MaximumOutputTokens', value: string(maximumOutputTokens) }
  { name: 'Pii__Limits__MaximumInputCharacters', value: string(maximumInputCharacters) }
  { name: 'Pii__Limits__MaximumRunTokens', value: string(maximumRunTokens) }
  { name: 'Pii__Limits__MaximumCallTokens', value: string(maximumCallTokens) }
  { name: 'Pii__Limits__PerRunUsd', value: perRunUsd }
  { name: 'Pii__Limits__MonthlyAiUsd', value: monthlyAiUsd }
  { name: 'Pii__Limits__MaximumCallUsd', value: maximumCallUsd }
]
var hasTwelveSecret = !empty(twelveDataApiKey) || !empty(twelveDataApiKeyKeyVaultUri)
resource web 'Microsoft.App/containerApps@2024-03-01' = if (deployWorkloads) {
  name: webName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${webIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
      registries: [{ server: registry.properties.loginServer, identity: webIdentity.id }]
      secrets: [empty(webClientSecretKeyVaultUri)
        ? { name: 'entra-client-secret', value: webClientSecret }
        : { name: 'entra-client-secret', keyVaultUrl: webClientSecretKeyVaultUri, identity: webIdentity.id }]
    }
    template: {
      containers: [{
        name: 'web'
        image: webImage
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: concat(commonEnvironment, catalogEnvironment, exclusionsEnvironment, limitsEnvironment, [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'ASPNETCORE_HTTPS_PORT', value: '443' }
          { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
          { name: 'AZURE_CLIENT_ID', value: webIdentity.properties.clientId }
          { name: 'Pii__Sql__ConnectionString', value: '${sqlRoot}User Id=${webIdentity.properties.clientId};' }
          { name: 'AllowedHosts', value: '${webName}.${environment.properties.defaultDomain}' }
          { name: 'AzureAd__Instance', value: az.environment().authentication.loginEndpoint }
          { name: 'AzureAd__TenantId', value: tenantId }
          { name: 'AzureAd__ClientId', value: webClientId }
          { name: 'AzureAd__CallbackPath', value: '/signin-oidc' }
          { name: 'AzureAd__ClientSecret', secretRef: 'entra-client-secret' }
          { name: 'Pii__Auth__AllowedTenantId', value: tenantId }
          { name: 'Pii__Auth__AllowedObjectId', value: allowedObjectId }
          { name: 'Pii__DataProtection__BlobUri', value: storage.outputs.keyRingUri }
        ])
        probes: [
          { type: 'Startup', tcpSocket: { port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10, failureThreshold: 30 }
          { type: 'Readiness', tcpSocket: { port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
          { type: 'Liveness', tcpSocket: { port: 8080 }, periodSeconds: 30, failureThreshold: 3 }
        ]
      }]
      scale: { minReplicas: 0, maxReplicas: 1, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }] }
    }
  }
  dependsOn: [pullRoles]
}
resource worker 'Microsoft.App/jobs@2024-03-01' = if (deployWorkloads) {
  name: workerName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 900
      replicaRetryLimit: 0
      scheduleTriggerConfig: { cronExpression: '* * * * *', parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registry.properties.loginServer, identity: workerIdentity.id }]
      secrets: concat(empty(secUserAgent) ? [] : [
        { name: 'sec-user-agent', value: secUserAgent }
      ], hasTwelveSecret ? [empty(twelveDataApiKeyKeyVaultUri)
        ? { name: 'twelve-data-api-key', value: twelveDataApiKey }
        : { name: 'twelve-data-api-key', keyVaultUrl: twelveDataApiKeyKeyVaultUri, identity: workerIdentity.id }] : [])
    }
    template: {
      containers: [{
        name: 'worker'
        image: workerImage
        args: ['--daily']
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: concat(commonEnvironment, catalogEnvironment, exclusionsEnvironment, limitsEnvironment, [
          { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          { name: 'AZURE_CLIENT_ID', value: workerIdentity.properties.clientId }
          { name: 'Pii__Sql__ConnectionString', value: '${sqlRoot}User Id=${workerIdentity.properties.clientId};' }
          { name: 'Pii__Worker__MaximumBatch', value: string(workerMaximumBatch) }
          { name: 'Pii__ScheduleVersion', value: string(scheduleVersion) }
          { name: 'Pii__LiveAuthorized', value: string(liveAuthorized) }
          { name: 'Pii__Model__ProcessingAuthorized', value: string(modelProcessingAuthorized) }
          { name: 'Pii__Model__Endpoint', value: bindFoundry ? foundry!.outputs.endpoint : '' }
          { name: 'Pii__Model__Deployment', value: modelDeploymentName }
          { name: 'Pii__Model__ModelVersion', value: modelVersion }
          { name: 'Pii__Model__ApiVersion', value: '2024-10-21' }
          { name: 'Pii__Model__Authentication', value: 'DefaultAzureCredential' }
          { name: 'Pii__Model__SupportsStrictSchemaAndTools', value: string(modelSupportsStrictSchemaAndTools) }
          { name: 'Pii__Model__ProcessingRegion', value: modelProcessingRegion }
          { name: 'Pii__Model__RateCardVersion', value: modelRateCardVersion }
          { name: 'Pii__Model__InputUsdPerMillion', value: empty(modelInputUsdPerMillion) ? '0' : modelInputUsdPerMillion }
          { name: 'Pii__Model__OutputUsdPerMillion', value: empty(modelOutputUsdPerMillion) ? '0' : modelOutputUsdPerMillion }
          { name: 'Pii__Sources__Sec__RightsAuthorized', value: string(secRightsAuthorized) }
          { name: 'Pii__Sources__Sec__ModelProcessingAllowed', value: string(secModelProcessingAllowed) }
          { name: 'Pii__Sources__Sec__CompanyFactsMaximumBytes', value: string(secCompanyFactsMaximumBytes) }
          { name: 'Pii__Sources__Sec__CompanyFactsLargerResponseAuthorized', value: string(secCompanyFactsLargerResponseAuthorized) }
          empty(secUserAgent)
            ? { name: 'Pii__Sources__Sec__UserAgent', value: '' }
            : { name: 'Pii__Sources__Sec__UserAgent', secretRef: 'sec-user-agent' }
          { name: 'Pii__Sources__Ecb__RightsAuthorized', value: string(ecbRightsAuthorized) }
          { name: 'Pii__Sources__Ecb__ModelProcessingAllowed', value: string(ecbModelProcessingAllowed) }
          { name: 'Pii__Sources__TwelveData__RightsAuthorized', value: string(twelveDataRightsAuthorized) }
          { name: 'Pii__Sources__TwelveData__ModelProcessingAllowed', value: string(twelveDataModelProcessingAllowed) }
          { name: 'Pii__Sources__TwelveData__EvidenceRetentionAuthorized', value: string(twelveDataEvidenceRetentionAuthorized) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__Authorized', value: string(twelveDataRequestCreditsAuthorized) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__RateCardVersion', value: twelveDataRateCardVersion }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__MaximumCreditsPerRequest', value: string(twelveDataMaximumCreditsPerRequest) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__DailyMaximumCredits', value: string(twelveDataDailyMaximumCredits) }
        ], entitlementsEnvironment, creditWeightsEnvironment, exchangeEnvironment,
        hasTwelveSecret ? [{ name: 'PII_TWELVEDATA_API_KEY', secretRef: 'twelve-data-api-key' }] : [])
      }]
    }
  }
  dependsOn: [pullRoles]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output webIdentityName string = webIdentity.name
output workerIdentityName string = workerIdentity.name
output webIdentityPrincipalId string = webIdentity.properties.principalId
output workerIdentityPrincipalId string = workerIdentity.properties.principalId
output webManagedIdentityClientId string = webIdentity.properties.clientId
output workerManagedIdentityClientId string = workerIdentity.properties.clientId
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output keyRingUri string = storage.outputs.keyRingUri
output webHostName string = '${webName}.${environment.properties.defaultDomain}'
output workerJobName string = workerName