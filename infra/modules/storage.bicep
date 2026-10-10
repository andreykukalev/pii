param name string
param location string
param webPrincipalId string
param workerPrincipalId string
param tags object = {}
param documentsContainer string = 'pii-documents'
param keysContainer string = 'dataprotectionkeys'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    encryption: {
      keySource: 'Microsoft.Storage'
      services: { blob: { enabled: true, keyType: 'Account' } }
    }
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}
resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: documentsContainer
  properties: { publicAccess: 'None' }
}
resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: keysContainer
  properties: { publicAccess: 'None' }
}
var contributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource documentRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in [webPrincipalId, workerPrincipalId]: {
  name: guid(documents.id, principal, contributor)
  scope: documents
  properties: { roleDefinitionId: contributor, principalId: principal, principalType: 'ServicePrincipal' }
}]
resource keyRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keys.id, webPrincipalId, contributor)
  scope: keys
  properties: { roleDefinitionId: contributor, principalId: webPrincipalId, principalType: 'ServicePrincipal' }
}
output serviceUri string = storage.properties.primaryEndpoints.blob
output documentsName string = documents.name
output keyRingUri string = '${storage.properties.primaryEndpoints.blob}${keys.name}/data-protection.xml'
output storageId string = storage.id