using '../stages/monitoring.bicep'
var config = loadJsonContent('manifest.json')
param location = config.location
param tags = config.tags
param prefix = config.prefix
param environmentName = config.environmentName
param workspaceName = config.shared.monitoring.workspaceName
param insightsName = config.shared.monitoring.insightsName
param enabled = config.stages.monitoring.parameters.enabled