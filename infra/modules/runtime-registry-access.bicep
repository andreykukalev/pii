param registryName string
param principalIds array
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: registryName }
var role = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource grants 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in principalIds: {
  name: guid(registry.id, principalId, role)
  scope: registry
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: role }
}]