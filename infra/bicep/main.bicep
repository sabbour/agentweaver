// P0 dedicated Azure integration environment for Agentweaver 1.0.
//
// This template DEFINES infrastructure only. It is validated with
// `az deployment group validate` / `what-if` (read-only) by
// scripts/azure/plan.mjs. Nothing in this repository executes a live
// deployment; that requires an explicit, human-confirmed target and the
// `scripts/azure/deploy.mjs --execute` path, which this phase does not run.
//
// Deploy at resource-group scope into a resource group dedicated to this
// environment (never a shared 0.x resource group). namePrefix must be
// unique to this environment; scripts/azure validates this before any plan
// or deploy call is attempted.
targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@minLength(6)
@maxLength(12)
@description('Dedicated naming prefix for this environment, e.g. aw-v1-p0. Must be unique per environment and never reused from a 0.x deployment.')
param namePrefix string

@description('Microsoft Entra tenant ID that owns Key Vault and the PostgreSQL administrator.')
param tenantId string = subscription().tenantId

@description('Microsoft Entra administrator object ID (group recommended) for PostgreSQL.')
param postgresEntraAdminObjectId string

@description('Microsoft Entra administrator principal name (user or group display name) for PostgreSQL.')
param postgresEntraAdminPrincipalName string

@description('Owning team or individual, recorded as a tag for cost and incident routing.')
param owner string

@description('Cost center or budget code, recorded as a tag.')
param costCenter string

@minLength(40)
@maxLength(40)
@description('Exact reviewed source commit supplied by deployment tooling.')
param sourceSha string

@minLength(40)
@maxLength(40)
@description('Git tree identity for the exact reviewed source commit. Not the infrastructure input hash.')
param sourceTree string

@minLength(64)
@maxLength(64)
@description('SHA-256 receipt of tracked infrastructure inputs supplied by deployment tooling.')
param sourceHash string

@maxLength(5)
@description('Optional IDs of existing application DNS zones for custom routing domains.')
param appRoutingDnsZoneResourceIds array = []

var tags = {
  'agentweaver:environment': 'v1-p0'
  'agentweaver:managed-by': 'bicep'
  'agentweaver:owner': owner
  'agentweaver:cost-center': costCenter
  'agentweaver:sourceSha': sourceSha
}

module network 'modules/network.bicep' = {
  name: '${namePrefix}-network'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module aks 'modules/aks.bicep' = {
  name: '${namePrefix}-aks'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    nodeSubnetId: network.outputs.aksSubnetId
    tenantId: tenantId
    appRoutingDnsZoneResourceIds: appRoutingDnsZoneResourceIds
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: '${namePrefix}-keyvault'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    tenantId: tenantId
    privateEndpointsSubnetId: network.outputs.privateEndpointsSubnetId
    privateDnsZoneId: keyVaultPrivateDnsZone.id
  }
}

module storage 'modules/storage.bicep' = {
  name: '${namePrefix}-storage'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    privateEndpointsSubnetId: network.outputs.privateEndpointsSubnetId
    privateDnsZoneId: blobPrivateDnsZone.id
  }
}

module monitor 'modules/monitor.bicep' = {
  name: '${namePrefix}-monitor'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    privateEndpointsSubnetId: network.outputs.privateEndpointsSubnetId
    monitorPrivateDnsZoneId: monitorPrivateDnsZone.id
    omsPrivateDnsZoneId: omsPrivateDnsZone.id
    odsPrivateDnsZoneId: odsPrivateDnsZone.id
    agentSvcPrivateDnsZoneId: agentSvcPrivateDnsZone.id
    blobPrivateDnsZoneId: blobPrivateDnsZone.id
  }
}

// Key Vault, Blob, and Azure Monitor (logs/query) private endpoints all
// resolve through private DNS zones linked to the dedicated VNet. Declared
// here (rather than inside each module) for the same reason as the
// PostgreSQL zone below: the zone must be linked to the VNet the module's
// caller already owns, before the module's private endpoint can resolve
// through it.
resource keyVaultPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource keyVaultPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: keyVaultPrivateDnsZone
  name: '${namePrefix}-kv-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.core.windows.net'
  location: 'global'
  tags: tags
}

resource blobPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: blobPrivateDnsZone
  name: '${namePrefix}-blob-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

// The five documented AMPLS DNS zones share this dedicated VNet.
resource monitorPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.monitor.azure.com'
  location: 'global'
  tags: tags
}

resource monitorPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: monitorPrivateDnsZone
  name: '${namePrefix}-monitor-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

resource omsPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.oms.opinsights.azure.com'
  location: 'global'
  tags: tags
}

resource omsPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: omsPrivateDnsZone
  name: '${namePrefix}-oms-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

resource odsPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.ods.opinsights.azure.com'
  location: 'global'
  tags: tags
}

resource odsPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: odsPrivateDnsZone
  name: '${namePrefix}-ods-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }

  }
}

resource agentSvcPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.agentsvc.azure-automation.net'
  location: 'global'
  tags: tags
}

resource agentSvcPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: agentSvcPrivateDnsZone
  name: '${namePrefix}-agentsvc-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

// PostgreSQL requires a private DNS zone linked to the dedicated VNet.
// Declared here (rather than inside modules/postgres.bicep) because the
// zone must be linked to the same VNet the module's caller already owns.
resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresPrivateDnsZone
  name: '${namePrefix}-pg-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.outputs.vnetId
    }
  }
}

module postgres 'modules/postgres.bicep' = {
  name: '${namePrefix}-postgres'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    delegatedSubnetId: network.outputs.postgresSubnetId
    privateDnsZoneId: postgresPrivateDnsZone.id
    entraAdminObjectId: postgresEntraAdminObjectId
    entraAdminPrincipalName: postgresEntraAdminPrincipalName
  }
}

module identity 'modules/identity.bicep' = {
  name: '${namePrefix}-identity'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
    appRoutingIdentityObjectId: aks.outputs.appRoutingIdentity.objectId
    aksClusterId: aks.outputs.clusterId
    keyVaultId: keyVault.outputs.vaultId
    storageAccountId: storage.outputs.storageAccountId
    monitorWorkspaceResourceId: monitor.outputs.workspaceId
    appInsightsResourceId: monitor.outputs.appInsightsId
  }
}

module appRoutingDnsRoles 'modules/app-routing-dns.bicep' = [for (zoneId, i) in appRoutingDnsZoneResourceIds: {
  name: '${namePrefix}-app-routing-dns-${i}'
  scope: resourceGroup(subscription().subscriptionId, split(zoneId, '/')[4])
  params: {
    zoneResourceId: zoneId
    zoneIsPrivate: toLower(split(zoneId, '/')[7]) == 'privatednszones'
    aksClusterId: aks.outputs.clusterId
    appRoutingIdentityObjectId: aks.outputs.appRoutingIdentity.objectId
  }
}]

output aksClusterName string = aks.outputs.clusterName
output aksControlPlanePrincipalId string = aks.outputs.controlPlanePrincipalId
output aksOidcIssuerUrl string = aks.outputs.oidcIssuerUrl
output appRoutingIdentity object = aks.outputs.appRoutingIdentity
output appRoutingDomain object = aks.outputs.appRoutingDomain
output keyVaultName string = keyVault.outputs.vaultName
output storageAccountName string = storage.outputs.storageAccountName
output postgresServerName string = postgres.outputs.serverName
output monitorWorkspaceId string = monitor.outputs.workspaceCustomerId
output serviceIdentityClientIds array = identity.outputs.identityClientIds
output serviceIdentityPrincipalIds array = identity.outputs.identityPrincipalIds
output sourceSha string = sourceSha
output sourceHash string = sourceHash
output sourceTree string = sourceTree
output foundationProbeIdentity object = identity.outputs.foundationProbeIdentity
output foundationResources object = {
  clusterId: aks.outputs.clusterId
  keyVaultId: keyVault.outputs.vaultId
  vaultUri: keyVault.outputs.vaultUri
  storageAccountId: storage.outputs.storageAccountId
  blobContainerId: storage.outputs.platformArtifactsContainerId
  blobContainerUri: '${storage.outputs.blobEndpoint}platform-artifacts'
  postgresServerId: postgres.outputs.serverId
  postgresHost: postgres.outputs.fullyQualifiedDomainName
  monitorWorkspaceResourceId: monitor.outputs.workspaceId
  monitorWorkspaceId: monitor.outputs.workspaceCustomerId
  appInsightsResourceId: monitor.outputs.appInsightsId
}
