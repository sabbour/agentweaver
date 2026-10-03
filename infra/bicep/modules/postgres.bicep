// Dedicated, private PostgreSQL Flexible Server for the P0 persistence
// foundation (Agentweaver.Persistence.Postgres). VNet-integrated (no public
// endpoint); Microsoft Entra authentication only, so no server admin
// password exists for callers to leak or hardcode.
@description('Azure region for the server.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0.')
param namePrefix string

@description('Resource tags applied to the server.')
param tags object

@description('Delegated subnet resource ID for the flexible server.')
param delegatedSubnetId string

@description('Private DNS zone resource ID for privatelink.postgres.database.azure.com, linked to the dedicated VNet.')
param privateDnsZoneId string

@description('Microsoft Entra administrator object ID (group recommended) for the server.')
param entraAdminObjectId string

@description('Microsoft Entra administrator principal name (user or group display name).')
param entraAdminPrincipalName string

@description('Compute SKU for the P0 smoke environment.')
param skuName string = 'Standard_B2s'

@description('Storage size in GiB.')
param storageSizeGB int = 32

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview' = {
  name: '${namePrefix}-pg'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    network: {
      delegatedSubnetResourceId: delegatedSubnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
    }
    storage: {
      storageSizeGB: storageSizeGB
    }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
  }
}

resource entraAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2023-06-01-preview' = {
  parent: server
  name: entraAdminObjectId
  properties: {
    principalType: 'Group'
    principalName: entraAdminPrincipalName
    tenantId: subscription().tenantId
  }
}

output serverId string = server.id
output serverName string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
