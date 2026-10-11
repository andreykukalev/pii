targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
@allowed(['create', 'existing'])
param mode string = 'create'
param serverName string = '${prefix}-${environmentName}-sql-${uniqueString(resourceGroup().id, prefix, environmentName)}'
param databaseName string = 'pii'
param tenantId string
param adminObjectId string
param adminName string
@allowed(['Serverless', 'Basic'])
param computeModel string = 'Serverless'
@allowed([1, 2, 4])
param maximumVcores int = 1
@allowed(['0.5', '1'])
param minimumVcores string = '0.5'
@allowed([60, 120, 240, 480, 720, 1440])
param autoPauseDelayMinutes int = 60
@minValue(1)
@maxValue(32)
param maximumStorageGb int = 2
@allowed(['Local', 'Zone', 'Geo', 'GeoZone'])
param backupRedundancy string = 'Local'
@allowed([1, 7])
param backupRetentionDays int = 7
param allowAzureServices bool = false
param firewallRules array = []
param developmentPasswordOptIn bool = false
param sqlAdministratorLogin string = 'pii_dev_admin'
@secure()
param sqlAdministratorPassword string = ''

resource newServer 'Microsoft.Sql/servers@2023-08-01' = if (mode == 'create') {
  name: serverName
  location: location
  tags: tags
  properties: union({
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: adminName
      sid: adminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: !(developmentPasswordOptIn && environmentName == 'dev')
    }
  }, developmentPasswordOptIn && environmentName == 'dev' ? { administratorLogin: sqlAdministratorLogin, administratorLoginPassword: sqlAdministratorPassword } : {})
}
resource server 'Microsoft.Sql/servers@2023-08-01' existing = { name: serverName }
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = if (mode == 'create') {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: computeModel == 'Basic'
    ? { name: 'Basic', tier: 'Basic', capacity: 5 }
    : { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: maximumVcores }
  properties: union({
    collation: 'Latin1_General_100_BIN2'
    maxSizeBytes: maximumStorageGb * 1024 * 1024 * 1024
    requestedBackupStorageRedundancy: backupRedundancy
    zoneRedundant: false
  }, computeModel == 'Serverless' ? { minCapacity: json(minimumVcores), autoPauseDelay: autoPauseDelayMinutes } : {})
  dependsOn: [newServer]
}
resource backup 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = if (mode == 'create') {
  name: '${serverName}/${databaseName}/default'
  properties: { retentionDays: backupRetentionDays }
  dependsOn: [database]
}
resource firewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = [for rule in firewallRules: if (mode == 'create') {
  parent: server
  name: rule.name
  properties: { startIpAddress: rule.startIpAddress, endIpAddress: rule.endIpAddress }
  dependsOn: [newServer]
}]
resource azureFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (mode == 'create' && allowAzureServices) {
  parent: server
  name: 'AllowAzureServicesExplicitOptIn'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
  dependsOn: [newServer]
}
output serverId string = server.id
output databaseId string = '${server.id}/databases/${databaseName}'
output serverFqdn string = server.properties.fullyQualifiedDomainName
output database string = databaseName