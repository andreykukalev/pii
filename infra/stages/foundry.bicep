targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
@allowed(['create', 'existing'])
param mode string = 'create'
param accountName string = '${prefix}-${environmentName}-ai-${uniqueString(resourceGroup().id, prefix, environmentName)}'
@allowed(['AIServices', 'OpenAI'])
param existingAccountKind string = 'AIServices'
param projectName string = '${prefix}-${environmentName}-project'
param createProject bool = mode == 'create'
param bindProject bool = false
param disableLocalAuth bool = true
@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'
param createModelDeployment bool = false
param deploymentName string = ''
param modelName string = ''
param modelVersion string = ''
@allowed(['Standard', 'GlobalStandard', 'DataZoneStandard'])
param deploymentSku string = 'Standard'
@minValue(1)
@maxValue(100)
param deploymentCapacity int = 1

resource newAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = if (mode == 'create') {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: accountName
    allowProjectManagement: true
    disableLocalAuth: disableLocalAuth
    publicNetworkAccess: publicNetworkAccess
  }
}
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = { name: accountName }
resource boundProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = { parent: account, name: projectName }
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = if ((mode == 'create' || existingAccountKind == 'AIServices') && createProject) {
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: { displayName: projectName, description: 'PII logical research workspace; no hosted agents.' }
  dependsOn: [newAccount]
}
resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (createModelDeployment) {
  parent: account
  name: deploymentName
  sku: { name: deploymentSku, capacity: deploymentCapacity }
  properties: {
    model: { format: 'OpenAI', name: modelName, version: modelVersion }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [newAccount]
}
output accountId string = account.id
output name string = accountName
output kind string = mode == 'create' ? 'AIServices' : existingAccountKind
output openAiRootEndpoint string = mode == 'create' || existingAccountKind == 'AIServices'
  ? account.properties.endpoints['OpenAI Language Model Instance']
  : account.properties.endpoint
output projectId string = createProject || bindProject ? '${account.id}/projects/${projectName}' : ''
output projectEndpoints object = createProject || bindProject ? boundProject.properties.endpoints : {}
output deployment string = deploymentName
output version string = modelVersion