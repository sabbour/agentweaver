import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildDeployArgs, cliConfig, cliOptions, deploy } from '../deploy.mjs';
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

test('offline dry-run never reads account or mutates', async () => {
  const result = await deploy(fixture, { sourceResolver: () => source, execAz: () => { throw new Error('cloud call'); } });
  assert.equal(result.executed, false);
  assert.equal(result.scope, 'infrastructure-only');
});

test('stale subnet-role cleanup requires a separate explicit CLI authorization', () => {
  assert.equal(cliOptions['allow-stale-aks-subnet-role-cleanup'].type, 'boolean');
  assert.equal(cliOptions['allow-stale-aks-subnet-role-cleanup'].default, false);
  assert.equal(cliConfig({ 'allow-stale-aks-subnet-role-cleanup': true }).allowStaleAksSubnetRoleCleanup, true);
});

test('identity PostgreSQL bootstrap is a separate default-off deployment option', () => {
  assert.equal(cliOptions['bootstrap-identity-postgres'].type, 'boolean');
  assert.equal(cliOptions['bootstrap-identity-postgres'].default, false);
  assert.equal(cliConfig({}).bootstrapIdentityPostgres, false);
  assert.equal(cliConfig({ 'bootstrap-identity-postgres': true }).bootstrapIdentityPostgres, true);
});

test('Probe PostgreSQL bootstrap is independently default-off and never runs in a plan', async () => {
  assert.equal(cliOptions['bootstrap-foundation-probe-postgres'].default, false);
  assert.equal(cliConfig({}).bootstrapFoundationProbePostgres, false);
  assert.equal(cliConfig({ 'bootstrap-foundation-probe-postgres': true }).bootstrapFoundationProbePostgres, true);
  const result = await deploy({ ...fixture, bootstrapFoundationProbePostgres: true }, {
    sourceResolver: () => source, execAz() { throw new Error('plan cloud call'); },
    initializeIdentityPostgres() { throw new Error('plan PostgreSQL mutation'); },
  });
  assert.equal(result.bootstrapFoundationProbePostgres, true);
});

test('Probe PostgreSQL bootstrap uses its exact native identity after namespace setup', async () => {
  const steps = [];
  const identity = deploymentOutputs.foundationProbeIdentity.value;
  const result = await deploy({ ...fixture, execute: true, bootstrapFoundationProbePostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      identities: {
        'aw-v1-p0-id-foundation-probe': {
          status: 0, stderr: '',
          stdout: JSON.stringify({ id: identity.resourceId, principalId: identity.principalObjectId }),
        },
      },
    }),
    bootstrapNamespace() { steps.push('namespace'); },
    initializeIdentityPostgres(config) {
      steps.push('probe-postgres');
      assert.equal(config.foundationProbePrincipalObjectId, identity.principalObjectId);
      assert.equal(config.runtimePrincipalObjectId, deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId);
      assert.equal(config.migrationPrincipalObjectId, deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId);
      assert.equal(config.postgresHost, deploymentOutputs.foundationResources.value.postgresHost);
      assert.equal(config.adminUsername, source.postgresEntraAdminPrincipalName);
      return { database: 'agentweaver', schema: 'foundation_probe' };
    },
  });
  assert.deepEqual(steps, ['namespace', 'probe-postgres']);
  assert.deepEqual(result.receipt.foundationProbePostgresBootstrap,
    { database: 'agentweaver', schema: 'foundation_probe' });
  assert.equal(result.receipt.identityPostgresBootstrap, undefined);
});

test('Probe PostgreSQL bootstrap refuses a different native identity without opening transport', async () => {
  await assert.rejects(deploy({ ...fixture, execute: true, bootstrapFoundationProbePostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      identities: {
        'aw-v1-p0-id-foundation-probe': {
          status: 0, stderr: '',
          stdout: JSON.stringify({
            id: deploymentOutputs.identityBrokerRuntimeIdentity.value.resourceId,
            principalId: deploymentOutputs.foundationProbeIdentity.value.principalObjectId,
          }),
        },
      },
    }),
    bootstrapNamespace() {},
    initializeIdentityPostgres() { throw new Error('unexpected transport'); },
  }), /exact existing approved Probe principal/);
});

test('initial Broker state is default-off, non-mutating in plan, and separately wired after namespace', async () => {
  assert.equal(cliOptions['bootstrap-identity-broker-state'].default, false);
  assert.equal(cliConfig({}).bootstrapIdentityBrokerState, false);
  assert.equal(cliConfig({ 'bootstrap-identity-broker-state': true }).bootstrapIdentityBrokerState, true);
  const plan = await deploy({ ...fixture, bootstrapIdentityBrokerState: true }, {
    sourceResolver: () => source, execAz() { throw new Error('cloud call'); },
    initializeIdentityBrokerState() { throw new Error('dry-run state mutation'); },
  });
  assert.equal(plan.bootstrapIdentityBrokerState, true);
  const steps = [];
  const result = await deploy({ ...fixture, execute: true, bootstrapIdentityBrokerState: true }, {
    sourceResolver: () => source, execAz: fakeAzure(),
    bootstrapNamespace() { steps.push('namespace'); },
    initializeIdentityBrokerState(config) {
      steps.push('state');
      assert.equal(config.subscriptionId, ids.subscriptionId);
      return { runtimeVerified: false };
    },
  });
  assert.deepEqual(steps, ['namespace', 'state']);
  assert.equal(result.receipt.identityBrokerState.runtimeVerified, false);
});

test('runtime and Probe inputs are separately default-off and plans never invoke their installers', async () => {
    assert.equal(cliOptions['bootstrap-identity-broker-runtime'].default, false);
    assert.equal(cliOptions['bootstrap-foundation-probe-inputs'].default, false);
    assert.equal(cliOptions['align-existing-identity-broker-hostname'].default, false);
    const runtime = {
      bootstrapIdentityBrokerRuntime: true, bootstrapFoundationProbeInputs: true,
      alignExistingIdentityBrokerHostname: true,
      upstreamClientId: ids.subscriptionId, acceptanceRunId: ids.subscriptionId,
      acceptanceRedirectUri: 'http://127.0.0.1:18641/callback',
      brokerImage: 'ghcr.io/sabbour/agentweaver.identity.broker@sha256:' + 'a'.repeat(64),
    };
    const result = await deploy({ ...fixture, ...runtime }, {
      sourceResolver: () => source, execAz() { throw new Error('cloud call'); },
      initializeIdentityBrokerRuntime() { throw new Error('dry-run runtime write'); },
      initializeFoundationProbeInputs() { throw new Error('dry-run Probe write'); },
      alignIdentityBrokerHostname() { throw new Error('dry-run alignment write'); },
    });
    assert.equal(result.bootstrapIdentityBrokerRuntime, true);
    assert.equal(result.bootstrapFoundationProbeInputs, true);
    const steps = [];
    await deploy({ ...fixture, ...runtime, bootstrapIdentityBrokerState: true, execute: true }, {
      sourceResolver: () => source, execAz: fakeAzure(), bootstrapNamespace() { steps.push('namespace'); },
      initializeIdentityBrokerState() { steps.push('state'); },
      alignIdentityBrokerHostname() { steps.push('alignment'); },
      initializeIdentityBrokerRuntime() { steps.push('runtime'); },
      initializeFoundationProbeInputs() { steps.push('probe'); },
    });
    assert.deepEqual(steps, ['namespace', 'alignment', 'state', 'runtime', 'probe']);
    await assert.rejects(deploy({ ...fixture, brokerImage: runtime.brokerImage }), /require --bootstrap/);
});

test('dry-run never invokes namespace or PostgreSQL bootstrap, even when requested', async () => {
  let namespaceCalled = false;
  let postgresCalled = false;
  const result = await deploy({ ...fixture, bootstrapIdentityPostgres: true }, {
    sourceResolver: () => source,
    execAz: () => { throw new Error('cloud call'); },
    bootstrapNamespace: () => { namespaceCalled = true; },
    initializeIdentityPostgres: () => { postgresCalled = true; },
  });
  assert.equal(result.executed, false);
  assert.equal(result.bootstrapIdentityPostgres, true);
  assert.equal(namespaceCalled, false);
  assert.equal(postgresCalled, false);
});

test('Identity routing is default-off with explicit placement, policy, and public client inputs', async () => {
  assert.equal(cliOptions['bootstrap-identity-routing'].default, false);
  assert.equal(cliConfig({}).bootstrapIdentityRouting, false);
  assert.equal(cliOptions['identity-gateway-namespace'].default, undefined);
  assert.equal(cliOptions['identity-gateway-security-policy'].default, undefined);
  assert.equal(cliOptions['verify-identity-broker-readiness'].default, false);
  assert.equal(cliOptions['register-identity-broker-callback'].default, false);
  assert.equal(cliOptions['confirm-identity-broker-callback'].default, undefined);
  const routing = { bootstrapIdentityRouting: true, gatewayNamespace: 'agentweaver-v1-gateway',
    gatewaySecurityPolicy: 'baseline', upstreamClientId: ids.subscriptionId };
  const result = await deploy({ ...fixture, ...routing }, {
    sourceResolver: () => source,
    execAz: () => { throw new Error('dry-run cloud call'); },
    initializeIdentityRouting: () => { throw new Error('dry-run routing mutation'); },
  });
  assert.equal(result.executed, false);
  assert.equal(result.identityRoutingPlan.gatewaySecurityPolicy, 'baseline');
  assert.equal(result.identityRoutingPlan.appRegistrationMutation, false);
  await assert.rejects(deploy({ ...fixture, bootstrapIdentityRouting: true }), /requires explicit/);
  await assert.rejects(deploy({ ...fixture, gatewaySecurityPolicy: 'baseline' }), /require the separate/);
  await assert.rejects(deploy({ ...fixture, ...routing, registerBrokerCallback: true }), /requires --confirm/);
  await assert.rejects(deploy({ ...fixture, ...routing, confirmBrokerCallback: 'https://agentweaver.test.aksapp.io/signin-oidc' }),
    /confirmation requires/);
  const confirmedPlan = await deploy({ ...fixture, ...routing, registerBrokerCallback: true,
    confirmBrokerCallback: 'https://agentweaver.test.aksapp.io/signin-oidc' }, {
    sourceResolver: () => source, execAz: () => { throw new Error('dry-run cloud call'); },
    initializeIdentityRouting: () => { throw new Error('dry-run callback write'); },
  });
  assert.equal(confirmedPlan.identityRoutingPlan.appRegistrationMutationRequested, true);
  assert.equal(confirmedPlan.identityRoutingPlan.appRegistrationMutation, false);
});

test('guarded routing runs after namespace setup, without PostgreSQL or application mutation', async () => {
  const steps = [];
  const routing = { bootstrapIdentityRouting: true, gatewayNamespace: 'agentweaver-v1-gateway',
    gatewaySecurityPolicy: 'baseline', upstreamClientId: ids.subscriptionId };
  const result = await deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source, execAz: fakeAzure(),
    bootstrapNamespace: () => steps.push('namespace'),
    initializeIdentityPostgres: () => { throw new Error('unexpected migration/bootstrap'); },
    initializeIdentityRouting: config => {
      steps.push('routing');
      assert.equal(config.gatewayNamespace, routing.gatewayNamespace);
      assert.equal(config.gatewaySecurityPolicy, routing.gatewaySecurityPolicy);
      assert.equal(config.upstreamClientId, routing.upstreamClientId);
      assert.equal(config.subscriptionId, ids.subscriptionId);
      assert.equal(config.verifyBrokerReadiness, false);
      return { routingConfigured: true, runtimeVerified: false };
    },
  });
  assert.deepEqual(steps, ['namespace', 'routing']);
  assert.equal(result.receipt.identityRouting.runtimeVerified, false);
  await assert.rejects(deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source, execAz: fakeAzure(), bootstrapNamespace() {},
    initializeIdentityRouting: () => { throw new Error('Gateway not Programmed'); },
  }), /Identity routing bootstrap failed: Gateway not Programmed/);
});

test('deployment checks real account, group and resources, then what-if before create', async () => {
  const calls = [];
  const bootstrapCalls = [];
  const result = await deploy({ ...fixture, execute: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({}, calls),
    bootstrapNamespace: options => bootstrapCalls.push(options),
    initializeIdentityPostgres: () => { throw new Error('unexpected PostgreSQL bootstrap'); },
  });
  assert.equal(result.executed, true);
  assert.deepEqual(bootstrapCalls, [{
    resourceGroup: fixture.resourceGroup,
    subscriptionId: ids.subscriptionId,
    repoRoot: fixture.repoRoot,
    clusterName: 'aw-v1-p0-aks',
  }]);
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

test('explicit PostgreSQL bootstrap runs after namespace setup with exact deployment identities', async () => {
  const calls = [];
  const steps = [];
  const result = await deploy({ ...fixture, execute: true, bootstrapIdentityPostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({}, calls),
    bootstrapNamespace: () => steps.push('namespace'),
    initializeIdentityPostgres: options => {
      steps.push('postgres');
      assert.deepEqual(options, {
        repoRoot: fixture.repoRoot,
        resourceGroup: fixture.resourceGroup,
        subscriptionId: ids.subscriptionId,
        tenantId: ids.tenantId,
        clusterName: 'aw-v1-p0-aks',
        postgresHost: deploymentOutputs.foundationResources.value.postgresHost,
        adminUsername: source.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId,
        migrationPrincipalObjectId: deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId,
      });
      return { database: 'agentweaver', schema: 'identity_broker' };
    },
  });
  assert.deepEqual(steps, ['namespace', 'postgres']);
  assert.deepEqual(result.receipt.identityPostgresBootstrap,
    { database: 'agentweaver', schema: 'identity_broker' });
});

test('identity PostgreSQL bootstrap supports guarded AKS-only redeployment without replacing P0 resources', async () => {
  const aksSource = {
    ...source,
    scope: 'aks-only',
    template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const calls = [];
  const steps = [];
  const result = await deploy({ ...fixture, template: aksSource.template ?? 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
    execute: true, bootstrapIdentityPostgres: true }, {
    sourceResolver: () => aksSource,
    execAz: fakeAzure({}, calls),
    bootstrapNamespace: () => steps.push('namespace'),
    initializeIdentityPostgres: options => {
      steps.push('postgres');
      assert.deepEqual(options, {
        repoRoot: fixture.repoRoot,
        resourceGroup: fixture.resourceGroup,
        subscriptionId: ids.subscriptionId,
        tenantId: ids.tenantId,
        clusterName: 'aw-v1-p0-aks',
        postgresHost: 'aw-v1-p0-pg.postgres.database.azure.com',
        adminUsername: source.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId,
        migrationPrincipalObjectId: deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId,
      });
      return { database: 'agentweaver', schema: 'identity_broker', cleanup: { podRemoved: true } };
    },
  });
  assert.deepEqual(steps, ['namespace', 'postgres']);
  assert.equal(result.receipt.scope, 'aks-only');
  assert.equal(result.receipt.identityPostgresBootstrap.cleanup.podRemoved, true);
  assert.ok(calls.some(args => args[0] === 'postgres' && args[1] === 'flexible-server' && args[2] === 'show'));
  assert.ok(calls.some(args => args[0] === 'identity' && args[1] === 'show'));
  assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create' &&
    args.includes('infra/bicep/main.bicep')));
  assert.ok(calls.some(args => args[0] === 'deployment' && args[2] === 'create' &&
    args.includes('infra/bicep/aks-redeploy.bicep')));
});

test('AKS-only bootstrap refuses altered PostgreSQL network or authentication metadata before deployment', async () => {
  const aksSource = {
    ...source,
    scope: 'aks-only',
    template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const server = {
    id: deploymentOutputs.foundationResources.value.postgresServerId,
    name: 'aw-v1-p0-pg',
    location: source.location,
    version: '16',
    fullyQualifiedDomainName: deploymentOutputs.foundationResources.value.postgresHost,
    network: {
      delegatedSubnetResourceId:
        `${fixture.groupId}/providers/Microsoft.Network/virtualNetworks/aw-v1-p0-vnet/subnets/postgres`,
      privateDnsZoneArmResourceId:
        `${fixture.groupId}/providers/Microsoft.Network/privateDnsZones/privatelink.postgres.database.azure.com`,
      publicNetworkAccess: 'Disabled',
    },
    authConfig: { activeDirectoryAuth: 'Enabled', passwordAuth: 'Disabled', tenantId: ids.tenantId },
  };
  for (const altered of [
    { ...server, network: { ...server.network, delegatedSubnetResourceId: `${fixture.groupId}/subnets/unapproved` } },
    { ...server, network: { ...server.network, privateDnsZoneArmResourceId: `${fixture.groupId}/privateDnsZones/unapproved` } },
    { ...server, network: { ...server.network, publicNetworkAccess: 'Enabled' } },
    { ...server, authConfig: { ...server.authConfig, passwordAuth: 'Enabled' } },
    { ...server, authConfig: { ...server.authConfig, tenantId: '33333333-3333-4333-8333-333333333333' } },
  ]) {
    const calls = [];
    await assert.rejects(deploy({
      ...fixture,
      template: aksSource.template,
      parametersFile: aksSource.parametersFile,
      execute: true,
      bootstrapIdentityPostgres: true,
    }, {
      sourceResolver: () => aksSource,
      execAz: fakeAzure({ postgresServer: { status: 0, stdout: JSON.stringify(altered), stderr: '' } }, calls),
    }), /exact approved private P0 source inputs/);
    assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create'));
  }
});

test('source/account/ownership/plan/deployment failures never report a deployment receipt', async () => {
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
    await assert.rejects(deploy({ ...fixture, execute: true },
      { sourceResolver: () => source, execAz: fakeAzure(overrides, calls) }));
    assert.ok(!calls.some(args => args[2] === 'create'));
  }
  await assert.rejects(deploy({ ...fixture, execute: true }, {
    sourceResolver: () => { throw new Error('changed input'); }, execAz: () => { throw new Error('unexpected'); },
  }), /changed input/);
  await assert.rejects(deploy({ ...fixture, execute: true }, { sourceResolver: () => source,
    execAz: fakeAzure({ create: { status: 0, stdout: '{"properties":{"provisioningState":"Failed"}}' } }) }),
  /source-bound/);
});

test('full foundation execution refuses to rewrite an existing protected P0 resource', async () => {
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
    await assert.rejects(deploy({ ...fixture, execute: true }, {
      sourceResolver: () => source,
      execAz: fakeAzure({ resources: [protectedResource] }, calls),
    }), /empty dedicated P0 resource group/);
    assert.ok(!calls.some(args => args.includes('what-if') || args.includes('create')));
  }
});

test('scoped redeploy resolves and reuses the exact existing Cluster Admin assignment', async () => {
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
  const result = await deploy({ ...scopedFixture, execute: true }, {
    sourceResolver: () => scopedSource,
    execAz: fakeAzure({ operatorAssignments: { status: 0, stdout: JSON.stringify(assignments), stderr: '' } }, calls),
    bootstrapNamespace: options => {
      assert.deepEqual(options, {
        resourceGroup: fixture.resourceGroup,
        subscriptionId: ids.subscriptionId,
        repoRoot: scopedFixture.repoRoot,
        clusterName: 'aw-v1-p0-aks',
      });
    },
  });
  const create = calls.find(args => args[0] === 'deployment' && args[2] === 'create');
  assert.ok(create.includes(`operatorRoleAssignmentName=${existingName}`));
  assert.equal(result.receipt.scope, 'aks-only');
  assert.equal(result.receipt.clusterId, clusterId);
  assert.equal(result.receipt.operatorRoleAssignmentId, assignmentId);
  assert.equal(calls.filter(args => args[0] === 'deployment' && args[2] === 'create').length, 1);
});

test('full-foundation and AKS-only deployments report namespace bootstrap failure after infrastructure success', async () => {
  const aksSource = {
    ...source,
    scope: 'aks-only',
    template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const scenarios = [
    { config: fixture, source },
    {
      config: { ...fixture, template: aksSource.template, parametersFile: aksSource.parametersFile },
      source: aksSource,
    },
  ];
  for (const scenario of scenarios) {
    const calls = [];
    await assert.rejects(deploy({ ...scenario.config, execute: true }, {
      sourceResolver: () => scenario.source,
      execAz: fakeAzure({}, calls),
      bootstrapNamespace: () => { throw new Error('RBAC denied'); },
    }), /Infrastructure deployment succeeded, but namespace-only bootstrap failed: RBAC denied/);
    assert.ok(calls.some(args => args[0] === 'deployment' && args[2] === 'create'));
  }
});

test('default execute refuses a proven dangling subnet role without deleting it', async () => {
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
  await assert.rejects(deploy({ ...fixture, execute: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      networkRoleAssignment: { status: 0, stdout: JSON.stringify(stale), stderr: '' },
      aksShow: { status: 1, stdout: '', stderr: '(ResourceNotFound) Managed cluster was not found.' },
      servicePrincipal: { status: 1, stdout: '', stderr: `ERROR: Resource '${stale.properties.principalId}' does not exist.` },
    }, calls),
  }), /pass --allow-stale-aks-subnet-role-cleanup/);
  assert.ok(!calls.some(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'delete'));
  assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create'));
});

test('only an exact dangling subnet Network Contributor assignment is removed with explicit authorization', async () => {
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
  await deploy({ ...fixture, execute: true, allowStaleAksSubnetRoleCleanup: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      networkRoleAssignment: { status: 0, stdout: JSON.stringify(stale), stderr: '' },
      aksShow: { status: 1, stdout: '', stderr: '(ResourceNotFound) Managed cluster was not found.' },
      servicePrincipal: { status: 1, stdout: '', stderr: `ERROR: Resource '${stale.properties.principalId}' does not exist.` },
    }, calls),
    bootstrapNamespace() {},
  });
  const remove = calls.find(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'delete');
  assert.ok(remove);
  assert.deepEqual(remove.slice(0, 5), ['role', 'assignment', 'delete', '--ids', subnetAssignmentId]);
  const whatIfCalls = calls.filter(args => args[2] === 'what-if');
  assert.equal(whatIfCalls.length, 2);
  assert.ok(calls.indexOf(remove) < calls.indexOf(whatIfCalls[1]));
});

test('a live or unverifiable subnet role principal is never removed', async () => {
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
    await assert.rejects(deploy({ ...fixture, execute: true, allowStaleAksSubnetRoleCleanup: true }, {
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

test('changed inputs after what-if never reach create', async () => {
  for (const change of [{ sourceHash: 'c'.repeat(64) }, { sourceTree: 'd'.repeat(40) }]) {
    let resolves = 0;
    const calls = [];
    await assert.rejects(deploy({ ...fixture, execute: true }, {
      sourceResolver: () => (++resolves === 1 ? source : { ...source, ...change }),
      execAz: fakeAzure({}, calls),
    }), /changed after what-if/);
    assert.ok(!calls.some(args => args[2] === 'create'));
  }
});

test('deployment cannot publish a source-bound receipt with missing or substituted Git tree', async () => {
  for (const value of [undefined, source.sourceHash, 'd'.repeat(40)]) {
    await assert.rejects(deploy({ ...fixture, execute: true }, { sourceResolver: () => source,
      execAz: fakeAzure({ create: { status: 0, stdout: JSON.stringify({
        id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
        properties: { provisioningState: 'Succeeded', outputs: {
          ...deploymentOutputs, sourceTree: { value },
        } },
      }) } }),
    }), /source-bound/);
  }
});

test('deployment receipt rejects substituted resources, identity, workspace and deployment scope', async () => {
  const valid = { id: `${fixture.groupId}/providers/Microsoft.Resources/deployments/${fixture.deploymentName}`,
    properties: { provisioningState: 'Succeeded', outputs: deploymentOutputs } };
  const variants = [
    { ...valid, id: valid.id.replace(fixture.groupId, `${fixture.groupId}-other`) },
    { ...valid, properties: { ...valid.properties, outputs: { ...deploymentOutputs, foundationProbeIdentity: undefined } } },
    { ...valid, properties: { ...valid.properties, outputs: { ...deploymentOutputs, monitorWorkspaceId: { value: fixture.groupId } } } },
  ];
  for (const response of variants) {
    const execAz = fakeAzure({ create: { status: 0, stdout: JSON.stringify(response) } });
    await assert.rejects(deploy({ ...fixture, execute: true }, { sourceResolver: () => source, execAz }));
  }
});
