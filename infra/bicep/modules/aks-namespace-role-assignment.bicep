// Assign one role at the scope of a single Kubernetes namespace in an AKS cluster that uses Azure
// RBAC for Kubernetes authorization. The namespace scope keeps the grant inside that namespace.
@description('Existing AKS cluster name in this resource group.')
param clusterName string

@description('Kubernetes namespace that bounds the assignment.')
param namespace string

@description('Object ID of the service principal that receives the role.')
param principalId string

@description('Full resource ID of the role definition to assign.')
param roleDefinitionId string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: clusterName
}

// AKS accepts <cluster ID>/namespaces/<name> as a role assignment scope. The existing reference
// only builds that ID; it does not require an AKS managed namespace resource.
resource scopeNamespace 'Microsoft.ContainerService/managedClusters/namespaces@2025-10-01' existing = {
  parent: aks
  name: namespace
}

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, namespace, principalId, roleDefinitionId)
  scope: scopeNamespace
  properties: {
    roleDefinitionId: roleDefinitionId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
