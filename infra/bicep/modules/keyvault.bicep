// Dedicated, private Key Vault for the v1 P0 foundation. RBAC-only
// authorization (no legacy access policies), soft delete and purge
// protection, and no public network access. Network reachability comes
// exclusively from the Private Endpoint below: publicNetworkAccess is
// Disabled, which makes any networkAcls VNet rule a no-op, so this module
// does not declare one.
@description('Azure region for the vault.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0.')
param namePrefix string

@description('Resource tags applied to the vault.')
param tags object

@description('Tenant ID that owns the vault.')
param tenantId string

@description('Subnet resource ID the Key Vault private endpoint is attached to.')
param privateEndpointsSubnetId string

@description('Resource ID of the privatelink.vaultcore.azure.net private DNS zone, linked to the dedicated VNet by the caller.')
param privateDnsZoneId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}-kv'
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name: '${namePrefix}-kv-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${namePrefix}-kv-pls'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-09-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vaultcore'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output vaultId string = vault.id
output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
