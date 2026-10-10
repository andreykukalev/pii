param name string
param location string
param databaseName string = 'pii'
param tenantId string
param adminObjectId string
param adminName string
param allowAzureServices bool = false
param firewallRules array = []
param tags object = {}
@minValue(1)
@maxValue(4)
param maximumVcores int = 1
@allowed([60, 120, 240, 480, 720, 1440])
param autoPauseDelayMinutes int = 60
@minValue(1)
@maxValue(32)
param maximumStorageGb int = 2

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: adminName
      sid: adminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
  }
}
resource azureFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (allowAzureServices) {
  parent: server
  name: 'AllowAzureServicesExplicitOptIn'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}
resource customFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [for rule in firewallRules: {
  parent: server
  name: rule.name
  properties: { startIpAddress: rule.startIpAddress, endIpAddress: rule.endIpAddress }
}]
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: maximumVcores }
  properties: {
    collation: 'Latin1_General_100_BIN2'
    minCapacity: json('0.5')
    autoPauseDelay: autoPauseDelayMinutes
    maxSizeBytes: maximumStorageGb * 1024 * 1024 * 1024
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}
resource backupRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: database
  name: 'default'
  properties: { retentionDays: 7 }
}
output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
output databaseId string = database.id