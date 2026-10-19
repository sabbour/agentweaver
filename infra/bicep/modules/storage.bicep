// Dedicated storage account for the P0 Blob Object Store foundation.
// Public network access is disabled; the only reachability path is the
// Private Endpoint below (any networkAcls VNet rule would be a no-op
// while publicNetworkAccess is Disabled, so this module does not declare
// one). TLS 1.2 is the floor, and shared-key access stays disabled (no
// anonymous access, no public blob access).
@description('Azure region for the storage account.')
param location string

@description('Dedicated naming prefix, e.g. agentweaver. Storage account names strip hyphens and lowercase automatically.')
param namePrefix string

@description('Resource tags applied to the storage account.')
param tags object

@description('Subnet resource ID the Blob private endpoint is attached to.')
param privateEndpointsSubnetId string

@description('Resource ID of the privatelink.blob.core.windows.net private DNS zone, linked to the dedicated VNet by the caller.')
param privateDnsZoneId string

var containerNames = [
  'platform-artifacts'
  'session-material'
]

var storageAccountName = toLower(replace('${namePrefix}blob', '-', ''))

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: take(storageAccountName, 24)
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storage
  name: 'default'
}

module containers 'blob-containers.bicep' = {
  name: '${namePrefix}-blob-containers'
  params: {
    storageAccountName: storage.name
    containerNames: containerNames
  }
  dependsOn: [
    blobService
  ]
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name: '${namePrefix}-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${namePrefix}-blob-pls'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: [
            'blob'
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
        name: 'blob'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output storageAccountId string = storage.id
output storageAccountName string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output platformArtifactsContainerId string = resourceId(
  'Microsoft.Storage/storageAccounts/blobServices/containers',
  storage.name,
  'default',
  'platform-artifacts'
)
output sessionMaterialContainerId string = resourceId(
  'Microsoft.Storage/storageAccounts/blobServices/containers',
  storage.name,
  'default',
  'session-material'
)
output sessionMaterialContainerUri string = '${storage.properties.primaryEndpoints.blob}session-material'
