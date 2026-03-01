targetScope = 'subscription'

@description('Azure region')
param location string = 'swedencentral'

@description('Resource group name for health model resources')
param resourceGroupName string = 'rg-health-model'

@description('Optional: Service Group resource ID for auto-discovery. If empty, discovery is skipped.')
param serviceGroupScope string = ''

@description('Email address for health alert notifications')
param alertEmail string = ''

// ─── Resource Group ───────────────────────────────────────────────────

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
}

// ─── Health Model Module ──────────────────────────────────────────────

module healthModel 'modules/health-model.bicep' = {
  name: 'health-model-deploy'
  scope: rg
  params: {
    location: location
    serviceGroupScope: serviceGroupScope
    alertEmail: alertEmail
  }
}

output resourceGroupName string = rg.name
output monitorAccountName string = healthModel.outputs.monitorAccountName
output healthModelName string = healthModel.outputs.healthModelName
