// Create the dedicated P1 PostgreSQL migration identity in the existing cluster and
// bind it to one Kubernetes ServiceAccount through AKS OIDC. Core and Events migration
// Jobs use it as the schema owner. It gets no Azure role; its only authority is the
// PostgreSQL role that the ownership handover creates for the same object ID.
targetScope = 'resourceGroup'

@description('The existing approved 1.x resource group naming prefix.')
@allowed([
  'agentweaver'
])
param namePrefix string = 'agentweaver'

@description('Existing resource group ownership and cost tags.')
param tags object

@description('Exact reviewed source commit for this identity setup.')
@minLength(40)
@maxLength(40)
param sourceSha string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: '${namePrefix}-aks'
}

// The name, namespace, and ServiceAccount must match deploy/k8s/migrations/p1-postgres.
var namespace = 'agentweaver'
var migration = {
  name: 'p1-postgres-migration'
  namespace: namespace
  serviceAccountName: 'p1-postgres-migration'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-id-${migration.name}'
  location: resourceGroup().location
  tags: union(tags, {
    service: migration.name
    'agentweaver:source-sha': sourceSha
  })
}

module federatedCredential 'modules/workload-identity-federation.bicep' = {
  name: '${namePrefix}-p1-postgres-migration-federation'
  params: {
    namePrefix: namePrefix
    oidcIssuerUrl: aks.properties.oidcIssuerProfile.issuerURL
    services: [
      migration
    ]
  }
  dependsOn: [
    identity
  ]
}

output migrationIdentity object = {
  name: identity.name
  resourceId: identity.id
  clientId: identity.properties.clientId
  principalObjectId: identity.properties.principalId
  namespace: namespace
  serviceAccount: migration.serviceAccountName
}

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
