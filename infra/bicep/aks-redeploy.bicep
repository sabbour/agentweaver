// Dedicated cluster update path for an existing P0 foundation. This template
// does not redeploy the VNet, data services, private DNS, or monitoring.
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

@description('Microsoft Entra object ID of the approved Kubernetes cluster administrator.')
param operatorObjectId string

@description('Exact role-assignment name resolved by guarded tooling for the approved operator.')
param operatorRoleAssignmentName string

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

@maxLength(5)
@description('Optional existing application DNS zones for custom routing domains.')
param appRoutingDnsZoneResourceIds array = []

@allowed([2, 3])
param nodePoolCount int = 2

@allowed([2])
param nodePoolMinCount int

@allowed([3])
param nodePoolMaxCount int

var tags = {
  'agentweaver:environment': 'v1-p0'
  'agentweaver:managed-by': 'bicep'
  'agentweaver:owner': owner
  'agentweaver:cost-center': costCenter
  'agentweaver:sourceSha': sourceSha
}

resource existingVnet 'Microsoft.Network/virtualNetworks@2023-09-01' existing = {
  name: '${namePrefix}-vnet'
}

resource existingAksSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-09-01' existing = {
  parent: existingVnet
  name: 'aks'
}

module aks 'modules/aks.bicep' = {
  name: '${namePrefix}-aks'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    nodeSubnetId: existingAksSubnet.id
    tenantId: tenantId
    operatorObjectId: operatorObjectId
    operatorRoleAssignmentName: operatorRoleAssignmentName
    appRoutingDnsZoneResourceIds: appRoutingDnsZoneResourceIds
    nodePoolCount: nodePoolCount
    nodePoolMinCount: nodePoolMinCount
    nodePoolMaxCount: nodePoolMaxCount
  }
}

module workloadIdentityFederation 'modules/workload-identity-federation.bicep' = {
  name: '${namePrefix}-workload-identity-federation'
  params: {
    namePrefix: namePrefix
    oidcIssuerUrl: aks.outputs.oidcIssuerUrl
  }
}

output sourceSha string = sourceSha
output sourceTree string = sourceTree
output sourceHash string = sourceHash
output clusterId string = aks.outputs.clusterId
output clusterName string = aks.outputs.clusterName
output controlPlanePrincipalId string = aks.outputs.controlPlanePrincipalId
output operatorRoleAssignmentId string = aks.outputs.operatorRoleAssignmentId
output oidcIssuerUrl string = aks.outputs.oidcIssuerUrl
output appRoutingDomain object = aks.outputs.appRoutingDomain
