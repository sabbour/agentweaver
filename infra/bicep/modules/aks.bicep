// Dedicated P0 AKS cluster: OIDC issuer + workload identity enabled so
// per-service user-assigned identities can federate without storing any
// client secret. Private cluster, system-assigned control-plane identity,
// and a single system node pool sized for the foundation smoke workload.
@description('Azure region for the cluster.')
param location string

@description('Dedicated naming prefix, e.g. aw-v1-p0.')
param namePrefix string

@description('Resource tags applied to the cluster.')
param tags object

@description('Subnet resource ID the node pool attaches to.')
param nodeSubnetId string

@description('Microsoft Entra tenant for managed authentication. Operator access requires separate approved Azure RBAC assignments.')
param tenantId string

@description('Kubernetes version. Leave empty to use the AKS default supported version.')
param kubernetesVersion string = ''

@description('System node pool VM size.')
param nodePoolVmSize string = 'Standard_D2s_v5'

@description('System node pool node count. Kept small and fixed for the P0 smoke environment.')
param nodePoolCount int = 2

@description('Whether the AKS API server is private (no public endpoint). Always true for this dedicated environment.')
param enablePrivateCluster bool = true

@description('Optional existing application DNS zone resource IDs for custom routing domains.')
param appRoutingDnsZoneResourceIds array = []

var clusterName = '${namePrefix}-aks'
var networkContributorRoleId = '4d97b98b-1d4f-4787-a291-c67834d212e7'
var managedDefaultDomainRequested = empty(appRoutingDnsZoneResourceIds)

resource existingVnet 'Microsoft.Network/virtualNetworks@2023-09-01' existing = {
  name: '${namePrefix}-vnet'
}

resource existingNodeSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-09-01' existing = {
  parent: existingVnet
  name: 'aks'
}

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' = {
  name: clusterName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    dnsPrefix: clusterName
    disableLocalAccounts: true
    aadProfile: {
      managed: true
      enableAzureRBAC: true
      tenantID: tenantId
    }
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    ingressProfile: {
      webAppRouting: {
        enabled: true
        dnsZoneResourceIds: empty(appRoutingDnsZoneResourceIds) ? null : appRoutingDnsZoneResourceIds
        defaultDomain: {
          enabled: managedDefaultDomainRequested
        }
        nginx: {
          defaultIngressControllerType: 'None'
        }
      }
    }
    addonProfiles: {
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          enableSecretRotation: 'true'
          rotationPollInterval: '2m'
        }
      }
    }
    apiServerAccessProfile: {
      enablePrivateCluster: enablePrivateCluster
    }
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'cilium'
      networkDataplane: 'cilium'
      advancedNetworking: {
        enabled: true
        security: {
          enabled: true
        }
        observability: {
          enabled: false
        }
      }
      serviceCidr: '172.20.0.0/16'
      dnsServiceIP: '172.20.0.10'
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: nodePoolCount
        vmSize: nodePoolVmSize
        vnetSubnetID: nodeSubnetId
        osType: 'Linux'
        osSKU: 'AzureLinux'
        type: 'VirtualMachineScaleSets'
      }
    ]
  }
}

resource controlPlaneNetworkRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(nodeSubnetId, aks.id, networkContributorRoleId)
  scope: existingNodeSubnet
  properties: {
    principalId: aks.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', networkContributorRoleId)
  }
}

output controlPlanePrincipalId string = aks.identity.principalId
output clusterId string = aks.id
output clusterName string = aks.name
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId
output appRoutingIdentity object = {
  resourceId: aks.properties.ingressProfile.webAppRouting.identity.resourceId
  clientId: aks.properties.ingressProfile.webAppRouting.identity.clientId
  objectId: aks.properties.ingressProfile.webAppRouting.identity.objectId
}
output appRoutingDomain object = {
  managedDefaultRequested: managedDefaultDomainRequested
  domainName: managedDefaultDomainRequested
    ? aks.properties.ingressProfile.webAppRouting.defaultDomain.domainName
    : null
}
