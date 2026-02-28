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
var logAnalyticsName = 'log-${dashPrefix}'
var monitorWorkspaceName = 'mon-${dashPrefix}'
var appInsightsName = 'appi-${dashPrefix}'
var dceName = 'dce-${dashPrefix}'
var dcrPrometheusName = 'dcr-${dashPrefix}-prom'
var nsgName = 'nsg-${dashPrefix}'
var vnetName = 'vnet-${dashPrefix}'
var acrName = 'acr${prefix}'
var aksClusterName = 'aks-${dashPrefix}'
var openaiAccountName = 'oai-${dashPrefix}'
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

resource nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: nsgName
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
          networkSecurityGroup: { id: nsg.id }
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
  sku: { name: 'Base', tier: 'Standard' }
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
        name: 'system'
        count: 2
        vmSize: 'Standard_DS2_v2'
        mode: 'System'
        osType: 'Linux'
        osSKU: 'AzureLinux'
        vnetSubnetID: snetAksId
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

// ─── Prometheus DCR associations ─────────────────────────────────────

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

// ─── Azure OpenAI (reasoning models, RBAC-only, no keys) ─────────────

var reasoningModels = [
  { name: 'o4-mini', model: 'o4-mini', version: '2025-04-16', sku: 'Standard', capacity: 10 }
  { name: 'o3-mini', model: 'o3-mini', version: '2025-01-31', sku: 'GlobalStandard', capacity: 10 }
  { name: 'gpt-4-1', model: 'gpt-4.1', version: '2025-04-14', sku: 'Standard', capacity: 10 }
]

resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openaiAccountName
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: openaiAccountName
    disableLocalAuth: true
    publicNetworkAccess: publicAccess
    networkAcls: { defaultAction: networkDefault }
  }
}

@batchSize(1)
resource openaiDeployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [
  for model in reasoningModels: {
    name: model.name
    parent: openai
    sku: { name: model.sku, capacity: model.capacity }
    properties: {
      model: { format: 'OpenAI', name: model.model, version: model.version }
    }
  }
]

// App identity → OpenAI User (invoke models via RBAC)
resource appIdentityOpenAIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, appIdentity.id, roleIds.cognitiveServicesOpenAIUser)
  scope: openai
  properties: {
    roleDefinitionId: roleDefs.cognitiveServicesOpenAIUser
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Developer → OpenAI User
resource devOpenAIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasDeveloper) {
  name: guid(openai.id, developerPrincipalId, roleIds.cognitiveServicesOpenAIUser)
  scope: openai
  properties: {
    roleDefinitionId: roleDefs.cognitiveServicesOpenAIUser
    principalId: developerPrincipalId
    principalType: developerPrincipalType
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

// ─── Outputs (azd maps AZURE_ prefixed outputs to env vars) ─────────

output AZURE_AKS_CLUSTER_NAME string = aks.name
output AZURE_AKS_OIDC_ISSUER string = aks.properties.oidcIssuerProfile.issuerURL
output AZURE_CONTAINER_REGISTRY_LOGIN_SERVER string = acr.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = acr.name
output AZURE_ALB_IDENTITY_CLIENT_ID string = albIdentity.properties.clientId
output AZURE_APP_IDENTITY_CLIENT_ID string = appIdentity.properties.clientId
output AZURE_APP_IDENTITY_NAME string = appIdentity.name
output AZURE_OPENAI_ENDPOINT string = openai.properties.endpoint
output AZURE_OPENAI_ACCOUNT_NAME string = openai.name
output AZURE_VPN_GATEWAY_NAME string = privateNetworking ? vpnGw.name : ''
output AZURE_STORAGE_ACCOUNT_NAME string = storage.name
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = logAnalytics.id
output AZURE_APPLICATIONINSIGHTS_CONNECTION_STRING string = appInsights.properties.ConnectionString
output AZURE_RESOURCE_GROUP string = resourceGroup().name
output AZURE_ALB_SUBNET_ID string = snetAlbId
output AZURE_AGC_WAF_POLICY_ID string = agcWafPolicy.id
