import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildDeployArgs, deploy } from '../deploy.mjs';
import {
  fixture, source, fakeAzure, ids, tags, deploymentOutputs, operatorRoleAssignmentName,
  subnetId, subnetRoleAssignmentName,
} from './fixtures/target.mjs';

test('deployment uses supported source parameters, explicit subscription and incremental mode, never tags', () => {
  const args = buildDeployArgs({ ...source, resourceGroup: fixture.resourceGroup, sourceSha: source.sha,
    operatorRoleAssignmentName, deploymentName: 'candidate', subscription: ids.subscriptionId });
  assert.ok(!args.includes('--tags'));
  assert.ok(args.includes(`sourceSha=${source.sha}`));
  assert.ok(args.includes(`sourceHash=${source.sourceHash}`));
  assert.ok(args.includes(`sourceTree=${source.sourceTree}`));
  assert.ok(args.includes(`operatorObjectId=${source.operatorObjectId}`));
  assert.ok(args.includes(`operatorRoleAssignmentName=${operatorRoleAssignmentName}`));
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
  assert.equal(result.receipt.operatorRoleAssignmentId,
    deploymentOutputs.operatorRoleAssignmentId.value);
  assert.deepEqual(calls.map(args => args.slice(0, 3)), [
    ['account', 'show', '-o'], ['group', 'show', '--name'], ['resource', 'list', '--resource-group'],
    ['role', 'assignment', 'list'], ['deployment', 'group', 'what-if'],
    ['resource', 'show', '--ids'], ['deployment', 'group', 'create'], ['resource', 'show', '--ids'],
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
    { operatorAssignments: { status: 1, stderr: '(AuthorizationFailed)', stdout: '' } },
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

test('full foundation execution refuses to rewrite an existing protected P0 resource', () => {
  for (const protectedResource of [
    {
      id: `${fixture.groupId}/providers/Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg`,
      type: 'Microsoft.DBforPostgreSQL/flexibleServers',
      name: 'aw-v1-p0-pg',
      tags,
    },
    {
      id: `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe`,
      type: 'Microsoft.ManagedIdentity/userAssignedIdentities',
      name: 'aw-v1-p0-id-foundation-probe',
      tags,
    },
  ]) {
    const calls = [];
    assert.throws(() => deploy({ ...fixture, execute: true }, {
      sourceResolver: () => source,
      execAz: fakeAzure({ resources: [protectedResource] }, calls),
    }), /empty dedicated P0 resource group/);
    assert.ok(!calls.some(args => args.includes('what-if') || args.includes('create')));
  }
});

test('scoped redeploy resolves and reuses the exact existing Cluster Admin assignment', () => {
  const existingName = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
  const clusterId = deploymentOutputs.foundationResources.value.clusterId;
  const assignmentId = `${clusterId}/providers/Microsoft.Authorization/roleAssignments/${existingName}`;
  const assignments = [{
    id: assignmentId, name: existingName, scope: clusterId, principalId: source.operatorObjectId,
    principalType: 'User',
    roleDefinitionId: `/subscriptions/${ids.subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b`,
  }];
  const scopedSource = {
    ...source, scope: 'aks-only', template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const scopedFixture = { ...fixture, template: scopedSource.template, parametersFile: scopedSource.parametersFile };
  const calls = [];
  const result = deploy({ ...scopedFixture, execute: true }, {
    sourceResolver: () => scopedSource,
    execAz: fakeAzure({ operatorAssignments: { status: 0, stdout: JSON.stringify(assignments), stderr: '' } }, calls),
  });
  const create = calls.find(args => args[0] === 'deployment' && args[2] === 'create');
  assert.ok(create.includes(`operatorRoleAssignmentName=${existingName}`));
  assert.equal(result.receipt.scope, 'aks-only');
  assert.equal(result.receipt.clusterId, clusterId);
  assert.equal(result.receipt.operatorRoleAssignmentId, assignmentId);
  assert.equal(calls.filter(args => args[0] === 'deployment' && args[2] === 'create').length, 1);
});

test('only an exact dangling subnet Network Contributor assignment is removed before the final plan', () => {
  const subnetAssignmentId = `${subnetId}/providers/Microsoft.Authorization/roleAssignments/${subnetRoleAssignmentName}`;
  const stale = {
    id: subnetAssignmentId,
    name: subnetRoleAssignmentName,
    type: 'Microsoft.Authorization/roleAssignments',
    properties: {
      scope: subnetId,
      principalId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
      principalType: 'ServicePrincipal',
      roleDefinitionId: `/subscriptions/${ids.subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/4d97b98b-1d4f-4787-a291-c67834d212e7`,
    },
  };
  const calls = [];
  deploy({ ...fixture, execute: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      networkRoleAssignment: { status: 0, stdout: JSON.stringify(stale), stderr: '' },
      aksShow: { status: 1, stdout: '', stderr: '(ResourceNotFound) Managed cluster was not found.' },
      servicePrincipal: { status: 1, stdout: '', stderr: `ERROR: Resource '${stale.properties.principalId}' does not exist.` },
    }, calls),
  });
  const remove = calls.find(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'delete');
  assert.ok(remove);
  assert.deepEqual(remove.slice(0, 5), ['role', 'assignment', 'delete', '--ids', subnetAssignmentId]);
  const whatIfCalls = calls.filter(args => args[2] === 'what-if');
  assert.equal(whatIfCalls.length, 2);
  assert.ok(calls.indexOf(remove) < calls.indexOf(whatIfCalls[1]));
});

test('a live or unverifiable subnet role principal is never removed', () => {
  const subnetAssignmentId = `${subnetId}/providers/Microsoft.Authorization/roleAssignments/${subnetRoleAssignmentName}`;
  const stale = {
    id: subnetAssignmentId, name: subnetRoleAssignmentName,
    type: 'Microsoft.Authorization/roleAssignments',
    properties: {
      scope: subnetId,
      principalId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
      principalType: 'ServicePrincipal',
      roleDefinitionId: `/subscriptions/${ids.subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/4d97b98b-1d4f-4787-a291-c67834d212e7`,
    },
  };
  for (const servicePrincipal of [
    { status: 0, stdout: JSON.stringify({ id: stale.properties.principalId }), stderr: '' },
    { status: 1, stdout: '', stderr: '(AuthorizationFailed)' },
  ]) {
    const calls = [];
    assert.throws(() => deploy({ ...fixture, execute: true }, {
      sourceResolver: () => source,
      execAz: fakeAzure({
        networkRoleAssignment: { status: 0, stdout: JSON.stringify(stale), stderr: '' },
        aksShow: { status: 1, stdout: '', stderr: '(ResourceNotFound) Managed cluster was not found.' },
        servicePrincipal,
      }, calls),
    }));
    assert.ok(!calls.some(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'delete'));
  }
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
