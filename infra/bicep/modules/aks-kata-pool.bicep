// Kata user node pool for Agentweaver sandbox pods. It is a child resource of the
// existing cluster, so the full-foundation and AKS-only templates create or
// update only this pool and never PUT the managed cluster for it.
// AKS adds the node label kubernetes.azure.com/kata-vm-isolation=true, which is
// the node selector of RuntimeClass kata-vm-isolation. There is no taint because
// sandbox pods have no toleration.
@description('Existing AKS cluster name.')
param clusterName string

@description('Subnet resource ID of the existing aks subnet.')
param nodeSubnetId string

@description('VM size of the kata pool: a Generation 2 size with nested virtualization.')
param vmSize string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: clusterName
}

resource kataPool 'Microsoft.ContainerService/managedClusters/agentPools@2026-07-02-preview' = {
  parent: aks
  name: 'kata'
  properties: {
    mode: 'User'
    // No count: AKS creates the pool with one node, and later deployments keep the autoscaler count.
    // Keep one node warm: a scale-up takes longer than the 30-second sandbox scheduling budget.
    enableAutoScaling: true
    minCount: 1
    maxCount: 2
    vmSize: vmSize
    vnetSubnetID: nodeSubnetId
    osType: 'Linux'
    osSKU: 'AzureLinux'
    workloadRuntime: 'KataVmIsolation'
    type: 'VirtualMachineScaleSets'
    securityProfile: {
      sshAccess: 'Disabled'
    }
  }
}

output name string = kataPool.name
