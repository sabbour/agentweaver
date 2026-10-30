// Create the identity that the v1 database maintenance workflow signs in as, and give it only
// what a routine run needs. GitHub Actions gets a token for it through OIDC, and only for jobs that
// use the v1-db-maintenance environment. It can read the resource group and get cluster credentials.
// The setup script applies a narrow Kubernetes Role and RoleBinding for maintenance objects in the
// agentweaver namespace. This template creates no Azure custom role definition. The identity cannot
// deploy Azure resources, change role assignments, or reach PostgreSQL itself.
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

@description('GitHub repository, as owner/name, whose workflow may sign in.')
param githubRepository string = 'sabbour/agentweaver'

@description('GitHub environment that the database maintenance workflow uses.')
param githubEnvironment string = 'v1-db-maintenance'

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: '${namePrefix}-aks'
}

// Built-in role IDs. Reader lets the runner read identities and the resource group. Cluster User
// lets it fetch a kubeconfig; the setup script grants Kubernetes object access with native RBAC.
var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var clusterUserRoleId = '4abbcc35-e782-43d8-92c5-2d3f1bd2253f'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-id-db-maintenance'
  location: resourceGroup().location
  tags: union(tags, {
    service: 'db-maintenance'
    'agentweaver:source-sha': sourceSha
  })
}

// Only a job that names the environment gets a token. Branch rules on the environment then limit
// it to v1, so a pull request or another branch cannot use this identity.
resource githubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-${githubEnvironment}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:${githubEnvironment}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource projectsConfigMigrationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-id-projects-config-migration'
  location: resourceGroup().location
  tags: union(tags, {
    service: 'projects-config-migration'
    'agentweaver:source-sha': sourceSha
  })
}

resource projectsConfigMigrationCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: projectsConfigMigrationIdentity
  name: 'projects-config-migration-workload-identity'
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:agentweaver:projects-config-migration'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource readerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource clusterUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, identity.id, clusterUserRoleId)
  scope: aks
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', clusterUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output maintenanceIdentity object = {
  name: identity.name
  clientId: identity.properties.clientId
  principalObjectId: identity.properties.principalId
  githubSubject: githubCredential.properties.subject
}

output projectsConfigMigrationIdentity object = {
  name: projectsConfigMigrationIdentity.name
  clientId: projectsConfigMigrationIdentity.properties.clientId
  principalObjectId: projectsConfigMigrationIdentity.properties.principalId
  serviceAccount: 'projects-config-migration'
  oidcSubject: projectsConfigMigrationCredential.properties.subject
}
