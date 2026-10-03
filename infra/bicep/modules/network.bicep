// Dedicated virtual network for the v1 P0 Azure integration environment.
// Subnets are delegated/scoped per consumer so AKS, PostgreSQL, and private
// endpoints never share an address space with any 0.x environment.
@description('Azure region for all resources in this module.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0. Must not collide with any 0.x resource name.')
param namePrefix string

@description('Resource tags applied to every resource in this module.')
param tags object

@description('Address space for the dedicated virtual network.')
param vnetAddressPrefix string = '10.90.0.0/16'

@description('Address prefix for the AKS node subnet.')
param aksSubnetAddressPrefix string = '10.90.0.0/19'

@description('Address prefix for the PostgreSQL delegated subnet.')
param postgresSubnetAddressPrefix string = '10.90.32.0/24'

@description('Address prefix for the private endpoints subnet (Key Vault, Blob, Monitor).')
param privateEndpointsSubnetAddressPrefix string = '10.90.33.0/24'

resource vnet 'Microsoft.Network/virtualNetworks@2023-09-01' = {
  name: '${namePrefix}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'aks'
        properties: {
          addressPrefix: aksSubnetAddressPrefix
        }
      }
      {
        name: 'postgres'
        properties: {
          addressPrefix: postgresSubnetAddressPrefix
          delegations: [
            {
              name: 'postgresFlexibleServers'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointsSubnetAddressPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

output vnetId string = vnet.id
output aksSubnetId string = vnet.properties.subnets[0].id
output postgresSubnetId string = vnet.properties.subnets[1].id
output privateEndpointsSubnetId string = vnet.properties.subnets[2].id
