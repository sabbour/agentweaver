// Grant one workload identity data access to one existing private Blob container.
@description('Name of the existing storage account.')
param storageAccountName string

@description('Exact Blob container that this identity may read and write.')
param containerName string

@description('Resource ID of the user-assigned managed identity.')
param identityResourceId string

@description('Principal object ID of the user-assigned managed identity.')
param principalId string

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' existing = {
  parent: blobService
  name: containerName
}

// Scope the data role to this container; the workload does not need account-wide access.
resource blobContainerDataContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(container.id, identityResourceId, storageBlobDataContributorRoleId)
  scope: container
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
  }
}

output roleAssignmentId string = blobContainerDataContributorRoleAssignment.id
