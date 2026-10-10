targetScope = 'resourceGroup'

@description('Existing or explicitly authorized new Azure OpenAI account name in this module resource group.')
param accountName string
param location string = resourceGroup().location
param workerPrincipalId string
param createAccount bool = false
param createDeployment bool = false
param deploymentName string = ''
param modelName string = ''
param modelVersion string = ''
@allowed(['Standard', 'GlobalStandard', 'DataZoneStandard'])
param deploymentSku string = 'Standard'
@minValue(1)
param deploymentCapacity int = 1
param tags object = {}

resource newAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (createAccount) {
  name: accountName
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  tags: tags
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: accountName
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (createDeployment) {
  parent: account
  name: deploymentName
  sku: { name: deploymentSku, capacity: deploymentCapacity }
  properties: {
    model: { format: 'OpenAI', name: modelName, version: modelVersion }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [newAccount]
}

resource workerInference 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, workerPrincipalId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
  dependsOn: [newAccount]
}

output accountId string = account.id
output endpoint string = account.properties.endpoint