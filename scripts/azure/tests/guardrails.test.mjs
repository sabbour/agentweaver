import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  assertDedicatedTarget,
  assertImageDigest,
  assertSubscription,
  assertTenant,
  assertDedicatedResourceGroupOwnership,
  guardAzureTarget,
  readFoundationOutputs,
} from '../lib/guardrails.mjs';
import { fixture, source, tags, fakeAzure, deploymentOutputs } from './fixtures/target.mjs';
import { postDeploymentAzure, postDeploymentInventory, postDeploymentDetails } from './fixtures/post-deployment.mjs';
import { nrmsNsgName, smartDetectorEvidence } from './fixtures/owned-p0-evidence.mjs';
import { plan } from '../plan.mjs';
import { deploy } from '../deploy.mjs';
import { runAcceptance } from '../verify-acceptance.mjs';

function expectedArmGuid(...values) {
  const namespace = Buffer.from('11fb06fb712d4ddd98c7e71bbd588830', 'hex');
  const bytes = createHash('sha1').update(namespace).update(values.join('-')).digest().subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function clonePostDeployment() {
  return structuredClone(postDeploymentAzure);
}

function guardPopulatedTarget(evidence = postDeploymentAzure, config = {}) {
  return guardAzureTarget({ ...fixture, ...source, ...config }, fakeAzure(evidence));
}

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
  const target = guardAzureTarget({ ...fixture, ...source }, fakeAzure(postDeploymentAzure, calls));
  assert.equal(calls.filter(args => args[0] === 'resource' && args[1] === 'show').length, 18);
  assert.equal(target.inventoryEvidence.privateDnsZones.rootCount, 7);
  assert.equal(target.inventoryEvidence.privateDnsZones.tagsPersisted, false);
  assert.equal(target.inventoryEvidence.privateDnsZones.taglessRootCount, 7);
  assert.equal(target.inventoryEvidence.privateDnsZones.sourceReceipt.successfulRootCreates, 7);
  assert.equal(target.inventoryEvidence.inheritedNetworkSecurityGroup.compliantPolicyStateCount, 8);
  assert.equal(target.inventoryEvidence.inheritedNetworkSecurityGroup.successfulRuleWriteEventCount, 8);
  assert.equal(target.inventoryEvidence.smartDetectorAlert.actionGroupCount, 0);
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], fixture.subscriptionId);
  assert.throws(() => guardAzureTarget({ ...fixture, ...source, sha: 'c'.repeat(40) },
    fakeAzure(postDeploymentAzure)), /exact SHA-derived source receipt/);
});

test('the same populated layout reaches plan, redeploy and configuration acceptance, never runtime success', () => {
  const dependencies = () => ({ sourceResolver: () => source, execAz: fakeAzure(postDeploymentAzure) });
  assert.equal(plan({ ...fixture, postgresEntraAdminObjectId: 'unreviewed-caller' }, dependencies()).status, 0);
  assert.equal(deploy({ ...fixture, execute: true }, dependencies()).executed, true);
  const report = runAcceptance({ ...fixture, deploymentName: `aw-v1-p0-${source.sha.slice(0, 12)}` }, dependencies());
  assert.ok(!report.checks.some(check => check.name === 'target-and-source'));
  assert.equal(report.checks.find(check => check.name === 'target-inventory').status, 'passed');
  assert.equal(report.checks.find(check => check.name === 'target-inventory').evidence.privateDnsZones.tagsPersisted, false);
  assert.equal(report.checks.find(check => check.name === 'deployed-sha').status, 'passed');
  assert.equal(report.overall, 'blocked');
});

test('the seven tagless private DNS roots require exact source, create-operation and owned-link proof', () => {
  const expectedTags = { ...tags, 'agentweaver:sourceSha': source.sha };
  const tagged = clonePostDeployment();
  for (const resource of tagged.resources.filter(item => item.type === 'Microsoft.Network/privateDnsZones')) {
    resource.tags = { ...expectedTags };
  }
  const taggedEvidence = guardPopulatedTarget(tagged).inventoryEvidence.privateDnsZones;
  assert.equal(taggedEvidence.tagsPersisted, true);
  assert.equal(taggedEvidence.taglessRootCount, 0);

  const deploymentNotInInventory = clonePostDeployment();
  deploymentNotInInventory.resources = deploymentNotInInventory.resources.filter(resource =>
    resource.name !== `aw-v1-p0-${source.sha.slice(0, 12)}`);
  assert.equal(guardPopulatedTarget(deploymentNotInInventory).inventoryEvidence.privateDnsZones.tagsPersisted, false);

  const conflictingTags = clonePostDeployment();
  const firstZone = conflictingTags.resources.find(item => item.type === 'Microsoft.Network/privateDnsZones');
  firstZone.tags = { 'agentweaver:environment': 'v1-p0', diagnostic: 'tag-secret-sentinel' };
  assert.throws(() => guardPopulatedTarget(conflictingTags), error => {
    assert.match(error.message, /conflicting or incomplete ownership tags/);
    assert.ok(!error.message.includes('tag-secret-sentinel'));
    return true;
  });

  const partialTags = clonePostDeployment();
  partialTags.resources.find(item => item.type === 'Microsoft.Network/privateDnsZones').tags = expectedTags;
  assert.throws(() => guardPopulatedTarget(partialTags), /partially missing/);

  const missingCreate = clonePostDeployment();
  missingCreate.deploymentOperations = missingCreate.deploymentOperations.slice(1);
  assert.throws(() => guardPopulatedTarget(missingCreate), /exactly one successful Create operation/);

  const duplicateCreate = clonePostDeployment();
  duplicateCreate.deploymentOperations.push(structuredClone(duplicateCreate.deploymentOperations[0]));
  assert.throws(() => guardPopulatedTarget(duplicateCreate), /exactly one successful Create operation/);

  const failedLink = clonePostDeployment();
  const link = failedLink.resources.find(item => item.type.endsWith('/virtualNetworkLinks'));
  failedLink.details[link.id].properties.virtualNetwork.id += '-external';
  assert.throws(() => guardPopulatedTarget(failedLink), /registration-disabled P0 link/);

  const enabledRegistration = clonePostDeployment();
  const registrationLink = enabledRegistration.resources.find(item => item.type.endsWith('/virtualNetworkLinks'));
  enabledRegistration.details[registrationLink.id].properties.registrationEnabled = true;
  assert.throws(() => guardPopulatedTarget(enabledRegistration), /registration-disabled P0 link/);
});

test('inherited NRMS NSG requires exact rules, reciprocal subnet links, compliant policy and successful child writes', () => {
  const baseline = clonePostDeployment();
  const nsgResource = baseline.resources.find(item => item.name === nrmsNsgName);
  const nsgId = nsgResource.id;
  const makeChanged = change => {
    const evidence = clonePostDeployment();
    change(evidence, evidence.details[nsgId]);
    return evidence;
  };
  const rejected = [
    [makeChanged((_evidence, nsg) => {
      nsg.properties.securityRules.find(rule => rule.name === 'NRMS-Rule-105')
        .properties.destinationPortRanges.pop();
    }), /exact shape/],
    [makeChanged((_evidence, nsg) => { delete nsg.properties.networkInterfaces; }),
      /network-interface association/],
    [makeChanged((_evidence, nsg) => { nsg.properties.networkInterfaces = null; }),
      /network-interface association/],
    [makeChanged((_evidence, nsg) => { nsg.properties.networkInterfaces = [{ id: `${nsgId}/nic` }]; }),
      /network-interface association/],
    [makeChanged((evidence, nsg) => {
      const subnetId = nsg.properties.subnets[0].id;
      evidence.details[subnetId].properties.networkSecurityGroup.id += '-external';
    }), /not reciprocal/],
    [makeChanged(evidence => { evidence.policyStates[0].complianceState = 'NonCompliant'; }),
      /exact compliant management-group assignment/],
    [makeChanged(evidence => {
      evidence.policyStates[0].policyDefinitionId = `${evidence.policyStates[0].policyDefinitionId}-other`;
    }), /exact compliant management-group assignment/],
    [makeChanged(evidence => {
      const ruleId = `${nsgId}/securityRules/NRMS-Rule-101`;
      evidence.activityEvents[ruleId] = [];
    }), /one successful child securityRules\/write event/],
    [makeChanged(evidence => {
      const ruleId = `${nsgId}/securityRules/NRMS-Rule-101`;
      evidence.activityEvents[ruleId][2].status.value = 'Failed';
    }), /one successful child securityRules\/write event/],
  ];
  for (const [evidence, message] of rejected) assert.throws(() => guardPopulatedTarget(evidence), message);
});

test('the owned Failure Anomalies rule rejects drift and external action-group links', () => {
  const alertResource = postDeploymentInventory.find(item => item.name === smartDetectorEvidence.name);
  const changeAlert = change => {
    const evidence = clonePostDeployment();
    change(evidence.details[alertResource.id].properties);
    return evidence;
  };
  for (const evidence of [
    changeAlert(properties => { properties.state = 'Disabled'; }),
    changeAlert(properties => { properties.severity = 'Sev2'; }),
    changeAlert(properties => { properties.detector.id = 'OtherDetector'; }),
    changeAlert(properties => { properties.frequency = 'PT5M'; }),
    changeAlert(properties => { properties.scope = [`${fixture.groupId}/external`]; }),
    changeAlert(properties => { properties.actionGroups.groupIds = ['/subscriptions/other/actionGroups/external']; }),
    changeAlert(properties => { properties.actionGroups.customEmailSubject = 'unexpected subject'; }),
  ]) assert.throws(() => guardPopulatedTarget(evidence), /exact isolated P0 Smart Detector/);
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

test('module deployment names are exact and historical deployment names require matching SHA/tree/hash outputs', () => {
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

test('historical receipt requires lowercase Git tree SHA before plan or redeploy, without repair fallback', () => {
  const outer = postDeploymentInventory.find(resource => resource.name === `aw-v1-p0-${source.sha.slice(0, 12)}`);
  const receipt = tree => ({ resources: [outer], create: {
    status: 0, stderr: '', stdout: JSON.stringify({ id: outer.id, properties: { outputs: {
      sourceSha: { value: source.sha }, sourceTree: { value: tree }, sourceHash: { value: source.sourceHash },
    } } }),
  } });
  assert.doesNotThrow(() => guardAzureTarget(fixture, fakeAzure(receipt(source.sourceTree))));
  for (const tree of [undefined, '', 'malformed', source.sourceHash, source.sourceTree.toUpperCase()]) {
    for (const operation of [plan, deploy]) {
      const calls = [];
      assert.throws(() => operation({ ...fixture, execute: true },
        { sourceResolver: () => source, execAz: fakeAzure(receipt(tree), calls) }), /source-bound receipt/);
      assert.ok(!calls.some(args => args.includes('what-if') || args.includes('create') || args.includes('delete')));
    }
    const calls = [];
    const report = runAcceptance(fixture, { sourceResolver: () => source, execAz: fakeAzure(receipt(tree), calls) });
    assert.equal(report.overall, 'blocked');
    assert.equal(report.deployedAcceptance, false);
    assert.equal(report.checks[0].name, 'target-and-source');
    assert.ok(!calls.some(args => args[0] === 'identity' || args[0] === 'storage'));
  }
});

test('role assignments must have the exact reviewed parent scope and ARM GUID', () => {
  for (const role of postDeploymentInventory.filter(resource => resource.type === 'Microsoft.Authorization/roleAssignments')) {
    assert.doesNotThrow(() => guardAzureTarget(fixture, fakeAzure({ resources: [role] })));
    for (const resource of [
      { ...role, id: role.id.replace(fixture.groupId, `${fixture.groupId}-other`) },
      { ...role, id: role.id.replace(role.name, fixture.tenantId), name: fixture.tenantId },
    ]) assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [resource] })), /unexpected|outside/);
  }
  const vaultId = `${fixture.groupId}/providers/Microsoft.KeyVault/vaults/aw-v1-p0-kv`;
  const clusterId = `${fixture.groupId}/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks`;
  const roleId = 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba';
  const name = expectedArmGuid(vaultId, clusterId, roleId);
  const routingRole = {
    id: `${vaultId}/providers/Microsoft.Authorization/roleAssignments/${name}`,
    type: 'Microsoft.Authorization/roleAssignments', name,
  };
  assert.doesNotThrow(() => guardAzureTarget(fixture, fakeAzure({ resources: [routingRole] })));
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({
    resources: [{ ...routingRole, id: routingRole.id.replace(name, fixture.tenantId), name: fixture.tenantId }],
  })), /unexpected/);

  const runtimeIdentityId =
    `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker`;
  const migrationIdentityId =
    `${fixture.groupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-identity-broker-migration`;
  const secretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6';
  const runtimeRoleName = expectedArmGuid(vaultId, runtimeIdentityId, secretsUserRoleId);
  const runtimeVaultRole = {
    id: `${vaultId}/providers/Microsoft.Authorization/roleAssignments/${runtimeRoleName}`,
    type: 'Microsoft.Authorization/roleAssignments', name: runtimeRoleName,
  };
  assert.doesNotThrow(() => guardAzureTarget(fixture, fakeAzure({ resources: [runtimeVaultRole] })));
  const migrationRoleName = expectedArmGuid(vaultId, migrationIdentityId, secretsUserRoleId);
  const migrationVaultRole = {
    id: `${vaultId}/providers/Microsoft.Authorization/roleAssignments/${migrationRoleName}`,
    type: 'Microsoft.Authorization/roleAssignments', name: migrationRoleName,
  };
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({ resources: [migrationVaultRole] })), /unexpected/);
});

test('custom App Routing DNS inventory admits only exact scoped roles and module deployments', () => {
  const zoneIds = [
    `${fixture.groupId}/providers/Microsoft.Network/dnsZones/apps.example.com`,
    `${fixture.groupId}/providers/Microsoft.Network/privateDnsZones/apps.internal.example`,
  ];
  const clusterId = `${fixture.groupId}/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks`;
  const resources = zoneIds.flatMap((zoneId, index) => {
    const zoneIsPrivate = zoneId.includes('/privateDnsZones/');
    const zoneName = zoneId.slice(zoneId.lastIndexOf('/') + 1);
    const roleId = zoneIsPrivate
      ? 'b12aa53e-6015-4669-85d0-8515ebb3ae7f'
      : 'befefa01-2a29-4197-83a8-272ff33ce314';
    const roleName = expectedArmGuid(zoneId, clusterId, roleId);
    const deploymentName = `aw-v1-p0-app-routing-dns-${index}`;
    return [
      { id: zoneId, type: zoneIsPrivate ? 'Microsoft.Network/privateDnsZones' : 'Microsoft.Network/dnsZones',
        name: zoneName },
      { id: `${zoneId}/providers/Microsoft.Authorization/roleAssignments/${roleName}`,
        type: 'Microsoft.Authorization/roleAssignments', name: roleName },
      { id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${deploymentName}`,
        type: 'Microsoft.Resources/deployments', name: deploymentName },
    ];
  });
  const config = { ...fixture, appRoutingDnsZoneResourceIds: zoneIds };
  assert.doesNotThrow(() => guardAzureTarget(config, fakeAzure({ resources })));
  const role = resources.find(resource => resource.type === 'Microsoft.Authorization/roleAssignments');
  assert.throws(() => guardAzureTarget(config, fakeAzure({
    resources: [{ ...role, id: role.id.replace(role.name, fixture.tenantId), name: fixture.tenantId }],
  })), /unexpected/);
  assert.throws(() => guardAzureTarget(fixture, fakeAzure({
    resources: [resources.find(resource => resource.type === 'Microsoft.Resources/deployments')],
  })), /unexpected/);
  assert.throws(() => guardAzureTarget(config, fakeAzure({
    resources: [{ ...resources[0], type: 'Microsoft.Network/virtualNetworks' }],
  })), /unexpected/);
});

test('external custom App Routing DNS zones require exact existing resource evidence', () => {
  const zoneIds = [
    `/subscriptions/${fixture.subscriptionId}/resourceGroups/public-dns/providers/Microsoft.Network/dnsZones/apps.example.com`,
    `/subscriptions/${fixture.subscriptionId}/resourceGroups/private-dns/providers/Microsoft.Network/privateDnsZones/apps.internal.example`,
  ];
  const details = Object.fromEntries(zoneIds.map((id, index) => [id, {
    id,
    type: index === 0 ? 'Microsoft.Network/dnsZones' : 'Microsoft.Network/privateDnsZones',
    name: index === 0 ? 'apps.example.com' : 'apps.internal.example',
  }]));
  const calls = [];
  assert.doesNotThrow(() => guardAzureTarget({ ...fixture, appRoutingDnsZoneResourceIds: zoneIds },
    fakeAzure({ details }, calls)));
  assert.equal(calls.filter(args => args[0] === 'resource' && args[1] === 'show').length, 2);
  for (const mismatch of [
    { ...details[zoneIds[0]], id: `${details[zoneIds[0]].id}-other` },
    { ...details[zoneIds[0]], type: 'Microsoft.Network/privateDnsZones' },
    { ...details[zoneIds[0]], name: 'other.example.com' },
  ]) {
    assert.throws(() => guardAzureTarget({ ...fixture, appRoutingDnsZoneResourceIds: [zoneIds[0]] },
      fakeAzure({ details: { [zoneIds[0]]: mismatch } })), /exact existing zone resource/);
  }
  assert.throws(() => guardAzureTarget({ ...fixture, appRoutingDnsZoneResourceIds: [zoneIds[0]] },
    fakeAzure({ details: {} })));
});

test('custom App Routing DNS inputs are validated before target reads', () => {
  const valid = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/dns-a/providers/Microsoft.Network/dnsZones/apps.example.com';
  for (const zoneIds of [
    ['not-an-arm-id'],
    [valid.replace('11111111-1111-1111-1111-111111111111', '99999999-9999-9999-9999-999999999999')],
    [valid.replace('apps.example.com', 'privatelink.example.com')],
    [valid, valid.toUpperCase()],
    [valid, valid.replace('/dns-a/', '/dns-b/')],
  ]) {
    const calls = [];
    assert.throws(() => guardAzureTarget({ ...fixture, appRoutingDnsZoneResourceIds: zoneIds },
      fakeAzure({}, calls)));
    assert.equal(calls.length, 0);
  }
});

test('deployment configuration requires exact resource IDs/endpoints and a named probe identity, not parallel array order', () => {
  const result = readFoundationOutputs(deploymentOutputs, fixture);
  assert.equal(result.foundationProbeIdentity.name, 'foundation-probe');
  assert.equal(result.identityBrokerRuntimeIdentity.name, 'identity-broker');
  assert.equal(result.identityBrokerMigrationIdentity.name, 'identity-broker-migration');
  assert.equal(result.appRoutingIdentity.objectId, deploymentOutputs.appRoutingIdentity.value.objectId);
  assert.equal(result.appRoutingDomain.managedDefaultRequested, true);
  assert.notEqual(result.resources.monitorWorkspaceId, result.resources.monitorWorkspaceResourceId);
  for (const key of Object.keys(deploymentOutputs.foundationResources.value)) {
    const outputs = structuredClone(deploymentOutputs);
    outputs.foundationResources.value[key] = 'unreviewed';
    assert.throws(() => readFoundationOutputs(outputs, fixture));
  }
  for (const key of Object.keys(deploymentOutputs.foundationProbeIdentity.value)) {
    const outputs = structuredClone(deploymentOutputs);
    outputs.foundationProbeIdentity.value[key] = 'unreviewed';
    assert.throws(() => readFoundationOutputs(outputs, fixture));
  }
  for (const name of ['identityBrokerRuntimeIdentity', 'identityBrokerMigrationIdentity']) {
    for (const key of Object.keys(deploymentOutputs[name].value)) {
      const outputs = structuredClone(deploymentOutputs);
      outputs[name].value[key] = 'unreviewed';
      assert.throws(() => readFoundationOutputs(outputs, fixture));
    }
  }
  const customZones = [
    `/subscriptions/${fixture.subscriptionId}/resourceGroups/dns/providers/Microsoft.Network/dnsZones/apps.example.com`,
  ];
  const customOutputs = structuredClone(deploymentOutputs);
  customOutputs.appRoutingDomain.value = { managedDefaultRequested: false, domainName: null };
  assert.equal(readFoundationOutputs(customOutputs, {
    ...fixture, appRoutingDnsZoneResourceIds: customZones,
  }).appRoutingDomain.domainName, null);
  for (const outputs of [
    {},
    { ...deploymentOutputs, appRoutingIdentity: undefined },
    { ...deploymentOutputs, foundationProbeIdentity: { value: deploymentOutputs.foundationProbeIdentity.value.clientId } },
    { ...deploymentOutputs, identityBrokerRuntimeIdentity: undefined },
    { ...deploymentOutputs, identityBrokerMigrationIdentity: undefined },
    { ...deploymentOutputs, identityBrokerMigrationIdentity: { value: {
      ...deploymentOutputs.identityBrokerMigrationIdentity.value,
      principalObjectId: deploymentOutputs.identityBrokerRuntimeIdentity.value.clientId,
    } } },
    { ...deploymentOutputs, appRoutingIdentity: { value: {
      ...deploymentOutputs.appRoutingIdentity.value,
      clientId: deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId,
    } } },
    { ...deploymentOutputs, serviceIdentityClientIds: { value: [fixture.tenantId] }, foundationProbeIdentity: undefined },
    { ...deploymentOutputs, monitorWorkspaceId: { value: fixture.groupId } },
    { ...deploymentOutputs, foundationProbeIdentity: { value: {
      ...deploymentOutputs.foundationProbeIdentity.value, principalObjectId: deploymentOutputs.foundationProbeIdentity.value.clientId,
    } } },
    { ...deploymentOutputs, appRoutingIdentity: { value: {
      ...deploymentOutputs.appRoutingIdentity.value, objectId: deploymentOutputs.appRoutingIdentity.value.clientId,
    } } },
    { ...deploymentOutputs, appRoutingIdentity: { value: {
      ...deploymentOutputs.appRoutingIdentity.value, objectId: deploymentOutputs.aksControlPlanePrincipalId.value,
    } } },
    { ...deploymentOutputs, appRoutingIdentity: { value: {
      ...deploymentOutputs.appRoutingIdentity.value,
      resourceId: deploymentOutputs.appRoutingIdentity.value.resourceId.replace(fixture.subscriptionId, fixture.tenantId),
    } } },
    { ...deploymentOutputs, appRoutingDomain: { value: { managedDefaultRequested: true, domainName: null } } },
    { ...deploymentOutputs, appRoutingDomain: { value: { managedDefaultRequested: false, domainName: 'test-only.invalid' } } },
  ]) assert.throws(() => readFoundationOutputs(outputs, fixture));
  assert.throws(() => readFoundationOutputs(customOutputs, fixture), /App Routing|Deployment outputs/);
});
