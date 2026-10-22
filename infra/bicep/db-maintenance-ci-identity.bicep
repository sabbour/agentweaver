// Create the identity that the v1 database maintenance workflow signs in as, and give it only
// what a routine run needs. GitHub Actions gets a token for it through OIDC, and only for jobs that
// use the v1-db-maintenance environment. It can read the resource group, get cluster credentials,
// and work with Jobs, Pods, ConfigMaps, Leases, ServiceAccounts, network policies, and the two
// service Deployments in the agentweaver namespace. It cannot deploy Azure resources, change role
// assignments, or reach PostgreSQL itself. The one-time setup that needs those rights is run by a
// person with npm run db:maintenance:setup.
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

var namespace = 'agentweaver'
// Built-in role IDs. Reader lets the runner read identities, federated credentials, and the
// cluster. Cluster User lets it fetch a kubeconfig; the Kubernetes rights come from the role below.
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

// Kubernetes rights, as Azure RBAC data actions, matching the runner's permission preflight.
// The cluster uses Azure RBAC for Kubernetes and has no local accounts, so this is the only grant.
// Custom resources are limited to this namespace by the assignment scope; the runner only reads
// and applies the two migration CiliumNetworkPolicies.
resource maintenanceRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'agentweaver-db-maintenance-kubernetes')
  properties: {
    roleName: 'Agentweaver DB Maintenance Kubernetes (${resourceGroup().name})'
    description: 'Runs the v1 database maintenance Jobs and rolls the two service Deployments in one namespace.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.ContainerService/managedClusters/batch/jobs/read'
          'Microsoft.ContainerService/managedClusters/batch/jobs/write'
          'Microsoft.ContainerService/managedClusters/batch/jobs/delete'
          'Microsoft.ContainerService/managedClusters/pods/read'
          'Microsoft.ContainerService/managedClusters/configmaps/read'
          'Microsoft.ContainerService/managedClusters/configmaps/write'
          'Microsoft.ContainerService/managedClusters/coordination.k8s.io/leases/read'
          'Microsoft.ContainerService/managedClusters/coordination.k8s.io/leases/write'
          'Microsoft.ContainerService/managedClusters/coordination.k8s.io/leases/delete'
          'Microsoft.ContainerService/managedClusters/serviceaccounts/read'
          'Microsoft.ContainerService/managedClusters/serviceaccounts/write'
          'Microsoft.ContainerService/managedClusters/customresources/read'
          'Microsoft.ContainerService/managedClusters/customresources/write'
          'Microsoft.ContainerService/managedClusters/apps/deployments/read'
          'Microsoft.ContainerService/managedClusters/apps/deployments/write'
        ]
        notDataActions: []
      }
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

// A namespace scope is an AKS-supported role assignment scope below the cluster resource. The
// assignment is deployed through a nested resource ID because Bicep has no symbolic namespace.
module namespaceAssignment 'modules/aks-namespace-role-assignment.bicep' = {
  name: '${namePrefix}-db-maintenance-namespace-role'
  params: {
    clusterName: aks.name
    namespace: namespace
    principalId: identity.properties.principalId
    roleDefinitionId: maintenanceRole.id
  }
}

output maintenanceIdentity object = {
  name: identity.name
  clientId: identity.properties.clientId
  principalObjectId: identity.properties.principalId
  githubSubject: githubCredential.properties.subject
  kubernetesRoleDefinitionId: maintenanceRole.id
  namespaceScope: '${aks.id}/namespaces/${namespace}'
}
