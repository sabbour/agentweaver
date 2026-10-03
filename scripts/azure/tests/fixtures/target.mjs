export const ids = {
  subscriptionId: '11111111-1111-1111-1111-111111111111',
  allowedSubscriptionId: '11111111-1111-1111-1111-111111111111',
  tenantId: '22222222-2222-2222-2222-222222222222',
  allowedTenantId: '22222222-2222-2222-2222-222222222222',
};
export const tags = { 'agentweaver:environment': 'v1-p0', 'agentweaver:managed-by': 'bicep',
  'agentweaver:owner': 'team', 'agentweaver:cost-center': 'p0' };
export const source = { sha: 'a'.repeat(40), sourceHash: 'b'.repeat(64), branch: 'candidate',
  template: 'infra/bicep/main.bicep', parametersFile: 'infra/bicep/parameters/approved.json',
  owner: 'team', costCenter: 'p0', scope: 'infrastructure-only',
  postgresEntraAdminObjectId: '33333333-3333-3333-3333-333333333333' };
export const fixture = { ...ids, resourceGroup: 'aw-v1-p0', repoRoot: process.cwd(),
  template: source.template, parametersFile: source.parametersFile, expectedSha: source.sha,
  groupId: `/subscriptions/${ids.subscriptionId}/resourceGroups/aw-v1-p0` };
const ok = value => ({ status: 0, stdout: JSON.stringify(value), stderr: '' });
export function fakeAzure(overrides = {}, calls = []) {
  return args => {
    calls.push(args);
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
    if (args[0] === 'deployment') return overrides.create ?? ok({ id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/candidate`,
      properties: { provisioningState: 'Succeeded', outputs: {
        sourceSha: { value: source.sha }, sourceHash: { value: source.sourceHash },
        aksClusterName: { value: 'aw-v1-p0-aks' }, storageAccountName: { value: 'awv1p0blob' },
        monitorWorkspaceId: { value: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' },
      } } });
    if (args[0] === 'aks') return { status: 0, stdout: 'https://issuer.example/', stderr: '' };
    if (args[0] === 'monitor') return ok([{ Message: 'historical' }]);
    throw new Error(`Unexpected Azure command: ${args.join(' ')}`);
  };
}
