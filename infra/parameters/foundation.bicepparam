using '../stages/foundation.bicep'
var config = loadJsonContent('manifest.json')
param resourceGroupName = config.resourceGroupName
param location = config.location
param tags = config.tags
param createResourceGroup = config.stages.foundation.parameters.createResourceGroup