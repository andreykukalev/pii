using '../stages/storage.bicep'
var config = loadJsonContent('manifest.json')
param location = config.location
param tags = config.tags
param prefix = config.prefix
param environmentName = config.environmentName
param accountName = config.shared.storage.accountName
param documentsName = config.shared.storage.documentsName
param keysName = config.shared.storage.keysName
param mode = config.stages.storage.parameters.mode