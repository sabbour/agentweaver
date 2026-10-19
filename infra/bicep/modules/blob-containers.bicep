// Create private Blob containers on an existing storage account.
@description('Name of the existing storage account.')
param storageAccountName string

@description('Blob containers to create. Every container is private.')
param containerNames array

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

// Keep platform data private; anonymous and public container access is disabled.
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = [
  for containerName in containerNames: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

output containerIds array = [for i in range(0, length(containerNames)): containers[i].id]
