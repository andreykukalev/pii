param storageAccountName string
param documentsName string
param keysName string
param webPrincipalId string
param workerPrincipalId string
resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { name: '${storageAccountName}/default/${documentsName}' }
resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { name: '${storageAccountName}/default/${keysName}' }
var role = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource documentGrants 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in [webPrincipalId, workerPrincipalId]: {
  name: guid(documents.id, principal, role)
  scope: documents
  properties: { principalId: principal, principalType: 'ServicePrincipal', roleDefinitionId: role }
}]
resource keyGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keys.id, webPrincipalId, role)
  scope: keys
  properties: { principalId: webPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: role }
}