import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildDeployArgs, deploy } from '../deploy.mjs';
import { fixture, source, fakeAzure, ids, tags, deploymentOutputs } from './fixtures/target.mjs';

test('deployment uses supported source parameters, explicit subscription and incremental mode, never tags', () => {
  const args = buildDeployArgs({ ...source, resourceGroup: fixture.resourceGroup, sourceSha: source.sha,
    deploymentName: 'candidate', subscription: ids.subscriptionId });
  assert.ok(!args.includes('--tags'));
  assert.ok(args.includes(`sourceSha=${source.sha}`));
  assert.ok(args.includes(`sourceHash=${source.sourceHash}`));
  assert.ok(args.includes(`sourceTree=${source.sourceTree}`));
  assert.ok(args.includes('Incremental'));
  assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('offline dry-run never reads account or mutates', () => {
  const result = deploy(fixture, { sourceResolver: () => source, execAz: () => { throw new Error('cloud call'); } });
  assert.equal(result.executed, false);
  assert.equal(result.scope, 'infrastructure-only');
});

test('deployment checks real account, group and resources, then what-if before create', () => {
  const calls = [];
  const result = deploy({ ...fixture, execute: true }, { sourceResolver: () => source, execAz: fakeAzure({}, calls) });
  assert.equal(result.executed, true);
  assert.equal(result.receipt.sourceHash, source.sourceHash);
  assert.equal(result.receipt.sourceTree, source.sourceTree);
  assert.equal(result.receipt.foundationProbeIdentity.principalObjectId,
    deploymentOutputs.foundationProbeIdentity.value.principalObjectId);
  assert.deepEqual(calls.map(args => args.slice(0, 3)), [
    ['account', 'show', '-o'], ['group', 'show', '--name'], ['resource', 'list', '--resource-group'],
    ['deployment', 'group', 'what-if'], ['deployment', 'group', 'create'],
  ]);
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('source/account/ownership/plan/deployment failures never report a deployment receipt', () => {
  for (const overrides of [
    { account: { id: 'wrong', tenantId: ids.tenantId, state: 'Enabled' } },
    { account: { id: ids.subscriptionId, tenantId: 'wrong', state: 'Enabled' } },
    { group: { tags: null } },
    { group: { tags: { ...tags, 'agentweaver:owner': 'other' } } },
    { groupResult: { status: 1, stderr: '(AuthorizationFailed)', stdout: '' } },
    { groupResult: { status: 1, stderr: '(ResourceGroupNotFound)', stdout: '' } },
    { groupResult: { status: 0, stderr: '', stdout: 'malformed' } },
    { resources: [{ id: '/subscriptions/other/resourceGroups/other/providers/thing', tags }] },
    { resources: [{ id: `${fixture.groupId}/providers/Microsoft.Storage/storageAccounts/other`, tags: null }] },
    { whatIf: { status: 1, stderr: 'AccessDenied', stdout: '' } },
  ]) {
    const calls = [];
    assert.throws(() => deploy({ ...fixture, execute: true },
      { sourceResolver: () => source, execAz: fakeAzure(overrides, calls) }));
    assert.ok(!calls.some(args => args[2] === 'create'));
  }
  assert.throws(() => deploy({ ...fixture, execute: true }, {
    sourceResolver: () => { throw new Error('changed input'); }, execAz: () => { throw new Error('unexpected'); },
  }), /changed input/);
  assert.throws(() => deploy({ ...fixture, execute: true }, { sourceResolver: () => source,
    execAz: fakeAzure({ create: { status: 0, stdout: '{"properties":{"provisioningState":"Failed"}}' } }) }),
  /source-bound/);
});

test('changed inputs after what-if never reach create', () => {
  for (const change of [{ sourceHash: 'c'.repeat(64) }, { sourceTree: 'd'.repeat(40) }]) {
    let resolves = 0;
    const calls = [];
    assert.throws(() => deploy({ ...fixture, execute: true }, {
      sourceResolver: () => (++resolves === 1 ? source : { ...source, ...change }),
      execAz: fakeAzure({}, calls),
    }), /changed after what-if/);
    assert.ok(!calls.some(args => args[2] === 'create'));
  }
});

test('deployment cannot publish a source-bound receipt with missing or substituted Git tree', () => {
  for (const value of [undefined, source.sourceHash, 'd'.repeat(40)]) {
    assert.throws(() => deploy({ ...fixture, execute: true }, { sourceResolver: () => source,
      execAz: fakeAzure({ create: { status: 0, stdout: JSON.stringify({
        id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
        properties: { provisioningState: 'Succeeded', outputs: {
          ...deploymentOutputs, sourceTree: { value },
        } },
      }) } }),
    }), /source-bound/);
  }
});

test('deployment receipt rejects substituted resources, identity, workspace and deployment scope', () => {
  const valid = { id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs } };
  const variants = [
    { ...valid, id: valid.id.replace(fixture.groupId, `${fixture.groupId}-other`) },
    { ...valid, properties: { ...valid.properties, outputs: { ...deploymentOutputs, foundationProbeIdentity: undefined } } },
    { ...valid, properties: { ...valid.properties, outputs: { ...deploymentOutputs, monitorWorkspaceId: { value: fixture.groupId } } } },
  ];
  for (const response of variants) {
    const execAz = fakeAzure({ create: { status: 0, stdout: JSON.stringify(response) } });
    assert.throws(() => deploy({ ...fixture, execute: true }, { sourceResolver: () => source, execAz }));
  }
});
