targetScope = 'resourceGroup'
param storageAccountName string
param documentsName string = 'documents'
param keysName string = 'keys'
param grantKeyContainer bool = false
param principalId string
@allowed(['User', 'Group', 'ServicePrincipal'])
param principalType string = 'User'
param foundryAccountName string = ''
param grantInference bool = false
resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { name: '${storageAccountName}/default/${documentsName}' }
resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { name: '${storageAccountName}/default/${keysName}' }
resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = { name: foundryAccountName }
var blobRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var inferenceRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
resource documentsGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documents.id, principalId, blobRole)
  scope: documents
  properties: { principalId: principalId, principalType: principalType, roleDefinitionId: blobRole }
}
resource keysGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantKeyContainer) {
  name: guid(keys.id, principalId, blobRole)
  scope: keys
  properties: { principalId: principalId, principalType: principalType, roleDefinitionId: blobRole }
}
resource inferenceGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantInference) {
  name: guid(foundry.id, principalId, inferenceRole)
  scope: foundry
  properties: { principalId: principalId, principalType: principalType, roleDefinitionId: inferenceRole }
}