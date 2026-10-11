targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
@allowed(['Consumption'])
param hostingOption string = 'Consumption'
@allowed(['create', 'existing'])
param environmentMode string = 'create'
param managedEnvironmentName string = '${prefix}-${environmentName}-environment'
@allowed(['create', 'existing'])
param registryMode string = 'create'
param registryName string = 'acr${uniqueString(resourceGroup().id, prefix, environmentName)}'
param registryResourceGroup string = resourceGroup().name
@allowed(['Basic', 'Standard', 'Premium'])
param registrySku string = 'Basic'
param createIdentities bool = true
param webIdentityName string = '${prefix}-${environmentName}-web-mi'
param workerIdentityName string = '${prefix}-${environmentName}-worker-mi'
param storageAccountName string
param storageResourceGroup string = resourceGroup().name
param documentsName string = 'documents'
param keysName string = 'keys'
param grantInference bool = false
param foundryAccountName string = ''
param foundryResourceGroup string = resourceGroup().name
param monitoringEnabled bool = false
param workspaceName string = ''
param workspaceResourceGroup string = resourceGroup().name

resource newWebIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (createIdentities) {
  name: webIdentityName
  location: location
  tags: tags
}
resource newWorkerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (createIdentities) {
  name: workerIdentityName
  location: location
  tags: tags
}
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: webIdentityName }
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: workerIdentityName }
resource newRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = if (registryMode == 'create') {
  name: registryName
  location: location
  tags: tags
  sku: { name: registrySku }
  properties: { adminUserEnabled: false, publicNetworkAccess: 'Enabled' }
}
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(registryResourceGroup)
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
  scope: resourceGroup(workspaceResourceGroup)
}
resource newEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (environmentMode == 'create') {
  name: managedEnvironmentName
  location: location
  tags: tags
  properties: {
    workloadProfiles: [{ name: hostingOption, workloadProfileType: hostingOption }]
    appLogsConfiguration: monitoringEnabled ? {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: workspace.properties.customerId, sharedKey: workspace.listKeys().primarySharedKey }
    } : { destination: 'none' }
  }
}
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = { name: managedEnvironmentName }
module pullGrants '../modules/runtime-registry-access.bicep' = {
  name: '${prefix}-${environmentName}-runtime-registry-access'
  scope: resourceGroup(registryResourceGroup)
  params: {
    registryName: registryName
    principalIds: [webIdentity.properties.principalId, workerIdentity.properties.principalId]
  }
  dependsOn: [newRegistry, newWebIdentity, newWorkerIdentity]
}
module blobAccess '../modules/runtime-blob-access.bicep' = {
  name: '${prefix}-${environmentName}-runtime-blob-access'
  scope: resourceGroup(storageResourceGroup)
  params: {
    storageAccountName: storageAccountName
    documentsName: documentsName
    keysName: keysName
    webPrincipalId: webIdentity.properties.principalId
    workerPrincipalId: workerIdentity.properties.principalId
  }
  dependsOn: [newWebIdentity, newWorkerIdentity]
}
module inference '../modules/runtime-inference-access.bicep' = if (grantInference) {
  name: '${prefix}-${environmentName}-runtime-inference-access'
  scope: resourceGroup(foundryResourceGroup)
  params: { accountName: foundryAccountName, principalId: workerIdentity.properties.principalId }
  dependsOn: [newWorkerIdentity]
}
output environmentId string = environment.id
output defaultDomain string = environment.properties.defaultDomain
output registryId string = registry.id
output registryLoginServer string = registry.properties.loginServer
output webIdentityId string = webIdentity.id
output workerIdentityId string = workerIdentity.id
output webPrincipalId string = webIdentity.properties.principalId
output workerPrincipalId string = workerIdentity.properties.principalId
output webClientId string = webIdentity.properties.clientId
output workerClientId string = workerIdentity.properties.clientId