// Least-privilege user-assigned managed identities with AKS workload-
// identity federation (no client secret or certificate). This currently
// provisions a single `foundation-probe` identity used only by
// the separately owned #1784 AKS Job, not the operator CLI (see
// the `services` param description below). Once a real service lands,
// it gets its own entry granted only the RBAC roles it actually needs,
// scoped to the exact resource rather than the resource group or
// subscription.
@description('Azure region for the identities.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0.')
param namePrefix string

@description('Resource tags applied to every identity.')
param tags object

@description('AKS OIDC issuer URL (from the aks module output). Federated credentials trust only this issuer.')
param oidcIssuerUrl string

@description('Key Vault resource ID identities may be granted secrets-user access to.')
param keyVaultId string

@description('Storage account resource ID identities may be granted blob-data access to.')
param storageAccountId string

@description('Exact Log Analytics workspace for foundation-probe queries.')
param monitorWorkspaceResourceId string

@description('Exact Application Insights resource for authenticated foundation-probe ingestion.')
param appInsightsResourceId string

@description('''
The foundation-probe principal is reserved for the #1784 acceptance-only
AKS Job. The operator CLI does not run as that principal. Identity is the
product host. Relay is a library and release is a CLI, not runtime services.
Future product hosts add only identities for the permissions they need.
''')
param services array = [
  {
    name: 'foundation-probe'
    namespace: 'agentweaver-v1-p0'
    serviceAccountName: 'foundation-probe'
    needsKeyVault: true
    needsBlob: true
    needsMonitor: true
  }
]

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var logAnalyticsReaderRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [
  for service in services: {
    name: '${namePrefix}-id-${service.name}'
    location: location
    tags: union(tags, { service: service.name })
  }
]

resource federatedCredentials 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = [
  for (service, i) in services: {
    parent: identities[i]
    name: '${service.name}-workload-identity'
    properties: {
      issuer: oidcIssuerUrl
      subject: 'system:serviceaccount:${service.namespace}:${service.serviceAccountName}'
      audiences: [
        'api://AzureADTokenExchange'
      ]
    }
  }
]

resource existingKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: last(split(keyVaultId, '/'))
}

resource existingStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: last(split(storageAccountId, '/'))
}

resource existingWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(monitorWorkspaceResourceId, '/'))
}

resource existingAppInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: last(split(appInsightsResourceId, '/'))
}

// Scoped to the exact vault resource (not the resource group), so a
// service identity can only ever read secrets from this one vault.
resource keyVaultRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (service, i) in services: if (service.needsKeyVault) {
    name: guid(keyVaultId, identities[i].id, keyVaultSecretsUserRoleId)
    scope: existingKeyVault
    properties: {
      principalId: identities[i].properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    }
  }
]

// Scoped to the exact storage account resource for the same reason.
resource blobRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (service, i) in services: if (service.needsBlob) {
    name: guid(storageAccountId, identities[i].id, storageBlobDataContributorRoleId)
    scope: existingStorageAccount
    properties: {
      principalId: identities[i].properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    }
  }
]

output identityClientIds array = [for i in range(0, length(services)): identities[i].properties.clientId]
output identityNames array = [for i in range(0, length(services)): identities[i].name]
output identityPrincipalIds array = [for i in range(0, length(services)): identities[i].properties.principalId]

resource monitorQueryRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (service, i) in services: if (service.needsMonitor) {
    name: guid(monitorWorkspaceResourceId, identities[i].id, logAnalyticsReaderRoleId)
    scope: existingWorkspace
    properties: {
      principalId: identities[i].properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReaderRoleId)
    }
  }
]

resource monitorIngestionRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (service, i) in services: if (service.needsMonitor) {
    name: guid(appInsightsResourceId, identities[i].id, monitoringMetricsPublisherRoleId)
    scope: existingAppInsights
    properties: {
      principalId: identities[i].properties.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisherRoleId)
    }
  }
]

var serviceNames = [for service in services: service.name]
var probeIndex = indexOf(serviceNames, 'foundation-probe')

output foundationProbeIdentity object = {
  name: services[probeIndex].name
  resourceId: identities[probeIndex].id
  clientId: identities[probeIndex].properties.clientId
  principalObjectId: identities[probeIndex].properties.principalId
  namespace: services[probeIndex].namespace
  serviceAccount: services[probeIndex].serviceAccountName
}
