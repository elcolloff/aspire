@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param hostmi_outputs_id string

resource hostgroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: take('hostgroup${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${hostmi_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'hostgroup'
  }
}

output id string = hostgroup.id

output name string = hostgroup.name