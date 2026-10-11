using '../stages/foundry.bicep'
var config = loadJsonContent('manifest.json')
param location = config.location
param tags = config.tags
param prefix = config.prefix
param environmentName = config.environmentName
param accountName = config.shared.foundry.accountName
param projectName = config.shared.foundry.projectName
param mode = config.stages.foundry.parameters.mode
param createProject = config.stages.foundry.parameters.createProject
param createModelDeployment = config.stages.foundry.parameters.createModelDeployment