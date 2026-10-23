import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildDeployArgs, cliConfig, cliOptions, deploy } from '../deploy.mjs';
import {
  fixture, source, fakeAzure, ids, tags, deploymentOutputs,
} from './fixtures/target.mjs';

test('deployment uses supported source parameters, explicit subscription and incremental mode, never tags', () => {
  const args = buildDeployArgs({ ...source, resourceGroup: fixture.resourceGroup, sourceSha: source.sha,
    deploymentName: 'candidate', subscription: ids.subscriptionId });
  assert.ok(!args.includes('--tags'));
  assert.ok(args.includes(`sourceSha=${source.sha}`));
  assert.ok(args.includes(`sourceHash=${source.sourceHash}`));
  assert.ok(args.includes(`sourceTree=${source.sourceTree}`));
  assert.ok(!args.some(arg => arg.startsWith('operatorObjectId=') ||
    arg.startsWith('operatorRoleAssignmentName=')));
  assert.ok(args.includes('nodePoolCount=2'));
  assert.ok(args.includes('Incremental'));
  assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
  const aksOnlyArgs = buildDeployArgs({ ...source, scope: 'aks-only', resourceGroup: fixture.resourceGroup,
    sourceSha: source.sha, deploymentName: 'candidate', subscription: ids.subscriptionId });
  assert.ok(!aksOnlyArgs.some(arg => arg.startsWith('nodePoolCount=')));
});

test('offline dry-run never reads account or mutates', async () => {
  const result = await deploy(fixture, { sourceResolver: () => source, execAz: () => { throw new Error('cloud call'); } });
  assert.equal(result.executed, false);
  assert.equal(result.scope, 'infrastructure-only');
});

test('AKS-only deployment reads the cluster without submitting pool or AKS configuration', async () => {
  const az = fakeAzure();
  const calls = [];
  const result = await deploy({ ...fixture, execute: true }, {
    sourceResolver: () => ({ ...source, scope: 'aks-only' }),
    execAz(args, options) {
      calls.push(args);
      return az(args, options);
    },
  });
  assert.equal(result.receipt.autoscaling.countBefore, null);
  assert.equal(result.receipt.autoscaling.countSubmitted, null);
  assert.equal(result.receipt.autoscaling.minCount, 2);
  assert.equal(result.receipt.autoscaling.maxCount, 3);
  assert.equal(result.receipt.autoscaling.scaleUpProven, false);
  const whatIf = calls.findIndex(args => args.slice(0, 3).join(' ') === 'deployment group what-if');
  const create = calls.findIndex(args => args.slice(0, 3).join(' ') === 'deployment group create');
  assert.ok(whatIf < create);
  for (const args of calls.filter(args => ['what-if', 'create'].includes(args[2]))) {
    assert.ok(!args.some(arg => arg.startsWith('nodePoolCount=')));
  }
  assert.ok(!calls.some(args => args.slice(0, 3).join(' ') === 'aks nodepool show'));
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

test('Probe PostgreSQL bootstrap reads the principal it needs without comparing the resource ID', async () => {
  const steps = [];
  const identity = deploymentOutputs.foundationProbeIdentity.value;
  const result = await deploy({ ...fixture, execute: true, bootstrapFoundationProbePostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      identities: {
        'agentweaver-id-foundation-probe': {
          status: 0, stderr: '',
          stdout: JSON.stringify({ id: '/different/resource/id', principalId: identity.principalObjectId }),
        },
      },
    }),
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
  assert.deepEqual(steps, ['probe-postgres']);
  assert.deepEqual(result.receipt.foundationProbePostgresBootstrap,
    { database: 'agentweaver', schema: 'foundation_probe' });
  assert.equal(result.receipt.identityPostgresBootstrap, undefined);
});

test('Probe PostgreSQL bootstrap rejects a malformed principal input before opening transport', async () => {
  await assert.rejects(deploy({ ...fixture, execute: true, bootstrapFoundationProbePostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({
      identities: {
        'agentweaver-id-foundation-probe': {
          status: 0, stderr: '',
          stdout: JSON.stringify({ principalId: 'not-a-guid' }),
        },
      },
    }),
    initializeIdentityPostgres() { throw new Error('unexpected transport'); },
  }), /valid existing Probe principal ID/);
});

test('initial Broker state is default-off, non-mutating in plan, and separately wired', async () => {
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
    initializeIdentityBrokerState(config) {
      steps.push('state');
      assert.equal(config.subscriptionId, ids.subscriptionId);
      return { runtimeVerified: false };
    },
  });
  assert.deepEqual(steps, ['state']);
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
    const customName = 'custom-vault-01';
    const customSource = { ...source, keyVaultName: customName };
    const az = fakeAzure();
    const steps = [];
    await deploy({ ...fixture, ...runtime, bootstrapIdentityBrokerState: true, execute: true }, {
      sourceResolver: () => customSource,
      execAz(args, options) {
        const result = az(args, options);
        if (args[0] === 'deployment' && args[2] === 'create') {
          const deployment = JSON.parse(result.stdout);
          deployment.properties.outputs.foundationResources.value.keyVaultId =
            deployment.properties.outputs.foundationResources.value.keyVaultId.replace('agentweaver-kv', customName);
          deployment.properties.outputs.foundationResources.value.vaultUri = `https://${customName}.vault.azure.net/`;
          return { ...result, stdout: JSON.stringify(deployment) };
        }
        return result;
      },
      preflightIdentityBrokerRegistration() { return {
        callbackUri: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc',
        registrationReady: true, mutationExecuted: false,
      }; },
      initializeIdentityBrokerState() { steps.push('state'); },
      alignIdentityBrokerHostname() { steps.push('alignment'); },
      initializeIdentityPostgres(config) {
        steps.push('runtime-grants');
        assert.equal(config.runtimePrincipalObjectId, deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId);
        assert.equal(config.migrationPrincipalObjectId, deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId);
        return { database: 'agentweaver', schema: 'identity_broker' };
      },
      initializeIdentityBrokerRuntime(config) {
        assert.equal(config.keyVaultName, customName);
        steps.push('runtime');
      },
      initializeFoundationProbeInputs(config) {
        assert.equal(config.keyVaultName, customName);
        steps.push('probe');
      },
    });
    assert.deepEqual(steps, ['alignment', 'state', 'runtime-grants', 'runtime', 'probe']);
    await assert.rejects(deploy({ ...fixture, brokerImage: runtime.brokerImage }), /require --bootstrap/);
});

test('dry-run never invokes PostgreSQL bootstrap, even when requested', async () => {
  let postgresCalled = false;
  const result = await deploy({ ...fixture, bootstrapIdentityPostgres: true }, {
    sourceResolver: () => source,
    execAz: () => { throw new Error('cloud call'); },
    initializeIdentityPostgres: () => { postgresCalled = true; },
  });
  assert.equal(result.executed, false);
  assert.equal(result.bootstrapIdentityPostgres, true);
  assert.equal(postgresCalled, false);
});

test('runtime phase cannot reuse the earlier initial PostgreSQL bootstrap receipt', async () => {
    const steps = [];
    const initialReceipt = { phase: 'initial', runtimeGrants: 'pending' };
    const runtimeReceipt = { phase: 'runtime', runtimeGrants: 'applied' };
    const result = await deploy({
      ...fixture, execute: true, bootstrapIdentityPostgres: true,
      bootstrapIdentityBrokerState: true, bootstrapIdentityBrokerRuntime: true,
      upstreamClientId: ids.subscriptionId, acceptanceRunId: ids.subscriptionId,
      acceptanceRedirectUri: 'http://127.0.0.1:18641/callback',
      brokerImage: 'ghcr.io/sabbour/agentweaver.identity.broker@sha256:' + 'a'.repeat(64),
    }, {
      sourceResolver: () => source, execAz: fakeAzure(),
      preflightIdentityBrokerRegistration() { return {
        callbackUri: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc',
        registrationReady: true, mutationExecuted: false,
      }; },
      initializeIdentityPostgres(config) {
        assert.equal(config.runtimePrincipalObjectId,
          deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId);
        assert.equal(config.migrationPrincipalObjectId,
          deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId);
        const initial = !steps.includes('initial-postgres');
        steps.push(initial ? 'initial-postgres' : 'runtime-grants');
        return initial ? initialReceipt : runtimeReceipt;
      },
      initializeIdentityBrokerState() { steps.push('state'); },
      initializeIdentityBrokerRuntime() { steps.push('runtime'); return { configurationOnly: true }; },
    });
    assert.deepEqual(steps, ['initial-postgres', 'state', 'runtime-grants', 'runtime']);
    assert.equal(result.receipt.identityPostgresBootstrap, initialReceipt);
    assert.equal(result.receipt.identityPostgresRuntimeBootstrap, runtimeReceipt);
});

test('Identity routing is default-off with explicit placement, policy, and public client inputs', async () => {
  assert.equal(cliOptions['bootstrap-identity-routing'].default, false);
  assert.equal(cliConfig({}).bootstrapIdentityRouting, false);
  assert.equal(cliOptions['identity-routing-certificates-only'].default, false);
  assert.equal(cliConfig({}).identityRoutingCertificatesOnly, false);
  assert.equal(cliOptions['identity-gateway-namespace'].default, undefined);
  assert.equal(cliOptions['identity-gateway-security-policy'].default, undefined);
  assert.equal(cliOptions['verify-identity-broker-readiness'].default, false);
  assert.equal(cliOptions['register-identity-broker-callback'].default, false);
  assert.equal(cliOptions['confirm-identity-broker-callback'].default, undefined);
  const routing = { bootstrapIdentityRouting: true, gatewayNamespace: 'agentweaver-gateway',
    gatewaySecurityPolicy: 'baseline', upstreamClientId: ids.subscriptionId };
  const result = await deploy({ ...fixture, ...routing }, {
    sourceResolver: () => source,
    execAz: () => { throw new Error('dry-run cloud call'); },
    initializeIdentityRouting: () => { throw new Error('dry-run routing mutation'); },
  });
  assert.equal(result.executed, false);
  assert.equal(result.identityRoutingPlan.gatewaySecurityPolicy, 'baseline');
  assert.equal(result.identityRoutingPlan.certificatesOnly, false);
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
  const certificatePlan = await deploy({
    ...fixture, ...routing, identityRoutingCertificatesOnly: true,
  }, {
    sourceResolver: () => source,
    execAz: () => { throw new Error('dry-run certificate write'); },
    initializeIdentityRouting: () => { throw new Error('dry-run certificate mutation'); },
  });
  assert.equal(certificatePlan.identityRoutingPlan.certificatesOnly, true);
  const confirmedCertificatePlan = await deploy({
    ...fixture, ...routing, identityRoutingCertificatesOnly: true, registerBrokerCallback: true,
    confirmBrokerCallback: 'https://agentweaver.test.aksapp.io/signin-oidc',
  }, {
    sourceResolver: () => source, execAz: () => { throw new Error('dry-run registration write'); },
    initializeIdentityRouting: () => { throw new Error('dry-run certificate mutation'); },
  });
  assert.equal(confirmedCertificatePlan.identityRoutingPlan.appRegistrationMutationRequested, true);
  assert.equal(confirmedCertificatePlan.identityRoutingPlan.appRegistrationMutation, false);
  await assert.rejects(deploy({
    ...fixture, identityRoutingCertificatesOnly: true,
  }), /require the separate --bootstrap-identity-routing option/);
  await assert.rejects(deploy({
    ...fixture, ...routing, identityRoutingCertificatesOnly: true, verifyBrokerReadiness: true,
  }), /cannot verify Broker readiness/);
});

test('guarded routing runs without namespace bootstrap, PostgreSQL, or application mutation', async () => {
  const steps = [];
  const routing = { bootstrapIdentityRouting: true, gatewayNamespace: 'agentweaver-gateway',
    gatewaySecurityPolicy: 'baseline', upstreamClientId: ids.subscriptionId };
  const result = await deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source, execAz: fakeAzure(),
    preflightIdentityBrokerRegistration(config) {
      steps.push('preflight');
      assert.equal(config.upstreamClientId, routing.upstreamClientId);
      return { callbackUri: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc',
        registrationReady: true, mutationExecuted: false };
    },
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
  assert.deepEqual(steps, ['preflight', 'routing']);
  assert.equal(result.receipt.identityRouting.runtimeVerified, false);
  await assert.rejects(deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source, execAz: fakeAzure(),
    preflightIdentityBrokerRegistration() { return {
      callbackUri: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc',
      registrationReady: true, mutationExecuted: false,
    }; },
    initializeIdentityRouting: () => { throw new Error('Gateway not Programmed'); },
  }), /Identity routing bootstrap failed: Gateway not Programmed/);
});

test('certificate-only routing stage is passed only through the guarded identity-routing operation', async () => {
  const routing = { bootstrapIdentityRouting: true, identityRoutingCertificatesOnly: true,
    gatewayNamespace: 'agentweaver-gateway', gatewaySecurityPolicy: 'baseline',
    upstreamClientId: ids.subscriptionId, registerBrokerCallback: true,
    confirmBrokerCallback: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc' };
  const result = await deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source, execAz: fakeAzure(),
    preflightIdentityBrokerRegistration(config) {
      assert.equal(config.registerBrokerCallback, true);
      assert.equal(config.confirmBrokerCallback, routing.confirmBrokerCallback);
      return {
      callbackUri: 'https://agentweaver.test.eastus2.aksapp.io/signin-oidc',
      registrationReady: true, mutationExecuted: true,
      };
    },
    initializeIdentityRouting(config) {
      assert.equal(config.identityRoutingCertificatesOnly, true);
      assert.equal(config.registerBrokerCallback, true);
      return { certificatesConfigured: true, routingConfigured: false, runtimeVerified: false };
    },
  });
  assert.equal(result.receipt.identityRouting.certificatesConfigured, true);
  assert.equal(result.receipt.identityRouting.routingConfigured, false);
});

test('Broker preflight failure stops before what-if and infrastructure mutation', async () => {
  const calls = [];
  const routing = { bootstrapIdentityRouting: true, gatewayNamespace: 'agentweaver-gateway',
    gatewaySecurityPolicy: 'baseline', upstreamClientId: ids.subscriptionId };
  await assert.rejects(deploy({ ...fixture, ...routing, execute: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({}, calls),
    preflightIdentityBrokerRegistration() {
      throw new Error('The exact Entra application is not ready');
    },
  }), /The exact Entra application is not ready/);
  assert.ok(!calls.some(args => args.slice(0, 3).join(' ') === 'deployment group what-if'));
  assert.ok(!calls.some(args => args.slice(0, 3).join(' ') === 'deployment group create'));
});

test('deployment checks the approved account and resource group before what-if and create', async () => {
  const calls = [];
  const result = await deploy({ ...fixture, execute: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({}, calls),
    bootstrapNamespace: () => { throw new Error('namespace bootstrap must be a separate authorized action'); },
    initializeIdentityPostgres: () => { throw new Error('unexpected PostgreSQL bootstrap'); },
  });
  assert.equal(result.executed, true);
  assert.equal(result.receipt.sourceHash, source.sourceHash);
  assert.equal(result.receipt.sourceTree, source.sourceTree);
  assert.equal(result.receipt.foundationProbeIdentity.principalObjectId,
    deploymentOutputs.foundationProbeIdentity.value.principalObjectId);
  assert.equal(result.receipt.operatorRoleAssignmentId, undefined);
  assert.deepEqual(calls.map(args => args.slice(0, 3)), [
    ['account', 'show', '-o'], ['group', 'show', '--name'],
    ['deployment', 'group', 'what-if'], ['deployment', 'group', 'create'],
  ]);
  assert.ok(!calls.some(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'list'));
  const deploymentArgs = calls.find(args => args[0] === 'deployment' && args[2] === 'create');
  assert.ok(!deploymentArgs.some(arg => arg.startsWith('operatorObjectId=') ||
    arg.startsWith('operatorRoleAssignmentName=')));
  for (const args of calls) assert.equal(args[args.indexOf('--subscription') + 1], ids.subscriptionId);
});

test('explicit PostgreSQL bootstrap uses exact deployment identities without namespace bootstrap', async () => {
  const calls = [];
  const steps = [];
  const result = await deploy({ ...fixture, execute: true, bootstrapIdentityPostgres: true }, {
    sourceResolver: () => source,
    execAz: fakeAzure({}, calls),
    bootstrapNamespace: () => { throw new Error('namespace bootstrap must be a separate authorized action'); },
    initializeIdentityPostgres: options => {
      steps.push('postgres');
      assert.deepEqual(options, {
        repoRoot: fixture.repoRoot,
        resourceGroup: fixture.resourceGroup,
        subscriptionId: ids.subscriptionId,
        tenantId: ids.tenantId,
        clusterName: 'agentweaver-aks',
        postgresHost: deploymentOutputs.foundationResources.value.postgresHost,
        adminUsername: source.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId,
        migrationPrincipalObjectId: deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId,
      });
      return { database: 'agentweaver', schema: 'identity_broker' };
    },
  });
  assert.deepEqual(steps, ['postgres']);
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
    bootstrapNamespace: () => { throw new Error('namespace bootstrap must be a separate authorized action'); },
    initializeIdentityPostgres: options => {
      steps.push('postgres');
      assert.deepEqual(options, {
        repoRoot: fixture.repoRoot,
        resourceGroup: fixture.resourceGroup,
        subscriptionId: ids.subscriptionId,
        tenantId: ids.tenantId,
        clusterName: 'agentweaver-aks',
        postgresHost: 'agentweaver-pg.postgres.database.azure.com',
        adminUsername: source.postgresEntraAdminPrincipalName,
        runtimePrincipalObjectId: deploymentOutputs.identityBrokerRuntimeIdentity.value.principalObjectId,
        migrationPrincipalObjectId: deploymentOutputs.identityBrokerMigrationIdentity.value.principalObjectId,
      });
      return { database: 'agentweaver', schema: 'identity_broker', cleanup: { podRemoved: true } };
    },
  });
  assert.deepEqual(steps, ['postgres']);
  assert.equal(result.receipt.scope, 'aks-only');
  assert.equal(result.receipt.identityPostgresBootstrap.cleanup.podRemoved, true);
  assert.ok(!calls.some(args => args[0] === 'postgres' && args[1] === 'flexible-server' && args[2] === 'show'));
  assert.ok(!calls.some(args => args[0] === 'resource' && args[1] === 'show'));
  assert.ok(calls.some(args => args[0] === 'identity' && args[1] === 'show'));
  assert.ok(!calls.some(args => args[0] === 'deployment' && args[2] === 'create' &&
    args.includes('infra/bicep/main.bicep')));
  assert.ok(calls.some(args => args[0] === 'deployment' && args[2] === 'create' &&
    args.includes('infra/bicep/aks-redeploy.bicep')));
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
    { whatIf: { status: 1, stderr: 'AccessDenied', stdout: '' } },
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

test('guarded infrastructure deployment grants no human AKS access or namespace bootstrap', async () => {
  const scopedSource = {
    ...source, scope: 'aks-only', template: 'infra/bicep/aks-redeploy.bicep',
    parametersFile: 'infra/bicep/parameters/p0-aks-redeploy.approved.json',
  };
  const scenarios = [
    { config: fixture, source, scope: 'infrastructure-only' },
    {
      config: { ...fixture, template: scopedSource.template, parametersFile: scopedSource.parametersFile },
      source: scopedSource,
      scope: 'aks-only',
    },
  ];
  for (const scenario of scenarios) {
    const calls = [];
    const result = await deploy({ ...scenario.config, execute: true }, {
      sourceResolver: () => scenario.source,
      execAz: fakeAzure({}, calls),
      bootstrapNamespace: () => { throw new Error('namespace bootstrap must be a separate authorized action'); },
    });
    assert.equal(result.receipt.scope, scenario.scope);
    assert.equal(result.receipt.operatorRoleAssignmentId, undefined);
    const create = calls.find(args => args[0] === 'deployment' && args[2] === 'create');
    assert.ok(create);
    assert.ok(!create.some(arg => arg.startsWith('operatorObjectId=') ||
      arg.startsWith('operatorRoleAssignmentName=')));
    assert.ok(!calls.some(args => args[0] === 'role' && args[1] === 'assignment' && args[2] === 'list'));
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
