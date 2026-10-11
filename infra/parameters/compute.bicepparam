using '../stages/compute.bicep'
var config = loadJsonContent('manifest.json')
param location = config.location
param tags = config.tags
param prefix = config.prefix
param environmentName = config.environmentName
param managedEnvironmentName = config.shared.compute.managedEnvironmentName
param registryName = config.shared.compute.registryName
param webIdentityName = config.shared.compute.webIdentityName
param workerIdentityName = config.shared.compute.workerIdentityName
param storageAccountName = config.shared.storage.accountName
param documentsName = config.shared.storage.documentsName
param keysName = config.shared.storage.keysName
param foundryAccountName = config.shared.foundry.accountName
param grantInference = config.stages.compute.parameters.grantInference
param monitoringEnabled = config.stages.compute.parameters.monitoringEnabled
param workspaceName = config.shared.monitoring.workspaceName