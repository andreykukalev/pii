targetScope = 'resourceGroup'
param location string
param tags object = {}
param prefix string = 'pii'
param environmentName string = 'dev'
param enabled bool = false
@allowed(['create', 'existing'])
param workspaceMode string = 'create'
param workspaceName string = '${prefix}-${environmentName}-logs'
param workspaceResourceGroup string = resourceGroup().name
param insightsName string = '${prefix}-${environmentName}-insights'
@allowed(['create', 'existing'])
param insightsMode string = 'create'
@allowed([30, 60, 90])
param retentionDays int = 30
@allowed(['0.1', '0.25', '0.5', '1'])
param dailyQuotaGb string = '0.1'
@minValue(1)
@maxValue(100)
param samplingPercentage int = 10

resource newWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (enabled && workspaceMode == 'create') {
  name: workspaceName
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: retentionDays, workspaceCapping: { dailyQuotaGb: json(dailyQuotaGb) } }
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
  scope: resourceGroup(workspaceResourceGroup)
}
resource newInsights 'Microsoft.Insights/components@2020-02-02' = if (enabled && insightsMode == 'create') {
  name: insightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    SamplingPercentage: samplingPercentage
    DisableLocalAuth: false
  }
  dependsOn: [newWorkspace]
}
resource insights 'Microsoft.Insights/components@2020-02-02' existing = { name: insightsName }
output workspaceId string = enabled ? workspace.id : ''
output insightsId string = enabled ? insights.id : ''
output telemetryEnabled bool = enabled