targetScope = 'resourceGroup'

@description('The existing approved 1.x resource group naming prefix.')
@allowed([
  'aw-v1-p0'
])
param namePrefix string = 'aw-v1-p0'

@description('Existing resource group ownership and cost tags.')
param tags object

@description('Exact reviewed source commit for this limited identity setup.')
@minLength(40)
@maxLength(40)
param sourceSha string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: '${namePrefix}-aks'
}

var namespace = 'agentweaver-v1-p0'
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

output runtimeIdentities array = [for (service, i) in services: {
  name: service.name
  resourceId: identities[i].id
  clientId: identities[i].properties.clientId
  principalObjectId: identities[i].properties.principalId
  namespace: namespace
  serviceAccount: service.serviceAccountName
}]

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
