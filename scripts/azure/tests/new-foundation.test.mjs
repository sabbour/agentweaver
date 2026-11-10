import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deploy, cliConfig } from '../deploy.mjs';
import { plan } from '../plan.mjs';
import { runAz } from '../lib/exec.mjs';
import { fixture, source, fakeAzure, ids, tags } from './fixtures/target.mjs';

const group = 'aweast2np01';
const config = { ...fixture, resourceGroup: group, newFoundation: true, execute: true };
const selectedSource = { ...source, keyVaultName: `${group}-kv`, location: 'eastus2', monitorLocation: 'eastus2' };
const groupId = `/subscriptions/${ids.subscriptionId}/resourceGroups/${group}`;
const planResult = { status: 'Succeeded', error: null, potentialChanges: null, diagnostics: null,
  changes: [{ resourceId: `${groupId}/providers/Microsoft.Network/virtualNetworks/${group}-vnet`,
    changeType: 'Create', unsupportedReason: null }] };
const ok = value => ({ status: 0, stdout: JSON.stringify(value), stderr: '' });

test('compiled foundation passes the optional AKS managed group without changing the P0 default', () => {
  const template = JSON.parse(runAz(['bicep', 'build', '--file', 'infra/bicep/main.bicep', '--stdout']).stdout);
  assert.equal(template.parameters.nodeResourceGroup.defaultValue, '');
  const aks = template.resources.find(resource => resource.name ===
    "[format('{0}-aks', parameters('namePrefix'))]");
  assert.equal(aks.properties.parameters.nodeResourceGroup.value, "[parameters('nodeResourceGroup')]");
  const cluster = aks.properties.template.resources.find(resource =>
    resource.type === 'Microsoft.ContainerService/managedClusters');
  assert.equal(cluster.properties.nodeResourceGroup,
    "[if(empty(parameters('nodeResourceGroup')), null(), parameters('nodeResourceGroup'))]");
});

function azure(overrides = {}, calls = []) {
  const fallback = fakeAzure();
  let inventoryCalls = 0;
  let resourceCalls = 0;
  let planCalls = 0;
  let addonReads = 0;
  return (args, options) => {
    calls.push(args);
    if (args[0] === 'version') return overrides.version ?? ok({ 'azure-cli': '2.92.0' });
    if (args[0] === 'account') return ok({ id: ids.subscriptionId, tenantId: ids.tenantId, state: 'Enabled',
      user: { name: ++inventoryCalls > 1 && overrides.identityChange ? 'other' : 'installer', type: 'user' } });
    if (args[0] === 'group' && args[1] === 'show') return overrides.group ?? ok({ id: groupId, name: group, tags });
    if (args[0] === 'group' && args[1] === 'exists') return overrides.managedGroup ?? ok(false);
    if (args[0] === 'resource' && args[1] === 'list') {
      resourceCalls++;
      return resourceCalls > 1 && overrides.secondResources ? overrides.secondResources : overrides.resources ?? ok([]);
    }
    if (args[0] === 'deployment' && args[2] === 'list') return overrides.history ?? ok([]);
    if (args[0] === 'monitor' && args[3] === 'list-deleted-workspaces') return overrides.deletedWorkspaces ?? ok([]);
    if (args[0] === 'deployment' && args[2] === 'what-if') {
      planCalls++;
      return planCalls > 1 && overrides.secondPlan ? overrides.secondPlan : overrides.whatIf ?? ok(planResult);
    }
    if (args[0] === 'aks' && args.includes('nodeResourceGroup')) return overrides.nodeGroup ?? ok(`${group}-aks-nodes`);
    if (args[0] === 'aks' && args.includes('addonProfiles.omsagent') && overrides.monitorDisabled && addonReads++ === 0) {
      return ok({ enabled: false });
    }
    const result = fallback(args, options);
    if (result.status !== 0 || !result.stdout) return result;
    if (args[0] === 'deployment' && args[2] === 'create' ||
        args[0] === 'aks' && args.includes('addonProfiles.omsagent')) {
      const value = JSON.parse(result.stdout.replaceAll('agentweaver', group));
      const outputs = value.properties?.outputs;
      if (outputs) {
        outputs.foundationProbeIdentity.value.namespace = 'agentweaver';
        outputs.identityBrokerRuntimeIdentity.value.namespace = 'agentweaver';
        outputs.identityBrokerMigrationIdentity.value.namespace = 'agentweaver';
        outputs.appRoutingIdentity.value.resourceId = outputs.appRoutingIdentity.value.resourceId
          .replace(`MC_${group}_eastus2`, overrides.routingGroup ?? `${group}-aks-nodes`);
      }
      return ok(value);
    }
    return result;
  };
}

test('new foundation deploy binds the target, repeated plan, managed group and existing Monitor step', async () => {
  const calls = [];
  const result = await deploy(config, { sourceResolver: () => selectedSource, execAz: azure({ monitorDisabled: true }, calls) });
  assert.equal(result.executed, true);
  assert.equal(result.receipt.resourceGroup, group);
  assert.equal(result.receipt.azureMonitorAddon.action, 'enabled');
  const plans = calls.filter(args => args[0] === 'deployment' && args[2] === 'what-if');
  const create = calls.find(args => args[0] === 'deployment' && args[2] === 'create');
  assert.equal(plans.length, 2);
  for (const args of [...plans, create]) {
    assert.ok(args.includes(`nodeResourceGroup=${group}-aks-nodes`));
    assert.ok(args.includes(`sourceSha=${source.sha}`));
    assert.ok(args.includes(`sourceTree=${source.sourceTree}`));
    assert.ok(args.includes(`sourceHash=${source.sourceHash}`));
    assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
    assert.equal(args[args.indexOf('--name') + 1], `${group}-${source.sha.slice(0, 12)}`);
    assert.equal(args[args.indexOf('--mode') + 1], 'Incremental');
  }
  assert.ok(calls.some(args => args.includes('addonProfiles.omsagent')));
  const monitor = calls.find(args => args[0] === 'aks' && args[1] === 'enable-addons');
  assert.equal(monitor[monitor.indexOf('--resource-group') + 1], group);
  assert.equal(monitor[monitor.indexOf('--workspace-resource-id') + 1],
    `${groupId}/providers/Microsoft.OperationalInsights/workspaces/${group}-law`);
});

test('new foundation rejects invalid targets, DNS and bootstrap before Azure reads', async () => {
  for (const [changes, sourceChanges] of [
    [{ resourceGroup: 'agentweaver' }, {}],
    [{ resourceGroup: 'aweast2np01 ' }, {}],
    [{ bootstrapIdentityPostgres: true }, {}],
    [{}, { scope: 'aks-only' }],
    [{}, { appRoutingDnsZoneResourceIds: ['/subscriptions/existing'] }],
    [{}, { keyVaultName: 'agentweaver-kv' }],
    [{}, { monitorLocation: 'westus2' }],
  ]) {
    await assert.rejects(deploy({ ...config, ...changes }, {
      sourceResolver: () => ({ ...selectedSource, ...sourceChanges }), execAz() { throw new Error('unexpected Azure call'); },
    }));
  }
  assert.equal(cliConfig({ 'new-foundation': true }).newFoundation, true);
  assert.throws(() => plan({ ...config, resourceGroup: 'awunlisted01' }, { sourceResolver: () => selectedSource, execAz: azure() }),
    /registered dedicated v1 target/);
});

test('new foundation compares plan changes, not request metadata or result ordering', async () => {
  const other = { resourceId: `${groupId}/providers/Microsoft.Network/virtualNetworks/second`, changeType: 'Create' };
  const first = { requestId: 'one', changes: [other, planResult.changes[0]] };
  const second = { requestId: 'two', changes: [planResult.changes[0], other] };
  const result = await deploy(config, { sourceResolver: () => selectedSource,
    execAz: azure({ whatIf: ok(first), secondPlan: ok(second) }) });
  assert.equal(result.executed, true);
});

test('new foundation fails closed before create for existing, ambiguous or changing state', async () => {
  const cases = [
    { resources: ok([{ id: `${groupId}/providers/Microsoft.Storage/storageAccounts/existing` }]) },
    { resources: ok({ value: [] }) },
    { resources: { status: 1, stderr: 'denied', stdout: '' } },
    { history: ok([{ name: 'failed-deployment' }]) },
    { history: ok(null) },
    { version: ok({ 'azure-cli': '2.74.0' }) },
    { version: ok({ 'azure-cli': 'invalid' }) },
    { deletedWorkspaces: ok([{ name: `${group}-law` }]) },
    { deletedWorkspaces: { status: 1, stderr: 'denied', stdout: '' } },
    { deletedWorkspaces: ok({ value: [] }) },
    { managedGroup: ok(true) },
    { managedGroup: ok(null) },
    { group: ok({ id: `${groupId}-other`, name: group, tags }) },
    { whatIf: ok({ changes: [] }) },
    { whatIf: ok({ ...planResult, diagnostics: [{ code: 'ShortCircuit' }] }) },
    { whatIf: ok({ ...planResult, potentialChanges: [{ resourceId: '/subscriptions/other', changeType: 'Modify' }] }) },
    { whatIf: ok({ changes: [{ ...planResult.changes[0], diagnostic: { code: 'ShortCircuit' } }] }) },
    { whatIf: ok({ ...planResult, status: 'Failed' }) },
    { whatIf: ok({ changes: [planResult.changes[0], planResult.changes[0]] }) },
    { whatIf: ok({ changes: [{ ...planResult.changes[0], changeType: 'Modify' }] }) },
    { whatIf: ok({ changes: [{ ...planResult.changes[0], changeType: 'Ignore' }] }) },
    { whatIf: ok({ changes: [{ ...planResult.changes[0], changeType: 'Deploy' }] }) },
    { whatIf: ok({ changes: [{ ...planResult.changes[0], resourceId: '/subscriptions/other/resourceGroups/existing/resource' }] }) },
    { whatIf: { status: 0, stdout: 'invalid', stderr: '' } },
    { identityChange: true },
    { secondResources: ok([{ id: `${groupId}/providers/Microsoft.Network/virtualNetworks/racing` }]) },
    { secondPlan: ok({ changes: [{ ...planResult.changes[0], resourceId: `${groupId}/providers/Microsoft.Network/virtualNetworks/other` }] }) },
  ];
  for (const overrides of cases) {
    const calls = [];
    await assert.rejects(deploy(config, { sourceResolver: () => selectedSource, execAz: azure(overrides, calls) }));
    assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create'), JSON.stringify(overrides));
  }
});

test('new foundation rejects changed source and verifies the returned managed group before Monitor', async () => {
  const calls = [];
  let resolves = 0;
  await assert.rejects(deploy(config, { sourceResolver: () => ++resolves === 1 ? selectedSource :
    { ...selectedSource, sourceHash: 'f'.repeat(64) }, execAz: azure({}, calls) }), /Source inputs changed/);
  assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create'));

  const afterCreate = [];
  await assert.rejects(deploy(config, { sourceResolver: () => selectedSource,
    execAz: azure({ nodeGroup: ok('wrong-managed-group') }, afterCreate) }), /managed group does not match/);
  assert.ok(afterCreate.some(args => args[0] === 'deployment' && args[2] === 'create'));
  assert.ok(!afterCreate.some(args => args.includes('addonProfiles.omsagent')));

  await assert.rejects(deploy(config, { sourceResolver: () => selectedSource,
    execAz: azure({ routingGroup: 'other-managed-group' }) }), /routing identity is outside/);
});

test('new foundation does not retry an uncertain create', async () => {
  const calls = [];
  const base = azure({}, calls);
  await assert.rejects(deploy(config, { sourceResolver: () => selectedSource, execAz(args, options) {
    if (args[0] === 'deployment' && args[2] === 'create') {
      calls.push(args);
      return { status: 1, stdout: '', stderr: 'timeout' };
    }
    return base(args, options);
  } }), /timeout/);
  assert.equal(calls.filter(args => args[0] === 'deployment' && args[2] === 'create').length, 1);
});
