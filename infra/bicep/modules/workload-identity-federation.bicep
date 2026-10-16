@description('Dedicated P0 naming prefix.')
param namePrefix string

@description('Issuer URL of the deployed AKS cluster.')
param oidcIssuerUrl string

@description('Workload identity ServiceAccounts and their UAMI names.')
param services array = [
  {
    name: 'foundation-probe'
    namespace: 'agentweaver-v1-p0'
    serviceAccountName: 'foundation-probe'
  }
  {
    name: 'identity-broker'
    namespace: 'agentweaver-v1-p0'
    serviceAccountName: 'identity-broker'
  }
  {
    name: 'identity-broker-migration'
    namespace: 'agentweaver-v1-p0'
    serviceAccountName: 'identity-broker-migration'
  }
]

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = [
  for service in services: {
    name: '${namePrefix}-id-${service.name}'
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
