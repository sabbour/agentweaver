export const ids = {
  subscriptionId: '11111111-1111-1111-1111-111111111111',
  allowedSubscriptionId: '11111111-1111-1111-1111-111111111111',
  tenantId: '22222222-2222-2222-2222-222222222222',
  allowedTenantId: '22222222-2222-2222-2222-222222222222',
};
export const tags = { 'agentweaver:environment': 'v1-p0', 'agentweaver:managed-by': 'bicep',
  'agentweaver:owner': 'team', 'agentweaver:cost-center': 'p0' };
export const source = { sha: 'a'.repeat(40), sourceTree: 'c'.repeat(40), sourceHash: 'b'.repeat(64), branch: 'candidate',
  template: 'infra/bicep/main.bicep', parametersFile: 'infra/bicep/parameters/approved.json',
  owner: 'team', costCenter: 'p0', scope: 'infrastructure-only',
  postgresEntraAdminObjectId: '33333333-3333-3333-3333-333333333333', appRoutingDnsZoneResourceIds: [] };
export const fixture = { ...ids, resourceGroup: 'aw-v1-p0', repoRoot: process.cwd(),
  template: source.template, parametersFile: source.parametersFile, expectedSha: source.sha,
  deploymentName: `aw-v1-p0-${source.sha.slice(0, 12)}`,
  groupId: `/subscriptions/${ids.subscriptionId}/resourceGroups/aw-v1-p0` };
export const deploymentOutputs = {
  sourceSha: { value: source.sha }, sourceTree: { value: source.sourceTree }, sourceHash: { value: source.sourceHash },
  aksClusterName: { value: 'aw-v1-p0-aks' }, storageAccountName: { value: 'awv1p0blob' },
  aksControlPlanePrincipalId: { value: '66666666-6666-6666-6666-666666666666' },
  appRoutingDomain: { value: { managedDefaultRequested: true, domainName: 'test-only.invalid' } },
  appRoutingIdentity: { value: {
    resourceId: `/subscriptions/${ids.subscriptionId}/resourceGroups/MC_aw-v1-p0_eastus2/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-aks-app-routing`,
    clientId: '77777777-7777-7777-7777-777777777777', objectId: '88888888-8888-8888-8888-888888888888',
  } },
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
    fqdn: 'api.example.privatelink.azmk8s.io',
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
  return args => {
    calls.push(args);
    if (args[0] === 'rest') return overrides.clusterResult ?? ok(observedCluster);
    if (args[0] === 'account') return overrides.accountResult ?? ok(overrides.account ??
      { id: ids.subscriptionId, tenantId: ids.tenantId, state: 'Enabled' });
    if (args[0] === 'group') return overrides.groupResult ?? ok({ id: fixture.groupId, name: 'aw-v1-p0',
      tags, ...overrides.group });
    if (args[0] === 'resource' && args[1] === 'show') {
      const id = args[args.indexOf('--ids') + 1];
      return overrides.detailResult ?? ok(overrides.details?.[id]);
    }
    if (args[0] === 'resource') return overrides.resourceResult ?? ok(overrides.resources ?? []);
    if (args[2] === 'what-if') return overrides.whatIf ?? ok({ changes: [] });
    if (args[0] === 'deployment') return overrides.create ?? ok({
      id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
      properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs } });
    if (args[0] === 'aks') return overrides.issuerResult ?? {
      status: 0,
      stdout: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
      stderr: '',
    };
    if (args[0] === 'identity') return overrides.federationResult ?? ok({
      issuer: 'https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/',
      subject: 'system:serviceaccount:agentweaver-v1-p0:foundation-probe',
      audiences: ['api://AzureADTokenExchange'],
    });
    if (args[0] === 'monitor') return overrides.monitorResult ?? ok([{ Message: 'historical' }]);
    throw new Error(`Unexpected Azure command: ${args.join(' ')}`);
  };
}
