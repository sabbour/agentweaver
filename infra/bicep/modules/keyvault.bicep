// Dedicated private Key Vault with RBAC-only authorization, soft delete,
// purge protection, and no public network access. Network reachability comes
// exclusively from the Private Endpoint below. An optional Secrets Officer
// assignment is scoped to this vault.
@description('Azure region for the vault.')
param location string

@description('Dedicated naming prefix, e.g. agentweaver.')
param namePrefix string

@description('Globally unique name for the dedicated Key Vault.')
param keyVaultName string

@description('Resource tags applied to the vault.')
param tags object

@description('Tenant ID that owns the vault.')
param tenantId string

@description('Subnet resource ID the Key Vault private endpoint is attached to.')
param privateEndpointsSubnetId string

@description('Resource ID of the privatelink.vaultcore.azure.net private DNS zone, linked to the dedicated VNet by the caller.')
param privateDnsZoneId string

@description('Optional workload identity principal that receives Key Vault Secrets Officer on this vault.')
param officerPrincipalId string = ''

var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
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

resource secretsOfficerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(officerPrincipalId)) {
  name: guid(vault.id, officerPrincipalId, keyVaultSecretsOfficerRoleId)
  scope: vault
  properties: {
    principalId: officerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
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
