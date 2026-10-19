// Existing-cluster path for an approved P0 foundation. It refreshes workload
// identity federation without PUTting the AKS resource or its managed fields.
targetScope = 'resourceGroup'

@description('Azure region for the existing P0 network and AKS cluster.')
param location string

@description('Dedicated P0 naming prefix.')
param namePrefix string

@description('Microsoft Entra tenant used by the AKS cluster.')
param tenantId string

@description('Resource owner tag from the dedicated resource group.')
param owner string

@description('Cost center tag from the dedicated resource group.')
param costCenter string

@minLength(40)
@maxLength(40)
@description('Exact reviewed source commit supplied by deployment tooling.')
param sourceSha string

@minLength(40)
@maxLength(40)
@description('Git tree identity for the exact reviewed source commit.')
param sourceTree string

@minLength(64)
@maxLength(64)
@description('SHA-256 receipt of tracked infrastructure inputs supplied by deployment tooling.')
param sourceHash string

// AKS owns generated fields such as defaultDomain.domainName; read the cluster
// instead of redeploying a partial managedClusters resource.
resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: '${namePrefix}-aks'
}

module workloadIdentityFederation 'modules/workload-identity-federation.bicep' = {
  name: '${namePrefix}-workload-identity-federation'
  params: {
    namePrefix: namePrefix
    oidcIssuerUrl: aks.properties.oidcIssuerProfile.issuerURL
  }
}

// Custom DNS clusters can omit defaultDomain, so keep this diagnostic output optional.
var defaultDomain = aks.properties.?ingressProfile.?webAppRouting.?defaultDomain

output sourceSha string = sourceSha
output sourceTree string = sourceTree
output sourceHash string = sourceHash
output clusterId string = aks.id
output clusterName string = aks.name
output controlPlanePrincipalId string = aks.identity.principalId
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output appRoutingDomain object = {
  managedDefaultRequested: defaultDomain.?enabled ?? false
  domainName: defaultDomain.?domainName ?? null
}
