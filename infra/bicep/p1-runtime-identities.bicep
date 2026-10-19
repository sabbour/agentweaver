// Create the P1 runtime service identities in the existing cluster and bind
// each identity to its own Kubernetes ServiceAccount through AKS OIDC.
targetScope = 'resourceGroup'

@description('The existing approved 1.x resource group naming prefix.')
@allowed([
  'agentweaver'
])
param namePrefix string = 'agentweaver'

@description('Existing resource group ownership and cost tags.')
param tags object

@description('Exact reviewed source commit for this limited identity setup.')
@minLength(40)
@maxLength(40)
param sourceSha string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: '${namePrefix}-aks'
}

// Keep service identity names and subjects aligned so federation remains
// scoped to the exact namespace and workload ServiceAccount.
var namespace = 'agentweaver'
var serviceNames = [
  'projects-config'
  'environment'
  'knowledge'
  'events-and-sessions'
  'orchestrator'
]
var services = [for name in serviceNames: {
  name: name
  namespace: namespace
  serviceAccountName: name
}]
var eventsAndSessionsIndex = indexOf(serviceNames, 'events-and-sessions')

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [
  for service in services: {
    name: '${namePrefix}-id-${service.name}'
    location: resourceGroup().location
    tags: union(tags, {
      service: service.name
      'agentweaver:source-sha': sourceSha
    })
  }
]

module federatedCredentials 'modules/workload-identity-federation.bicep' = {
  name: '${namePrefix}-p1-runtime-federation'
  params: {
    namePrefix: namePrefix
    oidcIssuerUrl: aks.properties.oidcIssuerProfile.issuerURL
    services: services
  }
  dependsOn: [
    identities
  ]
}

// Events stores typed session material in this container and needs no account-wide Blob role.
module eventsSessionMaterialBlobRole 'modules/blob-container-data-contributor.bicep' = {
  name: '${namePrefix}-events-session-material-blob-role'
  params: {
    storageAccountName: toLower(replace('${namePrefix}blob', '-', ''))
    containerName: 'session-material'
    identityResourceId: identities[eventsAndSessionsIndex].id
    principalId: identities[eventsAndSessionsIndex].properties.principalId
  }
  dependsOn: [
    identities
  ]
}

output runtimeIdentities array = [for (service, i) in services: {
  name: service.name
  resourceId: identities[i].id
  clientId: identities[i].properties.clientId
  principalObjectId: identities[i].properties.principalId
  namespace: namespace
  serviceAccount: service.serviceAccountName
}]

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output eventsSessionMaterialBlobRoleAssignmentId string = eventsSessionMaterialBlobRole.outputs.roleAssignmentId
