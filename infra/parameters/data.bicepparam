using '../stages/data.bicep'
var config = loadJsonContent('manifest.json')
param location = config.location
param tags = config.tags
param prefix = config.prefix
param environmentName = config.environmentName
param serverName = config.shared.data.serverName
param databaseName = config.shared.data.databaseName
param tenantId = config.shared.auth.tenantId
param mode = config.stages.data.parameters.mode
param adminObjectId = config.stages.data.parameters.adminObjectId
param adminName = config.stages.data.parameters.adminName
param computeModel = config.stages.data.parameters.computeModel
param firewallRules = config.stages.data.parameters.firewallRules