@description('Azure region')
param location string

@description('Optional: Service Group resource ID for auto-discovery')
param serviceGroupScope string = ''

@description('Email for health alert notifications')
param alertEmail string = ''

// ─── Naming ───────────────────────────────────────────────────────────

var salt = substring(uniqueString(resourceGroup().id), 0, 5)
var monitorAccountName = 'mon-health-${salt}'
var healthModelName = 'etherwurst-health'
var actionGroupName = 'ag-health-${salt}'

// ─── Monitor Account ──────────────────────────────────────────────────

resource monitorAccount 'Microsoft.Monitor/accounts@2023-04-03' = {
  name: monitorAccountName
  location: location
}

// ─── Action Group (for alerts) ────────────────────────────────────────

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: actionGroupName
  location: 'Global'
  properties: {
    groupShortName: 'HealthAlert'
    enabled: true
    emailReceivers: [
      {
        name: 'health-alert-email'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// ─── Health Model ─────────────────────────────────────────────────────

resource healthModel 'Microsoft.Monitor/accounts/healthmodels@2025-05-03-preview' = {
  parent: monitorAccount
  name: healthModelName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: !empty(serviceGroupScope) ? {
    discovery: {
      addRecommendedSignals: 'Enabled'
      scope: serviceGroupScope
    }
  } : {}
}

// ─── Signal Definitions ───────────────────────────────────────────────

resource cpuSignal 'Microsoft.Monitor/accounts/healthmodels/signaldefinitions@2025-05-03-preview' = {
  parent: healthModel
  name: 'high-cpu-usage'
  properties: {
    displayName: 'High CPU Usage'
    signalKind: 'AzureResourceMetric'
    metricNamespace: 'Microsoft.ContainerService/managedClusters'
    metricName: 'node_cpu_usage_percentage'
    aggregationType: 'Average'
    timeGrain: 'PT5M'
    dataUnit: 'Percent'
    refreshInterval: 'PT5M'
    evaluationRules: {
      degradedRule: { operator: 'GreaterThan', threshold: '70' }
      unhealthyRule: { operator: 'GreaterThan', threshold: '90' }
    }
  }
}

resource memorySignal 'Microsoft.Monitor/accounts/healthmodels/signaldefinitions@2025-05-03-preview' = {
  parent: healthModel
  name: 'high-memory-usage'
  properties: {
    displayName: 'High Memory Usage'
    signalKind: 'AzureResourceMetric'
    metricNamespace: 'Microsoft.ContainerService/managedClusters'
    metricName: 'node_memory_rss_percentage'
    aggregationType: 'Average'
    timeGrain: 'PT5M'
    dataUnit: 'Percent'
    refreshInterval: 'PT5M'
    evaluationRules: {
      degradedRule: { operator: 'GreaterThan', threshold: '75' }
      unhealthyRule: { operator: 'GreaterThan', threshold: '90' }
    }
  }
}

// ─── Outputs ──────────────────────────────────────────────────────────

output monitorAccountName string = monitorAccount.name
output healthModelName string = healthModel.name
output healthModelPrincipalId string = healthModel.identity.principalId
