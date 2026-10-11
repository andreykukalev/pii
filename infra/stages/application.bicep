targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
@allowed(['Consumption'])
param hostingOption string = 'Consumption'
param deployWorkloads bool = false
param scheduleEnabled bool = false
param managedEnvironmentName string = '${prefix}-${environmentName}-environment'
param registryName string
param registryResourceGroup string = resourceGroup().name
param webIdentityName string = '${prefix}-${environmentName}-web-mi'
param workerIdentityName string = '${prefix}-${environmentName}-worker-mi'
param webName string = '${prefix}-${environmentName}-web'
param workerName string = '${prefix}-${environmentName}-worker'
param webImage string = ''
param workerImage string = ''
@allowed(['0.25', '0.5', '0.75', '1', '1.25', '1.5', '1.75', '2'])
param webCpu string = '0.25'
@allowed(['0.25', '0.5', '0.75', '1', '1.25', '1.5', '1.75', '2'])
param workerCpu string = '0.25'
@minValue(0)
@maxValue(2)
param webMinimumReplicas int = 0
@minValue(1)
@maxValue(5)
param webMaximumReplicas int = 1
@minValue(1)
@maxValue(100)
param concurrentRequests int = 10
param workerCron string = '* * * * *'
@minValue(900)
@maxValue(3600)
param workerTimeoutSeconds int = 900
@allowed([1])
param workerParallelism int = 1
@allowed([1])
param workerMaximumBatch int = 1
param sqlServerFqdn string
param databaseName string = 'pii'
param blobServiceUri string
param documentsName string = 'documents'
param keyRingUri string
param tenantId string
param allowedObjectId string
param webClientId string
@secure()
param webClientSecret string = ''
@secure()
param webClientSecretKeyVaultUri string = ''
param telemetryEnabled bool = false
@secure()
param applicationInsightsConnectionString string = ''
param liveAuthorized bool = false
param modelProcessingAuthorized bool = false
param modelSupportsStrictSchemaAndTools bool = false
param modelEndpoint string = ''
param modelDeploymentName string = ''
param modelVersion string = ''
param modelProcessingRegion string = ''
param modelRateCardVersion string = ''
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
param twelveDataEvidenceRetentionAuthorized bool = false
param twelveDataRequestCreditsAuthorized bool = false
param twelveDataRateCardVersion string = ''
@minValue(0)
param twelveDataMaximumCreditsPerRequest int = 0
@minValue(0)
param twelveDataDailyMaximumCredits int = 0
param twelveDataEndpointWeights object = {}
@secure()
param twelveDataApiKey string = ''
@secure()
param twelveDataApiKeyKeyVaultUri string = ''
param twelveDataEntitlements array = []
param exchangeTimeZones object = {}
@maxLength(32)
param candidateCatalog array = []
param excludedInstrumentIds array = []
@minValue(1)
param scheduleVersion int = 1
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

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = { name: managedEnvironmentName }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: registryName, scope: resourceGroup(registryResourceGroup) }
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: webIdentityName }
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: workerIdentityName }
var sqlRoot = 'Server=tcp:${sqlServerFqdn},1433;Database=${databaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;'
var memoryByCpu = { '0.25': '0.5Gi', '0.5': '1Gi', '0.75': '1.5Gi', '1': '2Gi', '1.25': '2.5Gi', '1.5': '3Gi', '1.75': '3.5Gi', '2': '4Gi' }
var commonEnvironment = [
  { name: 'Pii__Mode', value: 'Live' }
  { name: 'Pii__AllowDevelopmentIdentity', value: 'false' }
  { name: 'Pii__Blob__ServiceUri', value: blobServiceUri }
  { name: 'Pii__Blob__Container', value: documentsName }
  { name: 'Pii__Telemetry__Enabled', value: string(telemetryEnabled) }
  { name: 'Logging__LogLevel__Default', value: 'Warning' }
  { name: 'Logging__LogLevel__Microsoft', value: 'Warning' }
]
var telemetryEnvironment = telemetryEnabled ? [{ name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'application-insights' }] : []
var telemetrySecrets = telemetryEnabled ? [{ name: 'application-insights', value: applicationInsightsConnectionString }] : []
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
    workloadProfileName: hostingOption
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
      registries: [{ server: registry.properties.loginServer, identity: webIdentity.id }]
      secrets: concat(telemetrySecrets, [empty(webClientSecretKeyVaultUri)
        ? { name: 'entra-client-secret', value: webClientSecret }
        : { name: 'entra-client-secret', keyVaultUrl: webClientSecretKeyVaultUri, identity: webIdentity.id }])
    }
    template: {
      containers: [{
        name: 'web'
        image: webImage
        resources: { cpu: json(webCpu), memory: memoryByCpu[webCpu] }
        env: concat(commonEnvironment, catalogEnvironment, exclusionsEnvironment, limitsEnvironment, telemetryEnvironment, [
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
          { name: 'Pii__DataProtection__BlobUri', value: keyRingUri }
        ])
        probes: [
          { type: 'Startup', tcpSocket: { port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10, failureThreshold: 30 }
          { type: 'Readiness', tcpSocket: { port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
          { type: 'Liveness', tcpSocket: { port: 8080 }, periodSeconds: 30, failureThreshold: 3 }
        ]
      }]
      scale: { minReplicas: webMinimumReplicas, maxReplicas: webMaximumReplicas, rules: [{ name: 'http', http: { metadata: { concurrentRequests: string(concurrentRequests) } } }] }
    }
  }
}
resource worker 'Microsoft.App/jobs@2024-03-01' = if (deployWorkloads) {
  name: workerName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: environment.id
    workloadProfileName: hostingOption
    configuration: {
      triggerType: scheduleEnabled ? 'Schedule' : 'Manual'
      replicaTimeout: workerTimeoutSeconds
      replicaRetryLimit: 0
      scheduleTriggerConfig: scheduleEnabled ? { cronExpression: workerCron, parallelism: workerParallelism, replicaCompletionCount: 1 } : null
      manualTriggerConfig: scheduleEnabled ? null : { parallelism: workerParallelism, replicaCompletionCount: 1 }
      registries: [{ server: registry.properties.loginServer, identity: workerIdentity.id }]
      secrets: concat(telemetrySecrets, empty(secUserAgent) ? [] : [{ name: 'sec-user-agent', value: secUserAgent }], hasTwelveSecret ? [empty(twelveDataApiKeyKeyVaultUri)
        ? { name: 'twelve-data-api-key', value: twelveDataApiKey }
        : { name: 'twelve-data-api-key', keyVaultUrl: twelveDataApiKeyKeyVaultUri, identity: workerIdentity.id }] : [])
    }
    template: {
      containers: [{
        name: 'worker'
        image: workerImage
        args: ['--daily']
        resources: { cpu: json(workerCpu), memory: memoryByCpu[workerCpu] }
        env: concat(commonEnvironment, catalogEnvironment, exclusionsEnvironment, limitsEnvironment, telemetryEnvironment, [
          { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          { name: 'AZURE_CLIENT_ID', value: workerIdentity.properties.clientId }
          { name: 'Pii__Sql__ConnectionString', value: '${sqlRoot}User Id=${workerIdentity.properties.clientId};' }
          { name: 'Pii__Worker__MaximumBatch', value: string(workerMaximumBatch) }
          { name: 'Pii__ScheduleVersion', value: string(scheduleVersion) }
          { name: 'Pii__LiveAuthorized', value: string(liveAuthorized) }
          { name: 'Pii__Model__ProcessingAuthorized', value: string(modelProcessingAuthorized) }
          { name: 'Pii__Model__Endpoint', value: modelEndpoint }
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
          empty(secUserAgent) ? { name: 'Pii__Sources__Sec__UserAgent', value: '' } : { name: 'Pii__Sources__Sec__UserAgent', secretRef: 'sec-user-agent' }
          { name: 'Pii__Sources__Ecb__RightsAuthorized', value: string(ecbRightsAuthorized) }
          { name: 'Pii__Sources__Ecb__ModelProcessingAllowed', value: string(ecbModelProcessingAllowed) }
          { name: 'Pii__Sources__TwelveData__RightsAuthorized', value: string(twelveDataRightsAuthorized) }
          { name: 'Pii__Sources__TwelveData__ModelProcessingAllowed', value: string(twelveDataModelProcessingAllowed) }
          { name: 'Pii__Sources__TwelveData__EvidenceRetentionAuthorized', value: string(twelveDataEvidenceRetentionAuthorized) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__Authorized', value: string(twelveDataRequestCreditsAuthorized) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__RateCardVersion', value: twelveDataRateCardVersion }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__MaximumCreditsPerRequest', value: string(twelveDataMaximumCreditsPerRequest) }
          { name: 'Pii__Sources__TwelveData__RequestCreditAuthorization__DailyMaximumCredits', value: string(twelveDataDailyMaximumCredits) }
        ], entitlementsEnvironment, creditWeightsEnvironment, exchangeEnvironment, hasTwelveSecret ? [{ name: 'PII_TWELVEDATA_API_KEY', secretRef: 'twelve-data-api-key' }] : [])
      }]
    }
  }
}
output webHostName string = '${webName}.${environment.properties.defaultDomain}'
output workerJobName string = workerName