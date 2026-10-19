// Narrow repair for the existing Events session-material container and grant.
// It changes no identity, federation, storage account, network, or workload.
targetScope = 'resourceGroup'

@allowed([
  'agentweaver'
])
@description('Approved v1 resource-group naming prefix.')
param namePrefix string = 'agentweaver'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: toLower(replace('${namePrefix}blob', '-', ''))
}

resource eventsAndSessionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${namePrefix}-id-events-and-sessions'
}

module sessionMaterialContainer 'modules/blob-containers.bicep' = {
  name: '${namePrefix}-session-material-container'
  params: {
    storageAccountName: storageAccount.name
    containerNames: [
      'session-material'
    ]
  }
}

module eventsSessionMaterialBlobRole 'modules/blob-container-data-contributor.bicep' = {
  name: '${namePrefix}-events-session-material-blob-role'
  params: {
    storageAccountName: storageAccount.name
    containerName: 'session-material'
    identityResourceId: eventsAndSessionsIdentity.id
    principalId: eventsAndSessionsIdentity.properties.principalId
  }
  dependsOn: [
    sessionMaterialContainer
  ]
}

output sessionMaterialContainerId string = sessionMaterialContainer.outputs.containerIds[0]
output sessionMaterialContainerUri string = '${storageAccount.properties.primaryEndpoints.blob}session-material'
output eventsAndSessionsIdentityResourceId string = eventsAndSessionsIdentity.id
output eventsSessionMaterialBlobRoleAssignmentId string = eventsSessionMaterialBlobRole.outputs.roleAssignmentId
