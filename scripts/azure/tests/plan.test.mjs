import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildWhatIfArgs, plan } from '../plan.mjs';
import { fixture, source, fakeAzure, ids } from './fixtures/target.mjs';

test('what-if uses identical source and account/ownership guards without mutation', () => {
  const calls = [];
  const result = plan(fixture, { sourceResolver: () => source, execAz: fakeAzure({}, calls) });
  assert.equal(result.status, 0);
  assert.equal(JSON.parse(result.stdout).nestedDeploymentChanges, undefined);
  assert.ok(calls.at(-1).includes('what-if'));
  assert.ok(!calls.some(args => args.includes('create')));
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

// The parent deployment's changes array does not include resources in its nested federation module.
test('AKS-only what-if lists the nested federated credential create-or-update operations', () => {
  const calls = [];
  const aksOnlySource = {
    ...source,
    scope: 'aks-only',
    template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const result = plan(fixture, {
    sourceResolver: () => aksOnlySource,
    execAz: fakeAzure({ whatIf: { status: 0, stdout: '{"changes":[]}', stderr: '' } }, calls),
  });
  const output = JSON.parse(result.stdout);
  const nestedChanges = output.nestedDeploymentChanges;

  assert.deepEqual(output.changes, []);
  assert.match(nestedChanges.note, /top-level what-if omits resources/);
  assert.match(nestedChanges.note, /existing FIC state was not queried/);
  assert.deepEqual(nestedChanges.federatedIdentityCredentials, [
    {
      operation: 'createOrUpdate',
      managedIdentityName: 'agentweaver-id-foundation-probe',
      federatedCredentialName: 'foundation-probe-workload-identity',
      subject: 'system:serviceaccount:agentweaver:foundation-probe',
      audiences: ['api://AzureADTokenExchange'],
    },
    {
      operation: 'createOrUpdate',
      managedIdentityName: 'agentweaver-id-identity-broker',
      federatedCredentialName: 'identity-broker-workload-identity',
      subject: 'system:serviceaccount:agentweaver:identity-broker',
      audiences: ['api://AzureADTokenExchange'],
    },
    {
      operation: 'createOrUpdate',
      managedIdentityName: 'agentweaver-id-identity-broker-migration',
      federatedCredentialName: 'identity-broker-migration-workload-identity',
      subject: 'system:serviceaccount:agentweaver:identity-broker-migration',
      audiences: ['api://AzureADTokenExchange'],
    },
  ]);
  assert.ok(!calls.some(args => args.includes('create')));
});

test('plan fails closed for unauthorized, missing or untagged target', () => {
  for (const overrides of [
    { groupResult: { status: 1, stderr: '(AuthorizationFailed)', stdout: '' } },
    { groupResult: { status: 1, stderr: '(ResourceGroupNotFound)', stdout: '' } },
    { group: { tags: null } },
    { accountResult: { status: 0, stdout: 'malformed', stderr: '' } },
  ]) {
    const calls = [];
    assert.throws(() => plan(fixture, { sourceResolver: () => source, execAz: fakeAzure(overrides, calls) }));
    assert.ok(!calls.some(args => args.includes('what-if')));
  }
});

test('what-if rejects caller template without source receipt', () => {
  assert.throws(() => buildWhatIfArgs({ resourceGroup: 'shared', template: 'anything' }));
  assert.throws(() => buildWhatIfArgs({ resourceGroup: 'agentweaver', template: 'anything' }));
});

test('AKS-only what-if does not submit a cluster node-pool setting', () => {
  const args = buildWhatIfArgs({ ...source, scope: 'aks-only', resourceGroup: fixture.resourceGroup,
    sourceSha: source.sha, subscription: ids.subscriptionId });
  assert.ok(!args.some(arg => arg.startsWith('nodePoolCount=')));
});
