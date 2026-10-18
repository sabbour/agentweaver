import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  assertDedicatedTarget,
  assertApprovedKeyVaultName,
  assertImageDigest,
  assertSubscription,
  assertTenant,
  assertDedicatedResourceGroupOwnership,
  guardAzureTarget,
  readFoundationOutputs,
  readIdentityPostgresBootstrapValues,
} from '../lib/guardrails.mjs';
import { fixture, source, tags, fakeAzure, deploymentOutputs } from './fixtures/target.mjs';

/** Builds the shared preflight with the approved source and records every Azure command. */
function targetGuard(overrides = {}, config = {}, calls = []) {
  const target = guardAzureTarget({ ...fixture, ...source, ...config }, fakeAzure(overrides, calls));
  return { target, calls };
}

test('dedicated resource-group target accepts only the v1 P0 name', () => {
  assert.equal(assertDedicatedTarget('agentweaver'), 'agentweaver');
  for (const name of ['agentweaver-prod', 'agentweaver-integration', 'agentweaver_bad',
    'agentweaver-legacy', '', undefined, 'shared-rg']) {
    assert.throws(() => assertDedicatedTarget(name), /dedicated v1 P0 naming convention/);
  }
});

test('Key Vault target name must be configured and Azure-valid', () => {
  assert.equal(assertApprovedKeyVaultName('agentweaver', 'agentweaver-kv'), 'agentweaver-kv');
  assert.equal(assertApprovedKeyVaultName('agentweaver', 'custom-vault-01'), 'custom-vault-01');
  for (const name of [undefined, '', 'ab', 'a'.repeat(25), '1-vault', '-vault', 'vault-',
    'vault--name', 'vault_name']) {
    assert.throws(() => assertApprovedKeyVaultName('agentweaver', name), /Key Vault name must be configured/);
  }
});

test('image digest guard requires an immutable SHA-256 value', () => {
  const digest = `sha256:${'a'.repeat(64)}`;
  assert.equal(assertImageDigest(digest, 'broker'), digest);
  for (const value of [undefined, '', 'sha256:abc', 'latest', `sha1:${'a'.repeat(40)}`]) {
    assert.throws(() => assertImageDigest(value, 'broker'), /invalid sha256 digest/);
  }
});

test('subscription and tenant inputs must match their explicit allow-list values', () => {
  assert.throws(() => assertSubscription('sub-1', undefined), /No allowed subscription/);
  assert.throws(() => assertSubscription('sub-1', 'sub-2'), /Refusing subscription/);
  assert.equal(assertSubscription('sub-1', 'sub-1'), 'sub-1');
  assert.throws(() => assertTenant('tenant-1', undefined), /No allowed tenant/);
  assert.throws(() => assertTenant('tenant-1', 'tenant-2'), /Refusing tenant/);
  assert.equal(assertTenant('tenant-1', 'tenant-1'), 'tenant-1');
});

test('resource-group ownership tags are required for an existing target', () => {
  assert.throws(() => assertDedicatedResourceGroupOwnership('agentweaver', null));
  assert.throws(() => assertDedicatedResourceGroupOwnership('agentweaver', { owner: 'someone-else' }), /not tagged/);
  assert.throws(() => assertDedicatedResourceGroupOwnership('agentweaver', {
    'agentweaver:environment': 'v1-p1',
  }), /not tagged/);
  assert.doesNotThrow(() => assertDedicatedResourceGroupOwnership('agentweaver', tags));
});

test('target preflight validates the selected account and resource group without listing resources', () => {
  const calls = [];
  const { target } = targetGuard({}, {}, calls);
  assert.equal(target.group.name, fixture.resourceGroup);
  assert.deepEqual(target.targetEvidence, {
    subscriptionId: fixture.subscriptionId,
    tenantId: fixture.tenantId,
    resourceGroupId: fixture.groupId,
  });
  assert.deepEqual(calls.map(args => args.slice(0, 2)), [['account', 'show'], ['group', 'show']]);
  for (const args of calls) {
    assert.equal(args[args.indexOf('--subscription') + 1], fixture.subscriptionId);
  }
});

test('target input mismatches fail before Azure reads', () => {
  for (const [config, message] of [
    [{ subscriptionId: 'other' }, /Refusing subscription/],
    [{ allowedSubscriptionId: undefined }, /No allowed subscription/],
    [{ tenantId: 'other' }, /Refusing tenant/],
    [{ allowedTenantId: undefined }, /No allowed tenant/],
    [{ resourceGroup: 'shared-rg' }, /dedicated v1 P0 naming convention/],
    [{ keyVaultName: undefined }, /Key Vault name must be configured/],
  ]) {
    const calls = [];
    assert.throws(() => targetGuard({}, config, calls), message);
    assert.equal(calls.length, 0);
  }
});

test('target preflight blocks account, resource-group, and ownership failures', () => {
  for (const evidence of [
    { account: { state: 'Disabled' } },
    { account: { id: 'wrong' } },
    { accountResult: { status: 1, stderr: 'AccessDenied', stdout: '' } },
    { accountResult: { status: 0, stdout: 'null', stderr: '' } },
    { groupResult: { status: 1, stderr: '(AuthorizationFailed)', stdout: '' } },
    { groupResult: { status: 1, stderr: '(ResourceGroupNotFound)', stdout: '' } },
    { groupResult: { status: 0, stdout: 'malformed', stderr: '' } },
    { group: { id: '/subscriptions/wrong/resourceGroups/agentweaver' } },
    { group: { name: 'other' } },
    { group: { tags: null } },
  ]) {
    assert.throws(() => targetGuard(evidence));
  }
});

test('PostgreSQL bootstrap reads only the two identity principals it later uses', () => {
  const calls = [];
  const values = readIdentityPostgresBootstrapValues({ ...fixture, ...source }, fakeAzure({}, calls));
  assert.equal(values.resources.postgresHost, 'agentweaver-pg.postgres.database.azure.com');
  assert.equal(values.postgresEntraAdminPrincipalName, source.postgresEntraAdminPrincipalName);
  assert.equal(values.identityBrokerRuntimeIdentity.principalObjectId,
    deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId);
  assert.equal(values.identityBrokerMigrationIdentity.principalObjectId,
    deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId);
  assert.deepEqual(calls.map(args => args.slice(0, 2)), [['identity', 'show'], ['identity', 'show']]);
  assert.ok(!calls.some(args => args[0] === 'postgres' || args[0] === 'resource'));
});

test('PostgreSQL bootstrap validates administrator inputs and principal IDs before use', () => {
  const calls = [];
  assert.throws(() => readIdentityPostgresBootstrapValues({
    ...fixture, ...source, postgresEntraAdminObjectId: 'not-a-guid',
  }, fakeAzure({}, calls)), /approved foundation Entra administrator inputs/);
  assert.equal(calls.length, 0);

  const sharedPrincipal = deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId;
  const samePrincipal = {
    status: 0,
    stderr: '',
    stdout: JSON.stringify({ principalId: sharedPrincipal }),
  };
  assert.throws(() => readIdentityPostgresBootstrapValues({ ...fixture, ...source }, fakeAzure({
    identities: {
      'agentweaver-id-identity-broker': samePrincipal,
      'agentweaver-id-identity-broker-migration': samePrincipal,
    },
  })), /distinct PostgreSQL bootstrap principals/);
});

test('custom App Routing DNS IDs are validated before target reads', () => {
  const valid = `/subscriptions/${fixture.subscriptionId}/resourceGroups/dns-a/providers/Microsoft.Network/dnsZones/apps.example.com`;
  for (const zoneIds of [
    ['not-an-arm-id'],
    [valid.replace(fixture.subscriptionId, '99999999-9999-9999-9999-999999999999')],
    [valid.replace('apps.example.com', 'privatelink.example.com')],
    [valid, valid.toUpperCase()],
    [valid, valid.replace('/dns-a/', '/dns-b/')],
  ]) {
    const calls = [];
    assert.throws(() => targetGuard({}, { appRoutingDnsZoneResourceIds: zoneIds }, calls));
    assert.equal(calls.length, 0);
  }
});

test('foundation outputs provide the resource and identity values used by later steps', () => {
  const result = readFoundationOutputs(deploymentOutputs, { ...fixture, ...source });
  assert.equal(result.resources.clusterId, deploymentOutputs.foundationResources.value.clusterId);
  assert.equal(result.foundationProbeIdentity.principalObjectId,
    deploymentOutputs.foundationProbeIdentity.value.principalObjectId);
  assert.equal(result.identityBrokerRuntimeIdentity.principalObjectId,
    deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId);
  assert.equal(result.appRoutingIdentity.objectId, deploymentOutputs.appRoutingIdentity.value.objectId);
});
