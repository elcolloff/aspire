@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource workergroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('workergroup${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  tags: {
    'aspire-resource-name': 'workergroup'
  }
}

output id string = workergroup.id

output name string = workergroup.name