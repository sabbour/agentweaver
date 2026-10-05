import { armGuid, AKS_RBAC_CLUSTER_ADMIN_ROLE_ID, AKS_NETWORK_CONTRIBUTOR_ROLE_ID } from '../../lib/guardrails.mjs';

export const ids = {
  subscriptionId: '11111111-1111-1111-1111-111111111111',
  allowedSubscriptionId: '11111111-1111-1111-1111-111111111111',
  tenantId: '22222222-2222-2222-2222-222222222222',
  allowedTenantId: '22222222-2222-2222-2222-222222222222',
  operatorObjectId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
};
export const tags = { 'agentweaver:environment': 'v1-p0', 'agentweaver:managed-by': 'bicep',
  'agentweaver:owner': 'team', 'agentweaver:cost-center': 'p0' };
export const source = { sha: 'a'.repeat(40), sourceTree: 'c'.repeat(40), sourceHash: 'b'.repeat(64), branch: 'candidate',
  template: 'infra/bicep/main.bicep', parametersFile: 'infra/bicep/parameters/approved.json',
  operatorObjectId: ids.operatorObjectId,
  owner: 'team', costCenter: 'p0', scope: 'infrastructure-only',
  location: 'eastus2euap', monitorLocation: 'eastus2',
  postgresEntraAdminObjectId: '33333333-3333-3333-3333-333333333333',
  postgresEntraAdminPrincipalName: 'generated-admin-principal',
  postgresEntraAdminPrincipalType: 'User',
  appRoutingDnsZoneResourceIds: [] };
export const fixture = { ...ids, resourceGroup: 'aw-v1-p0', repoRoot: process.cwd(),
  template: source.template, parametersFile: source.parametersFile, expectedSha: source.sha,
  deploymentName: `aw-v1-p0-${source.sha.slice(0, 12)}`,
  groupId: `/subscriptions/${ids.subscriptionId}/resourceGroups/aw-v1-p0` };
export const clusterId = `${fixture.groupId}/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks`;
export const operatorRoleAssignmentName =
  armGuid(clusterId, ids.operatorObjectId, AKS_RBAC_CLUSTER_ADMIN_ROLE_ID);
export const subnetId = `${fixture.groupId}/providers/Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/aks`;
export const subnetRoleAssignmentName =
  armGuid(subnetId, clusterId, AKS_NETWORK_CONTRIBUTOR_ROLE_ID);
export const deploymentOutputs = {
  sourceSha: { value: source.sha }, sourceTree: { value: source.sourceTree }, sourceHash: { value: source.sourceHash },
  operatorRoleAssignmentId: { value: `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${operatorRoleAssignmentName}` },
  aksClusterName: { value: 'aw-v1-p0-aks' }, storageAccountName: { value: 'awv1p0blob' },
  aksControlPlanePrincipalId: { value: '66666666-6666-6666-6666-666666666666' },
  appRoutingDomain: { value: { managedDefaultRequested: true, domainName: 'test-only.invalid' } },
  appRoutingIdentity: { value: {
    resourceId: `/subscriptions/${ids.subscriptionId}/resourceGroups/MC_aw-v1-p0_eastus2/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-aks-app-routing`,
    clientId: '77777777-7777-7777-7777-777777777777', objectId: '88888888-8888-8888-8888-888888888888',
  } },
  clusterName: { value: 'aw-v1-p0-aks' },
  clusterId: { value: `${fixture.groupId}/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks` },
  controlPlanePrincipalId: { value: '66666666-6666-4666-8666-666666666666' },
  oidcIssuerUrl: { value: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/' },
  aksOidcIssuerUrl: { value: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/' },
  monitorWorkspaceId: { value: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' },
  foundationProbeIdentity: { value: {
    name: 'foundation-probe', resourceId: `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe`,
    clientId: '44444444-4444-4444-4444-444444444444', principalObjectId: '55555555-5555-5555-5555-555555555555',
    namespace: 'agentweaver-v1-p0', serviceAccount: 'foundation-probe',
  } },
  identityBrokerRuntimeIdentity: { value: {
    name: 'identity-broker', resourceId: `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker`,
    clientId: '99999999-9999-9999-9999-999999999999', principalObjectId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    namespace: 'agentweaver-v1-p0', serviceAccount: 'identity-broker',
  } },
  identityBrokerMigrationIdentity: { value: {
    name: 'identity-broker-migration',
    resourceId: `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker-migration`,
    clientId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', principalObjectId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
    namespace: 'agentweaver-v1-p0', serviceAccount: 'identity-broker-migration',
  } },
  foundationResources: { value: {
    clusterId: `${fixture.groupId}/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks`,
    keyVaultId: `${fixture.groupId}/providers/Microsoft.KeyVault/vaults/aw-v1-p0-kv`,
    vaultUri: 'https://aw-v1-p0-kv.vault.azure.net/',
    storageAccountId: `${fixture.groupId}/providers/Microsoft.Storage/storageAccounts/awv1p0blob`,
    blobContainerId: `${fixture.groupId}/providers/Microsoft.Storage/storageAccounts/awv1p0blob/blobServices/default/containers/platform-artifacts`,
    blobContainerUri: 'https://awv1p0blob.blob.core.windows.net/platform-artifacts',
    postgresServerId: `${fixture.groupId}/providers/Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg`,
    postgresHost: 'aw-v1-p0-pg.postgres.database.azure.com',
    monitorWorkspaceResourceId: `${fixture.groupId}/providers/Microsoft.OperationalInsights/workspaces/aw-v1-p0-law`,
    monitorWorkspaceId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    appInsightsResourceId: `${fixture.groupId}/providers/Microsoft.Insights/components/aw-v1-p0-appi`,
  } },
};
export const observedCluster = {
  id: deploymentOutputs.foundationResources.value.clusterId,
  properties: {
    kubernetesVersion: '1.29.7',
    fqdn: 'api.example.azmk8s.io',
    disableLocalAccounts: true,
    aadProfile: { managed: true, enableAzureRBAC: true },
    apiServerAccessProfile: { enablePrivateCluster: false },
    networkProfile: {
      networkPlugin: 'azure',
      networkPolicy: 'cilium',
      networkDataplane: 'cilium',
      advancedNetworking: { enabled: true, security: { enabled: true } },
    },
  },
};
const ok = value => ({ status: 0, stdout: JSON.stringify(value), stderr: '' });
export function fakeAzure(overrides = {}, calls = []) {
  return (args, options = {}) => {
    const project = result => {
      if (typeof options.projectJson !== 'function' || result.status !== 0) return result;
      return { ...result, stdout: JSON.stringify(options.projectJson(JSON.parse(result.stdout))) };
    };
    calls.push(args);
    if (args[0] === 'role' && args[1] === 'assignment' && args[2] === 'list') {
      return overrides.operatorAssignments ?? ok([]);
    }
    if (args[0] === 'postgres' && args[1] === 'flexible-server' && args[2] === 'show') {
      return overrides.postgresServer ?? ok({
        id: deploymentOutputs.foundationResources.value.postgresServerId,
        name: 'aw-v1-p0-pg',
        location: source.location,
        version: '16',
        fullyQualifiedDomainName: deploymentOutputs.foundationResources.value.postgresHost,
        network: {
          delegatedSubnetResourceId: `${fixture.groupId}/providers/Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/postgres`,
          privateDnsZoneArmResourceId:
            `${fixture.groupId}/providers/Microsoft.Network/privateDnsZones/privatelink.postgres.database.azure.com`,
          publicNetworkAccess: 'Disabled',
        },
        authConfig: { activeDirectoryAuth: 'Enabled', passwordAuth: 'Disabled', tenantId: ids.tenantId },
      });
    }
    if (args[0] === 'identity' && args[1] === 'show') {
      const name = args[args.indexOf('--name') + 1];
      const runtime = name.endsWith('-identity-broker');
      const identity = runtime
        ? deploymentOutputs.identityBrokerRuntimeIdentity.value
        : deploymentOutputs.identityBrokerMigrationIdentity.value;
      return overrides.identities?.[name] ?? ok({
        id: identity.resourceId,
        name,
        location: source.location,
        clientId: identity.clientId,
        principalId: identity.principalObjectId,
      });
    }
    if (args[0] === 'resource' && args[1] === 'show') {
      const id = args[args.indexOf('--ids') + 1];
      if (id?.toLowerCase().includes('/administrators/')) {
        return overrides.postgresAdmin ?? ok({
          id,
          type: 'Microsoft.DBforPostgreSQL/flexibleServers/administrators',
          name: source.postgresEntraAdminPrincipalName,
          properties: {
            objectId: source.postgresEntraAdminObjectId,
            principalName: source.postgresEntraAdminPrincipalName,
            principalType: source.postgresEntraAdminPrincipalType,
            tenantId: ids.tenantId,
          },
        });
      }
      if (id?.toLowerCase().includes('/providers/microsoft.authorization/roleassignments/')) {
        if (id.toLowerCase() ===
          `${subnetId}/providers/Microsoft.Authorization/roleAssignments/${subnetRoleAssignmentName}`.toLowerCase()) {
          return overrides.networkRoleAssignment ?? {
            status: 1, stdout: '', stderr: '(RoleAssignmentNotFound) The role assignment does not exist.',
          };
        }
        if (id.toLowerCase().startsWith(
          `${clusterId}/providers/Microsoft.Authorization/roleAssignments/`.toLowerCase())) {
          const name = id.split('/').at(-1);
          return overrides.details?.[id] ?? overrides.operatorRoleAssignment ?? ok({
            id, name, type: 'Microsoft.Authorization/roleAssignments',
            properties: {
              scope: clusterId,
              principalId: ids.operatorObjectId,
              principalType: 'User',
              roleDefinitionId: `/subscriptions/${ids.subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/${AKS_RBAC_CLUSTER_ADMIN_ROLE_ID}`,
            },
          });
        }
        return project(overrides.detailResult ?? ok(overrides.details?.[id]));
      }
      return project(overrides.detailResult ?? ok(overrides.details?.[id]));
    }
    if (args[0] === 'role' && args[1] === 'assignment' && args[2] === 'delete') {
      return overrides.roleAssignmentDelete ?? ok({});
    }
    if (args[0] === 'rest') return overrides.clusterResult ?? ok(observedCluster);
    if (args[0] === 'account') return overrides.accountResult ?? ok(overrides.account ??
      { id: ids.subscriptionId, tenantId: ids.tenantId, state: 'Enabled' });
    if (args[0] === 'group') return overrides.groupResult ?? ok({ id: fixture.groupId, name: 'aw-v1-p0',
      tags, ...overrides.group });
    if (args[0] === 'resource') return project(overrides.resourceResult ?? ok(overrides.resources ?? []));
    if (args[0] === 'policy' && args[1] === 'state') {
      return project(overrides.policyStateResult ?? ok(overrides.policyStates ?? []));
    }
    if (args[0] === 'monitor' && args[1] === 'activity-log') {
      const id = args[args.indexOf('--resource-id') + 1];
      return project(overrides.activityResult ?? ok(overrides.activityEvents?.[id] ?? []));
    }
    if (args[0] === 'deployment' && args[1] === 'operation') {
      return project(overrides.deploymentOperationsResult ?? ok(overrides.deploymentOperations ?? []));
    }
    if (args[2] === 'what-if') return overrides.whatIf ?? ok({ changes: [] });
    if (args[0] === 'deployment') {
      const outputs = structuredClone(deploymentOutputs);
      const name = args[args.indexOf('--name') + 1];
      const roleName = args.find(value => value.startsWith('operatorRoleAssignmentName='))?.split('=')[1];
      outputs.operatorRoleAssignmentId.value =
        `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${roleName}`;
      return project(overrides.create ?? ok({
        id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${name}`,
        properties: { provisioningState: 'Succeeded', outputs },
      }));
    }
    if (args[0] === 'aks' && args[1] === 'show' && args.includes('--query')) return overrides.issuerResult ?? {
      status: 0,
      stdout: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
      stderr: '',
    };
    if (args[0] === 'aks' && args[1] === 'show') return overrides.aksShow ?? {
      status: 1, stdout: '', stderr: '(ResourceNotFound) Managed cluster was not found.',
    };
    if (args[0] === 'ad' && args[1] === 'sp' && args[2] === 'show') return overrides.servicePrincipal ??
      { status: 1, stdout: '', stderr: `ERROR: Resource '${args[args.indexOf('--id') + 1]}' does not exist.` };
    if (args[0] === 'identity' && args[1] === 'federated-credential' && args[2] === 'show') {
      const credentialName = args[args.indexOf('--name') + 1];
      const service = credentialName.replace(/-workload-identity$/, '');
      const response = overrides.federatedCredentials?.[credentialName] ??
        (service === 'foundation-probe' ? overrides.federationResult : undefined);
      return response ?? ok({
        issuer: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
        subject: `system:serviceaccount:agentweaver-v1-p0:${service}`,
        audiences: ['api://AzureADTokenExchange'],
      });
    }
    if (args[0] === 'identity') return overrides.federationResult ?? ok({
      issuer: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
      subject: 'system:serviceaccount:agentweaver-v1-p0:foundation-probe',
      audiences: ['api://AzureADTokenExchange'],
    });
    if (args[0] === 'monitor') return overrides.monitorResult ?? ok([{ Message: 'historical' }]);
    throw new Error(`Unexpected Azure command: ${args.join(' ')}`);
  };
}
