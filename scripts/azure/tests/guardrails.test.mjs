import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  assertDedicatedTarget,
  assertImageDigest,
  assertSubscription,
  assertTenant,
  assertDedicatedResourceGroupOwnership,
  guardAzureTarget,
} from '../lib/guardrails.mjs';
import { fixture, source, tags, fakeAzure } from './fixtures/target.mjs';
import { postDeploymentAzure, postDeploymentInventory, postDeploymentDetails } from './fixtures/post-deployment.mjs';
import { plan } from '../plan.mjs';
import { deploy } from '../deploy.mjs';
import { runAcceptance } from '../verify-acceptance.mjs';

test('assertDedicatedTarget accepts the dedicated naming convention', () => {
  assert.equal(assertDedicatedTarget('aw-v1-p0'), 'aw-v1-p0');
  assert.equal(assertDedicatedTarget('aw-v1-p0-integration'), 'aw-v1-p0-integration');
});

test('assertDedicatedTarget rejects anything outside the convention', () => {
  for (const name of ['agentweaver-prod', 'aw-v1-p0_bad', 'AW-V1-P0', '', undefined, 'shared-rg']) {
    assert.throws(() => assertDedicatedTarget(name), /dedicated v1 P0 naming convention/);
  }
});

test('assertImageDigest accepts a well-formed sha256 digest', () => {
  const digest = `sha256:${'a'.repeat(64)}`;
  assert.equal(assertImageDigest(digest, 'broker'), digest);
});

test('assertImageDigest rejects missing, short, or fake digests', () => {
  for (const digest of [undefined, '', 'sha256:abc', 'latest', `sha1:${'a'.repeat(40)}`]) {
    assert.throws(() => assertImageDigest(digest, 'broker'), /invalid sha256 digest/);
  }
});

test('assertSubscription requires an allowed subscription to be configured', () => {
  assert.throws(() => assertSubscription('sub-1', undefined), /No allowed subscription/);
});

test('assertSubscription rejects any subscription that does not match', () => {
  assert.throws(() => assertSubscription('sub-1', 'sub-2'), /Refusing subscription/);
});

test('assertSubscription accepts an exact match', () => {
  assert.equal(assertSubscription('sub-1', 'sub-1'), 'sub-1');
});

test('assertTenant requires an allowed tenant to be configured', () => {
  assert.throws(() => assertTenant('tenant-1', undefined), /No allowed tenant/);
});

test('assertTenant rejects any tenant that does not match', () => {
  assert.throws(() => assertTenant('tenant-1', 'tenant-2'), /Refusing tenant/);
});

test('assertTenant accepts an exact match', () => {
  assert.equal(assertTenant('tenant-1', 'tenant-1'), 'tenant-1');
});

test('null tags mean untagged existing resource, not safe creation', () => {
  assert.throws(() => assertDedicatedResourceGroupOwnership('aw-v1-p0', null));
});

test('assertDedicatedResourceGroupOwnership allows an existing resource group tagged as v1-p0', () => {
  assert.doesNotThrow(() => assertDedicatedResourceGroupOwnership('aw-v1-p0', tags));
});

test('shared guard rejects disabled account, missing IDs, bad JSON and mismatched actual group', () => {
  for (const overrides of [
    { account: { state: 'Disabled' } },
    { accountResult: { status: 1, stderr: 'AccessDenied', stdout: '' } },
    { resourceResult: { status: 0, stdout: 'null', stderr: '' } },
    { resourceResult: { status: 0, stdout: '{}', stderr: '' } },
    { group: { id: '/subscriptions/wrong/resourceGroups/aw-v1-p0' } },
    { group: { name: 'other' } },
    { resources: [{ id: `${fixture.groupId}/providers/Microsoft.Storage/storageAccounts/old`,
      type: 'Microsoft.Storage/storageAccounts', name: 'old', tags }] },
    { resources: [{ id: `${fixture.groupId}/providers/Microsoft.Compute/virtualMachines/aw-v1-p0-vm`,
      type: 'Microsoft.Compute/virtualMachines', name: 'aw-v1-p0-vm', tags }] },
  ]) assert.throws(() => guardAzureTarget(fixture, fakeAzure(overrides)));
});

test('assertDedicatedResourceGroupOwnership rejects an existing resource group with no matching tag', () => {
  assert.throws(
    () => assertDedicatedResourceGroupOwnership('aw-v1-p0', { owner: 'someone-else' }),
    /not tagged/,
  );
});

test('assertDedicatedResourceGroupOwnership rejects an existing resource group tagged for a different environment', () => {
  assert.throws(
    () => assertDedicatedResourceGroupOwnership('aw-v1-p0', { 'agentweaver:environment': 'v1-p1' }),
    /not tagged/,
  );
});

test('populated post-deployment inventory admits exact roots, untagged children, deployments and generated PE NICs', () => {
  const calls = [];
  assert.doesNotThrow(() => guardAzureTarget({ ...fixture, ...source }, fakeAzure(postDeploymentAzure, calls)));
  assert.equal(calls.filter(args => args[0] === 'resource' && args[1] === 'show').length, 6);
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], fixture.subscriptionId);
  assert.doesNotThrow(() => guardAzureTarget({ ...fixture, ...source, sha: 'c'.repeat(40) },
    fakeAzure(postDeploymentAzure)));
});

test('the same populated layout reaches plan, redeploy and configuration acceptance, never runtime success', () => {
  const dependencies = () => ({ sourceResolver: () => source, execAz: fakeAzure(postDeploymentAzure) });
  assert.equal(plan({ ...fixture, postgresEntraAdminObjectId: 'unreviewed-caller' }, dependencies()).status, 0);
  assert.equal(deploy({ ...fixture, execute: true }, dependencies()).executed, true);
  const report = runAcceptance({ ...fixture, deploymentName: `aw-v1-p0-${source.sha.slice(0, 12)}` }, dependencies());
  assert.ok(!report.checks.some(check => check.name === 'target-and-source'));
  assert.equal(report.checks.find(check => check.name === 'deployed-sha').status, 'passed');
  assert.equal(report.overall, 'blocked');
});

test('inventory rejects wrong root IDs, root ownership, arbitrary children and unreviewed admin IDs', () => {
  const root = postDeploymentInventory[0];
  const child = postDeploymentInventory.find(resource => resource.type.endsWith('/containers'));
  const admin = postDeploymentInventory.find(resource => resource.type.endsWith('/administrators'));
  for (const resource of [
    { ...root, id: root.id.replace('aw-v1-p0-vnet', 'other-vnet') },
    { ...root, tags: null },
    { ...root, tags: { ...tags, 'agentweaver:owner': 'other' } },
    { ...child, id: child.id.replace('platform-artifacts', 'unreviewed'), name: 'awv1p0blob/default/unreviewed' },
    { ...admin, id: admin.id.replace(source.postgresEntraAdminObjectId, fixture.tenantId),
      name: `aw-v1-p0-pg/${fixture.tenantId}` },
    { ...child, type: 'Microsoft.Storage/storageAccounts/fileServices/shares' },
  ]) assert.throws(() => guardAzureTarget({ ...fixture, ...source }, fakeAzure({ resources: [resource] })));
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [admin] })), /unexpected/);
});

test('generated NIC admission requires reciprocal approved endpoint and exact subnet evidence', () => {
  const nic = postDeploymentInventory.find(resource => resource.type === 'Microsoft.Network/networkInterfaces');
  const endpointId = postDeploymentDetails[nic.id].properties.privateEndpoint.id;
  const endpointResource = postDeploymentInventory.find(resource => resource.id === endpointId);
  const nicDetail = postDeploymentDetails[nic.id];
  const endpoint = postDeploymentDetails[endpointId];
  for (const details of [
    { ...postDeploymentDetails, [nic.id]: { ...nicDetail, properties: {} } },
    { ...postDeploymentDetails, [nic.id]: { ...nicDetail, properties: {
      ...nicDetail.properties, privateEndpoint: { id: endpointId.replace('-kv-pe', '-unapproved-pe') } } } },
    { ...postDeploymentDetails, [nic.id]: { ...nicDetail, properties: {
      ...nicDetail.properties, ipConfigurations: [] } } },
    { ...postDeploymentDetails, [nic.id]: { ...nicDetail, properties: {
      ...nicDetail.properties, ipConfigurations: [{ properties: { subnet: { id: 'other-subnet' } } }] } } },
    { ...postDeploymentDetails, [endpointId]: { ...endpoint, properties: {
      ...endpoint.properties, subnet: { id: 'other-subnet' } } } },
    { ...postDeploymentDetails, [endpointId]: { ...endpoint, properties: {
      ...endpoint.properties, networkInterfaces: [{ id: `${nic.id}-other` }] } } },
    { ...postDeploymentDetails, [nic.id]: { ...nicDetail, id: `${nic.id}-other` } },
  ]) assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [endpointResource, nic], details })), /NIC/);
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [nic], details: postDeploymentDetails })), /NIC/);
  for (const detailResult of [
    { status: 1, stderr: 'AuthorizationFailed', stdout: '' },
    { status: 0, stderr: '', stdout: 'malformed' },
    { status: 0, stderr: '', stdout: 'null' },
  ]) assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [endpointResource, nic], detailResult })));
});

test('module deployment names are exact and historical deployment names require matching SHA/hash outputs', () => {
  const outer = postDeploymentInventory.find(resource => resource.name === `aw-v1-p0-${source.sha.slice(0, 12)}`);
  const module = postDeploymentInventory.find(resource => resource.name === 'aw-v1-p0-network');
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({
    resources: [{ ...module, name: 'arbitrary', id: module.id.replace(module.name, 'arbitrary') }],
  })), /unexpected/);
  for (const outputs of [{}, { sourceSha: { value: 'c'.repeat(40) }, sourceHash: { value: source.sourceHash } },
    { sourceSha: { value: source.sha }, sourceHash: { value: 'invalid' } }]) {
    assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [outer], create: {
      status: 0, stderr: '', stdout: JSON.stringify({ id: outer.id, properties: { outputs } }),
    } })), /source-bound receipt/);
  }
});

test('role assignments must have the exact reviewed parent scope and ARM GUID', () => {
  const role = postDeploymentInventory.find(resource => resource.type === 'Microsoft.Authorization/roleAssignments');
  for (const resource of [
    { ...role, id: role.id.replace('aw-v1-p0-kv', 'other-kv') },
    { ...role, id: role.id.replace(role.name, fixture.tenantId), name: fixture.tenantId },
  ]) assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [resource] })), /unexpected/);
});
