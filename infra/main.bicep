@description('Name of the azd environment — used as a salt and prefix for all resources')
param environmentName string

@description('Project name prefix for all resources')
param projectName string = 'hazscam'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Enable private networking (private cluster, private endpoints, VPN gateway)')
param privateNetworking bool = false

@description('Enable Application Gateway for Containers (requires region support)')
param enableAGC bool = false

@description('Kubernetes version')
param kubernetesVersion string = '1.34'

@description('VPN point-to-site client address pool CIDR')
param vpnClientAddressPool string = '172.16.0.0/24'

@description('Give the deploying developer full RBAC access (AKS admin, Storage admin, ACR push)')
param isDeveloper bool = true

@description('Enable Advanced Container Networking Services (Hubble observability + FQDN policies, ~$18/node/month)')
param enableACNS bool = true

@description('Enable Azure Data Explorer (ADX) for blockchain analytics')
param enableAdx bool = false

@description('Object ID of the developer principal — auto-set by azd preprovision hook')
param developerPrincipalId string = ''

@description('Principal type for the developer identity')
@allowed(['User', 'Group', 'ServicePrincipal'])
param developerPrincipalType string = 'User'

// ─── Naming ───────────────────────────────────────────────────────────

var salt = substring(uniqueString(resourceGroup().id, environmentName), 0, 5)
var prefix = '${projectName}${salt}'
var dashPrefix = '${projectName}-${salt}'

var aksIdentityName = 'id-${dashPrefix}-aks'
var albIdentityName = 'id-${dashPrefix}-alb'
var appIdentityName = 'id-${dashPrefix}-app'
var opencostIdentityName = 'id-${dashPrefix}-opencost'
var logAnalyticsName = 'log-${dashPrefix}'
var monitorWorkspaceName = 'mon-${dashPrefix}'
var appInsightsName = 'appi-${dashPrefix}'
var dceName = 'dce-${dashPrefix}'
var dcrPrometheusName = 'dcr-${dashPrefix}-prom'

var vnetName = 'vnet-${dashPrefix}'
var acrName = 'acr${prefix}'
var aksClusterName = 'aks-${dashPrefix}'
var aiFoundryName = 'aif-${dashPrefix}'
var adxClusterName = 'adx${prefix}'
var adxDatabaseName = 'ethereum'
var storageName = 'st${prefix}'
var vpnPipName = 'pip-${dashPrefix}-vpn'
var vpnGatewayResourceName = 'vpng-${dashPrefix}'

// ─── Networking constants ─────────────────────────────────────────────

var vnetAddressSpace = '10.0.0.0/16'
var snetAksPrefix = '10.0.0.0/20'
var snetAlbPrefix = '10.0.16.0/24'
var snetGatewayPrefix = '10.0.17.0/24'
var snetPePrefix = '10.0.18.0/24'
var podCidr = '192.168.0.0/16'
var serviceCidr = '10.1.0.0/16'
var dnsServiceIP = '10.1.0.10'

var snetAksName = 'snet-aks'
var snetAlbName = 'snet-alb'
var snetGatewayName = 'GatewaySubnet'
var snetPeName = 'snet-pe'

var albDelegationService = 'Microsoft.ServiceNetworking/trafficControllers'

var snetAksId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, snetAksName)
var snetAlbId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, snetAlbName)
var snetGatewayId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, snetGatewayName)
var snetPeId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, snetPeName)

// ─── Well-known role definition IDs ───────────────────────────────────

var roleIds = {
  networkContributor: '4d97b98b-1d4f-4787-a291-c67834d212e7'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  aksRbacClusterAdmin: 'b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b'
  aksRbacAdmin: '3498e952-d568-435e-9b2c-8d77e338d7f7'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  monitoringReader: '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
  albConfigManager: 'fbc52c3f-28ad-4303-a892-8a056630b8f1'
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
}

// Helper to get full role definition resource IDs
var roleDefs = {
  networkContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.networkContributor)
  acrPull: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.acrPull)
  acrPush: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.acrPush)
  aksRbacClusterAdmin: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.aksRbacClusterAdmin)
  aksRbacAdmin: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.aksRbacAdmin)
  storageBlobDataContributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataContributor)
  cognitiveServicesOpenAIUser: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.cognitiveServicesOpenAIUser)
  monitoringReader: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.monitoringReader)
  albConfigManager: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.albConfigManager)
  reader: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.reader)
  contributor: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.contributor)
}

// ─── VPN Entra ID constants ──────────────────────────────────────────

var vpnAadAudience = 'c632b3df-fb67-4d84-bdcf-b95ad541b5c8'
var vpnAadTenant = '${environment().authentication.loginEndpoint}${tenant().tenantId}/'
var vpnAadIssuer = vpnAadTenant

// ─── ALB controller constants ────────────────────────────────────────

var albControllerNamespace = 'azure-alb-system'
var albControllerServiceAccount = 'alb-controller-sa'
var albFederatedSubject = 'system:serviceaccount:${albControllerNamespace}:${albControllerServiceAccount}'
var workloadIdentityAudience = 'api://AzureADTokenExchange'

// ─── SKU / access helpers ────────────────────────────────────────────

var acrSku = privateNetworking ? 'Premium' : 'Basic'
var publicAccess = privateNetworking ? 'Disabled' : 'Enabled'
var networkDefault = privateNetworking ? 'Deny' : 'Allow'
var hasDeveloper = isDeveloper && !empty(developerPrincipalId)

// ─── Identities ───────────────────────────────────────────────────────

resource aksIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-01-31-preview' = {
  name: aksIdentityName
  location: location
}

resource albIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-01-31-preview' = {
  name: albIdentityName
  location: location
}

// Workload identity for the application running on AKS
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-01-31-preview' = {
  name: appIdentityName
  location: location
}

// Workload identity for OpenCost (Azure Rate Card API access)
resource opencostIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-01-31-preview' = {
  name: opencostIdentityName
  location: location
}

// ─── Monitoring ───────────────────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource monitorWorkspace 'Microsoft.Monitor/accounts@2025-10-03-preview' = {
  name: monitorWorkspaceName
  location: location
}

resource appInsights 'Microsoft.Insights/components@2020-02-02-preview' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource dce 'Microsoft.Insights/dataCollectionEndpoints@2024-03-11' = {
  name: dceName
  location: location
  properties: {}
}

resource dcrPrometheus 'Microsoft.Insights/dataCollectionRules@2024-03-11' = {
  name: dcrPrometheusName
  location: location
  properties: {
    dataCollectionEndpointId: dce.id
    dataSources: {
      prometheusForwarder: [
        {
          name: 'PrometheusDataSource'
          streams: ['Microsoft-PrometheusMetrics']
        }
      ]
    }
    destinations: {
      monitoringAccounts: [
        {
          name: 'MonitoringAccount'
          accountResourceId: monitorWorkspace.id
        }
      ]
    }
    dataFlows: [
      {
        streams: ['Microsoft-PrometheusMetrics']
        destinations: ['MonitoringAccount']
      }
    ]
  }
}

// ─── Networking ───────────────────────────────────────────────────────

// Empty NSG shell — AKS dynamically manages the security rules at runtime
resource nsgAks 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: 'nsg-${dashPrefix}-aks'
  location: location
}

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressSpace] }
    subnets: [
      {
        name: snetAksName
        properties: {
          addressPrefix: snetAksPrefix
          networkSecurityGroup: { id: nsgAks.id }
        }
      }
      {
        name: snetAlbName
        properties: {
          addressPrefix: snetAlbPrefix
          delegations: [
            { name: 'alb', properties: { serviceName: albDelegationService } }
          ]
        }
      }
      {
        name: snetGatewayName
        properties: { addressPrefix: snetGatewayPrefix }
      }
      {
        name: snetPeName
        properties: { addressPrefix: snetPePrefix }
      }
    ]
  }
}

// Note: Erigon data disk (erigon-data-premiumv2, Premium SSD v2, 10k IOPS)
// lives in the MC_ resource group, managed outside Bicep.
// Created from snapshot of erigon-data-zrs. See erigon-storage.yaml for K8s PV/PVC.

// ─── Container Registry ──────────────────────────────────────────────

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: acrName
  location: location
  sku: { name: acrSku }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: publicAccess
  }
}

// ─── AKS ──────────────────────────────────────────────────────────────

resource aks 'Microsoft.ContainerService/managedClusters@2025-10-01' = {
  name: aksClusterName
  location: location
  sku: { name: 'Base', tier: 'Free' }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${aksIdentity.id}': {} }
  }
  properties: {
    dnsPrefix: 'aks-${prefix}'
    kubernetesVersion: kubernetesVersion
    nodeProvisioningProfile: { mode: 'Auto' }
    agentPoolProfiles: [
      {
        name: 'system2'
        count: 1
        vmSize: 'Standard_B4s_v2'
        mode: 'System'
        osType: 'Linux'
        osSKU: 'AzureLinux'
        vnetSubnetID: snetAksId
        enableAutoScaling: false
        nodeTaints: [
          'CriticalAddonsOnly=true:NoSchedule'
        ]
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'cilium'
      networkDataplane: 'cilium'
      podCidr: podCidr
      serviceCidr: serviceCidr
      dnsServiceIP: dnsServiceIP
      loadBalancerSku: 'standard'
      advancedNetworking: {
        enabled: enableACNS
        observability: {
          enabled: enableACNS
        }
        security: {
          enabled: enableACNS
        }
      }
    }
    addonProfiles: {
      omsagent: {
        enabled: true
        config: { logAnalyticsWorkspaceResourceID: logAnalytics.id }
      }
      azurepolicy: {
        enabled: true
        config: { version: 'v2' }
      }
    }
    azureMonitorProfile: {
      metrics: {
        enabled: true
        kubeStateMetrics: { metricAnnotationsAllowList: '', metricLabelsAllowlist: '' }
      }
    }
    apiServerAccessProfile: { enablePrivateCluster: privateNetworking }
    aadProfile: { managed: true, enableAzureRBAC: true }
    autoUpgradeProfile: { upgradeChannel: 'stable', nodeOSUpgradeChannel: 'NodeImage' }
    oidcIssuerProfile: { enabled: true }
    securityProfile: { workloadIdentity: { enabled: true } }
    // Application Gateway for Containers — ALB Controller managed add-on
    ingressProfile: {
      webAppRouting: { enabled: false }
    }
    serviceMeshProfile: { mode: 'Disabled' }
    workloadAutoScalerProfile: {
      verticalPodAutoscaler: { enabled: true }
    }
  }
}

// AGC ALB controller extension (requires region support for microsoft.network.appgwforcontainers)
resource albExtension 'Microsoft.KubernetesConfiguration/extensions@2023-05-01' = if (enableAGC) {
  name: 'alb-controller'
  scope: aks
  properties: {
    extensionType: 'microsoft.network.appgwforcontainers'
    autoUpgradeMinorVersion: true
    configurationSettings: {
      'albController.namespace': albControllerNamespace
    }
  }
}

// ─── Service identity RBAC ────────────────────────────────────────────

// AKS identity → Network Contributor on VNet
resource aksIdentityNetworkRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vnet.id, aksIdentity.id, roleIds.networkContributor)
  scope: vnet
  properties: {
    roleDefinitionId: roleDefs.networkContributor
    principalId: aksIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Kubelet identity → ACR Pull
resource kubeletAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aks.id, roleIds.acrPull)
  scope: acr
  properties: {
    roleDefinitionId: roleDefs.acrPull
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// App identity → Storage Blob Data Contributor
resource appIdentityStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, appIdentity.id, roleIds.storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: roleDefs.storageBlobDataContributor
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// App identity → federated credential so pods on AKS can assume this identity
resource appFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2025-01-31-preview' = {
  name: 'app-workload-identity'
  parent: appIdentity
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:default:${projectName}-sa'
    audiences: [workloadIdentityAudience]
  }
}

// ADX ETL → federated credential (CronJob in ethereum namespace)
resource adxEtlFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2025-01-31-preview' = if (enableAdx) {
  name: 'adx-etl-workload-identity'
  parent: appIdentity
  dependsOn: [appFederatedCredential]
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:ethereum:adx-etl-sa'
    audiences: [workloadIdentityAudience]
  }
}

// API pod → federated credential (Deployment in ethereum namespace)
resource apiFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2025-01-31-preview' = {
  name: 'api-workload-identity'
  parent: appIdentity
  dependsOn: enableAdx ? [adxEtlFederatedCredential] : [appFederatedCredential]
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:ethereum:hazmebeenscammed-api-sa'
    audiences: [workloadIdentityAudience]
  }
}

// OpenCost → federated credential (Deployment in opencost namespace)
resource opencostFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2025-01-31-preview' = {
  name: 'opencost-workload-identity'
  parent: opencostIdentity
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:opencost:opencost'
    audiences: [workloadIdentityAudience]
  }
}

// OpenCost needs Reader on the subscription for Azure Rate Card API
resource opencostReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, opencostIdentity.id, roleIds.reader)
  properties: {
    principalId: opencostIdentity.properties.principalId
    roleDefinitionId: roleDefs.reader
    principalType: 'ServicePrincipal'
  }
}

// ─── ALB identity RBAC ───────────────────────────────────────────────

resource albIdentityNetworkRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(snetAlbId, albIdentity.id, roleIds.networkContributor)
  scope: vnet
  properties: {
    roleDefinitionId: roleDefs.networkContributor
    principalId: albIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ALB identity → AppGw for Containers Configuration Manager on resource group
resource albConfigManagerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, albIdentity.id, roleIds.albConfigManager)
  properties: {
    roleDefinitionId: roleDefs.albConfigManager
    principalId: albIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ALB identity → Reader on resource group
resource albReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, albIdentity.id, roleIds.reader)
  properties: {
    roleDefinitionId: roleDefs.reader
    principalId: albIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource albFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2025-01-31-preview' = {
  name: 'azure-alb-identity'
  parent: albIdentity
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: albFederatedSubject
    audiences: [workloadIdentityAudience]
  }
}

// ─── Container Insights DCR (ContainerLogV2, namespace filtering) ────

var dcrContainerInsightsName = 'dcr-${dashPrefix}-ci'

resource dcrContainerInsights 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: dcrContainerInsightsName
  location: location
  kind: 'Linux'
  properties: {
    dataSources: {
      extensions: [
        {
          name: 'ContainerInsightsExtension'
          streams: ['Microsoft-ContainerLogV2']
          extensionSettings: {
            dataCollectionSettings: {
              interval: '5m'
              namespaceFilteringMode: 'Include'
              namespaces: ['ethereum']
              enableContainerLogV2: true
            }
          }
          extensionName: 'ContainerInsights'
        }
      ]
      performanceCounters: [
        {
          name: 'PerfCounterDataSource'
          streams: ['Microsoft-Perf']
          samplingFrequencyInSeconds: 300
          counterSpecifiers: [
            '\\Processor(_Total)\\% Processor Time'
            '\\Memory\\Available Bytes'
            '\\Memory\\% Used Memory'
          ]
        }
      ]
    }
    destinations: {
      logAnalytics: [
        {
          name: 'ContainerInsightsWorkspace'
          workspaceResourceId: logAnalytics.id
        }
      ]
    }
    dataFlows: [
      {
        destinations: ['ContainerInsightsWorkspace']
        streams: ['Microsoft-ContainerLogV2']
      }
      {
        destinations: ['ContainerInsightsWorkspace']
        streams: ['Microsoft-Perf']
      }
    ]
  }
}

// ─── DCR associations ────────────────────────────────────────────────

resource dceAssociation 'Microsoft.Insights/dataCollectionRuleAssociations@2024-03-11' = {
  name: 'configurationAccessEndpoint'
  scope: aks
  properties: { dataCollectionEndpointId: dce.id }
}

resource dcrAssociation 'Microsoft.Insights/dataCollectionRuleAssociations@2024-03-11' = {
  name: 'dcra-prometheus'
  scope: aks
  properties: { dataCollectionRuleId: dcrPrometheus.id }
}

resource dcrCiAssociation 'Microsoft.Insights/dataCollectionRuleAssociations@2024-03-11' = {
  name: 'dcra-container-insights'
  scope: aks
  properties: { dataCollectionRuleId: dcrContainerInsights.id }
}

// ─── Developer RBAC (isDeveloper=true) ───────────────────────────────

resource devAksAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(aks.id, developerPrincipalId, roleIds.aksRbacClusterAdmin)
  scope: aks
  properties: {
    roleDefinitionId: roleDefs.aksRbacClusterAdmin
    principalId: developerPrincipalId
    principalType: developerPrincipalType
  }
}

resource devStorageAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(storage.id, developerPrincipalId, roleIds.storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: roleDefs.storageBlobDataContributor
    principalId: developerPrincipalId
    principalType: developerPrincipalType
  }
}

resource devAcrPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(acr.id, developerPrincipalId, roleIds.acrPush)
  scope: acr
  properties: {
    roleDefinitionId: roleDefs.acrPush
    principalId: developerPrincipalId
    principalType: developerPrincipalType
  }
}

resource devMonitoring 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(logAnalytics.id, developerPrincipalId, roleIds.monitoringReader)
  scope: logAnalytics
  properties: {
    roleDefinitionId: roleDefs.monitoringReader
    principalId: developerPrincipalId
    principalType: developerPrincipalType
  }
}

// ─── AGC WAF Policy (for Application Gateway for Containers) ────
// WAF is handled directly at the AGC layer via WebApplicationFirewallPolicy CRD.
// AGC WAF is currently in Public Preview. Uses AppGW-type WAF policies (not Front Door).

var agcWafPolicyName = 'wafagc${prefix}'

resource agcWafPolicy 'Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies@2024-05-01' = {
  name: agcWafPolicyName
  location: location
  properties: {
    policySettings: {
      state: 'Enabled'
      mode: 'Prevention'
      requestBodyCheck: true
      maxRequestBodySizeInKb: 128
      fileUploadLimitInMb: 100
    }
    managedRules: {
      managedRuleSets: [
        {
          ruleSetType: 'Microsoft_DefaultRuleSet'
          ruleSetVersion: '2.1'
        }
        {
          ruleSetType: 'Microsoft_BotManagerRuleSet'
          ruleSetVersion: '1.1'
        }
      ]
    }
  }
}

// ─── Storage (RBAC-only, no keys) ────────────────────────────────────

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: publicAccess
    networkAcls: { defaultAction: networkDefault }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  name: 'default'
  parent: storage
}

resource etlContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  name: 'ethereum-etl'
  parent: blobService
  properties: { publicAccess: 'None' }
}

// Note: Storage Blob Data Contributor for appIdentity is assigned above as appIdentityStorageRole

// ─── Azure AI Foundry (Hub + Project, RBAC-only, no keys) ────────────

var aiFoundryProjectName = '${aiFoundryName}-proj'

var reasoningModels = [
  { name: 'gpt-4o-mini', model: 'gpt-4o-mini', version: '2024-07-18', sku: 'GlobalStandard', capacity: 1 }
]

resource aiFoundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: aiFoundryName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    allowProjectManagement: true
    customSubDomainName: aiFoundryName
    disableLocalAuth: true
    publicNetworkAccess: publicAccess
    networkAcls: { defaultAction: networkDefault }
  }
}

resource aiFoundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  name: aiFoundryProjectName
  parent: aiFoundry
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {}
}

@batchSize(1)
resource aiFoundryDeployments 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = [
  for model in reasoningModels: {
    name: model.name
    parent: aiFoundry
    sku: { name: model.sku, capacity: model.capacity }
    properties: {
      model: { format: 'OpenAI', name: model.model, version: model.version }
    }
  }
]

// App identity → OpenAI User (invoke models via RBAC)
resource appIdentityAIFoundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, appIdentity.id, roleIds.cognitiveServicesOpenAIUser)
  scope: aiFoundry
  properties: {
    roleDefinitionId: roleDefs.cognitiveServicesOpenAIUser
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Developer → OpenAI User
resource devAIFoundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(aiFoundry.id, developerPrincipalId, roleIds.cognitiveServicesOpenAIUser)
  scope: aiFoundry
  properties: {
    roleDefinitionId: roleDefs.cognitiveServicesOpenAIUser
    principalId: developerPrincipalId
    principalType: developerPrincipalType
  }
}

// ─── Azure Data Explorer (blockchain analytics) ──────────────────────

resource adxCluster 'Microsoft.Kusto/clusters@2024-04-13' = if (enableAdx) {
  name: adxClusterName
  location: location
  sku: {
    name: 'Dev(No SLA)_Standard_D11_v2'
    tier: 'Basic'
    capacity: 1
  }
  identity: { type: 'SystemAssigned' }
  properties: {
    enableStreamingIngest: true
    enableAutoStop: true
    enableDiskEncryption: true
    publicNetworkAccess: publicAccess
  }
}

resource adxDatabase 'Microsoft.Kusto/clusters/databases@2024-04-13' = if (enableAdx) {
  name: adxDatabaseName
  parent: adxCluster
  location: location
  kind: 'ReadWrite'
  properties: {
    hotCachePeriod: 'P7D'
    softDeletePeriod: 'P365D'
  }
}

// App identity → ADX Database Ingestor (ETL writes via managed identity)
resource appAdxIngestor 'Microsoft.Kusto/clusters/databases/principalAssignments@2024-04-13' = if (enableAdx) {
  name: 'app-ingestor'
  parent: adxDatabase
  properties: {
    principalId: appIdentity.properties.clientId
    principalType: 'App'
    role: 'Ingestor'
  }
}

// App identity → ADX Database Viewer (ETL reads progress, queries tables)
resource appAdxViewer 'Microsoft.Kusto/clusters/databases/principalAssignments@2024-04-13' = if (enableAdx) {
  name: 'app-viewer'
  parent: adxDatabase
  properties: {
    principalId: appIdentity.properties.clientId
    principalType: 'App'
    role: 'Viewer'
  }
}

// Developer → ADX Database Admin (handled in postprovision hook to avoid
// conflicts with pre-existing principal assignments that share the same role+principal)

// ─── ADX Schema (applied idempotently via .create-merge) ─────────────

resource adxSchema 'Microsoft.Kusto/clusters/databases/scripts@2024-04-13' = if (enableAdx) {
  name: 'ethereum-schema'
  parent: adxDatabase
  properties: {
    continueOnErrors: true
    forceUpdateTag: 'v2-cryo-string-types'
    scriptContent: loadTextContent('../src/etl/adx-schema.kql')
  }
}

// ─── VPN Gateway (P2S with Entra ID auth) ────────────────────────────

resource vpnPip 'Microsoft.Network/publicIPAddresses@2025-05-01' = if (privateNetworking) {
  name: vpnPipName
  location: location
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource vpnGw 'Microsoft.Network/virtualNetworkGateways@2025-05-01' = if (privateNetworking) {
  name: vpnGatewayResourceName
  location: location
  properties: {
    gatewayType: 'Vpn'
    vpnType: 'RouteBased'
    sku: { name: 'VpnGw1', tier: 'VpnGw1' }
    ipConfigurations: [
      {
        name: 'default'
        properties: {
          publicIPAddress: { id: vpnPip.id }
          subnet: { id: snetGatewayId }
        }
      }
    ]
    vpnClientConfiguration: {
      vpnClientAddressPool: { addressPrefixes: [vpnClientAddressPool] }
      vpnClientProtocols: ['OpenVPN']
      vpnAuthenticationTypes: ['AAD']
      aadTenant: vpnAadTenant
      aadAudience: vpnAadAudience
      aadIssuer: vpnAadIssuer
    }
  }
}

// ─── Private Endpoints (conditional) ─────────────────────────────────

resource storagePe 'Microsoft.Network/privateEndpoints@2025-05-01' = if (privateNetworking) {
  name: 'pe-${dashPrefix}-st'
  location: location
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'storage', properties: { privateLinkServiceId: storage.id, groupIds: ['blob'] } }
    ]
  }
}

resource acrPe 'Microsoft.Network/privateEndpoints@2025-05-01' = if (privateNetworking) {
  name: 'pe-${dashPrefix}-acr'
  location: location
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'acr', properties: { privateLinkServiceId: acr.id, groupIds: ['registry'] } }
    ]
  }
}

resource adxPe 'Microsoft.Network/privateEndpoints@2025-05-01' = if (privateNetworking && enableAdx) {
  name: 'pe-${dashPrefix}-adx'
  location: location
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'adx', properties: { privateLinkServiceId: adxCluster.id, groupIds: ['cluster'] } }
    ]
  }
}

// ─── Outputs (azd maps AZURE_ prefixed outputs to env vars) ─────────

output AZURE_AKS_CLUSTER_NAME string = aks.name
output AZURE_AKS_OIDC_ISSUER string = aks.properties.oidcIssuerProfile.issuerURL
output AZURE_CONTAINER_REGISTRY_LOGIN_SERVER string = acr.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = acr.name
output AZURE_ALB_IDENTITY_CLIENT_ID string = albIdentity.properties.clientId
output AZURE_APP_IDENTITY_CLIENT_ID string = appIdentity.properties.clientId
output AZURE_APP_IDENTITY_NAME string = appIdentity.name
output AZURE_OPENCOST_IDENTITY_CLIENT_ID string = opencostIdentity.properties.clientId
output AZURE_OPENAI_ENDPOINT string = aiFoundry.properties.endpoint
output AZURE_OPENAI_ACCOUNT_NAME string = aiFoundry.name
output AZURE_AI_FOUNDRY_PROJECT_NAME string = aiFoundryProject.name
output AZURE_VPN_GATEWAY_NAME string = privateNetworking ? vpnGw.name : ''
output AZURE_STORAGE_ACCOUNT_NAME string = storage.name
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = logAnalytics.id
output AZURE_APPLICATIONINSIGHTS_CONNECTION_STRING string = appInsights.properties.ConnectionString
output AZURE_RESOURCE_GROUP string = resourceGroup().name
output AZURE_ALB_SUBNET_ID string = snetAlbId
output AZURE_AGC_WAF_POLICY_ID string = agcWafPolicy.id
output AZURE_ADX_CLUSTER_URI string = enableAdx ? adxCluster.properties.uri : ''
output AZURE_ADX_CLUSTER_NAME string = enableAdx ? adxCluster.name : ''
output AZURE_ADX_DATABASE_NAME string = enableAdx ? adxDatabaseName : ''
output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId
output AZURE_TENANT_ID string = tenant().tenantId
