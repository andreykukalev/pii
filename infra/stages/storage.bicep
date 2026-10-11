targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
@allowed(['create', 'existing'])
param mode string = 'create'
param accountName string = 'st${uniqueString(resourceGroup().id, prefix, environmentName)}'
param documentsName string = 'documents'
param keysName string = 'keys'
@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS', 'Standard_RAGRS', 'Standard_GZRS', 'Standard_RAGZRS'])
param sku string = 'Standard_LRS'
@allowed(['Hot', 'Cool'])
param accessTier string = 'Hot'
param versioningEnabled bool = true
@minValue(1)
@maxValue(365)
param softDeleteDays int = 7
param developmentSharedKeyOptIn bool = false
@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'
@allowed(['Allow', 'Deny'])
param networkDefaultAction string = 'Allow'
param allowedIpAddresses array = []

resource newAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = if (mode == 'create') {
  name: accountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: sku }
  properties: {
    accessTier: accessTier
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: developmentSharedKeyOptIn && environmentName == 'dev'
    defaultToOAuthAuthentication: true
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      bypass: 'None'
      defaultAction: networkDefaultAction
      ipRules: [for address in allowedIpAddresses: { action: 'Allow', value: address }]
    }
    encryption: { keySource: 'Microsoft.Storage', services: { blob: { enabled: true, keyType: 'Account' } } }
  }
}
resource account 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: accountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (mode == 'create') {
  parent: account
  name: 'default'
  properties: {
    isVersioningEnabled: versioningEnabled
    deleteRetentionPolicy: { enabled: true, days: softDeleteDays }
    containerDeleteRetentionPolicy: { enabled: true, days: softDeleteDays }
  }
  dependsOn: [newAccount]
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for containerName in [documentsName, keysName]: if (mode == 'create') {
  name: '${accountName}/default/${containerName}'
  properties: { publicAccess: 'None' }
  dependsOn: [blobs]
}]
output accountId string = account.id
output name string = accountName
output serviceUri string = account.properties.primaryEndpoints.blob
output documentsContainer string = documentsName
output keysContainer string = keysName
output keyRingUri string = '${account.properties.primaryEndpoints.blob}${keysName}/keyring.xml'