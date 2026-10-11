targetScope = 'subscription'

param resourceGroupName string
param location string
param tags object = {}
param createResourceGroup bool = true

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = if (createResourceGroup) {
  name: resourceGroupName
  location: location
  tags: tags
}

output resourceGroupId string = subscriptionResourceId('Microsoft.Resources/resourceGroups', resourceGroupName)
output name string = resourceGroupName
output region string = location