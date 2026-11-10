import assert from 'node:assert/strict';
import { readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml, stringify } from 'yaml';
import {
  activateIdentityBrokerProviderSetup,
  buildIdentityBrokerProviderPlan,
  IdentityBrokerProviderSetupError,
  parseIdentityBrokerProviderInput,
  preserveIdentityBrokerProviderSetup,
} from '../lib/identity-broker-provider-setup.mjs';
import { applyTargetValues, targetValuePairs } from '../../release/target-values.mjs';
import { setupIdentityBrokerProviders } from '../deploy.mjs';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const input = {
  IdentityBroker: {
    WebOrigin: 'https://portal.example.test',
    CopilotConnection: {
      ProjectsOwnerAddress: 'https://projects.example.test/',
      ClientId: 'copilot-client-id',
      CallbackUri: 'https://portal.example.test/auth/github/copilot-app/callback',
      ClientSecretReference: {
        Id: 'copilot-client-secret',
        Version: '0123456789abcdef0123456789abcdef',
      },
    },
    GitHubRepoApp: {
      OAuthClientId: 'repo-app-client-id',
      CallbackUri: 'https://portal.example.test/auth/github/repo-app/callback',
      AppId: 12345,
      AppSlug: 'agentweaver',
      PrivateKeySecretId: 'repo-app-private-key',
      PrivateKeySecretVersion: '0123456789abcdef0123456789abcdef',
    },
  },
  SecretBindings: {
    IdentityBroker__GitHubRepoApp__OAuthClientSecret: {
      SecretKeyRef: { Name: 'github-repo-app-oauth', Key: 'oauth-client-secret' },
    },
  },
};

const target = {
  resourceGroup: 'agentweaver',
  subscriptionId: '00000000-0000-4000-8000-000000000001',
  allowedSubscriptionId: '00000000-0000-4000-8000-000000000001',
  tenantId: '00000000-0000-4000-8000-000000000002',
  allowedTenantId: '00000000-0000-4000-8000-000000000002',
  expectedConfigResourceVersion: 'config-rv-1',
  expectedDeploymentResourceVersion: 'deployment-rv-1',
  repoRoot,
};

const sourceMapPath = join(repoRoot, 'deploy', 'k8s', 'runtime-configs',
  'identity-broker-runtime-config.json');
const sourceDeploymentPath = join(repoRoot, 'deploy', 'k8s', 'base',
  'identity-broker', 'deployment.yaml');

function sourceObjects(pairs = []) {
  const configMap = JSON.parse(applyTargetValues(readFileSync(sourceMapPath, 'utf8'), pairs));
  const deployment = parseYaml(applyTargetValues(readFileSync(sourceDeploymentPath, 'utf8'), pairs));
  deployment.spec.template.spec.containers
    .find(container => container.name === 'identity-broker').image =
      `ghcr.io/sabbour/agentweaver.identity.broker@sha256:${'a'.repeat(64)}`;
  const rendered = [configMap, deployment].map(value => stringify(value)).join('---\n');
  return { configMap, deployment, rendered };
}

function liveResources({ activeMap, activeName, providers = false, drift = false, pairs } = {}) {
  const source = sourceObjects(pairs);
  const configMap = structuredClone(source.configMap);
  configMap.metadata.uid = 'base-map-uid';
  configMap.metadata.resourceVersion = target.expectedConfigResourceVersion;
  const deployment = structuredClone(source.deployment);
  deployment.metadata.uid = 'broker-deployment-uid';
  deployment.metadata.resourceVersion = target.expectedDeploymentResourceVersion;
  deployment.metadata.generation = 1;
  deployment.status = {
    observedGeneration: 1,
    updatedReplicas: 1,
    readyReplicas: 1,
    availableReplicas: 1,
  };
  if (activeName) {
    deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name = activeName;
  }
  if (providers && activeMap) {
    deployment.spec.template.spec.containers[0].env.push(...buildIdentityBrokerProviderPlan(input, repoRoot).deployment.env);
    deployment.spec.template.metadata.annotations = {
      ...(deployment.spec.template.metadata.annotations ?? {}),
      'agentweaver.io/provider-setup-fingerprint':
        buildIdentityBrokerProviderPlan(input, repoRoot).deployment.podTemplateAnnotations[
          'agentweaver.io/provider-setup-fingerprint'],
    };
  }
  if (drift) deployment.spec.replicas = 2;
  return { configMap, deployment, activeMap, source };
}

function createKubectlFixture(options = {}) {
  const state = liveResources(options);
  const events = [];
  const apiCalls = [];
  const maps = new Map([[state.configMap.metadata.name, state.configMap]]);
  if (options.activeMap) maps.set(options.activeMap.metadata.name, options.activeMap);
  let createdMap;
  let patchedBody;
  let patched = false;
  let rolloutOptions;
  const execKubectl = (args, settings = {}) => {
    if (args[0] === 'kustomize') {
      return { status: 0, stdout: state.source.rendered, stderr: '' };
    }
    if (args.includes('can-i')) return { status: 0, stdout: 'yes\n', stderr: '' };
    const command = args.findIndex(value => ['get', 'create', 'patch', 'rollout'].includes(value));
    const action = args[command];
    if (command >= 0) {
      assert.equal(args[args.indexOf('--namespace') + 1], 'agentweaver',
        `${action} must name the agentweaver namespace`);
      assert.ok(args.includes('--namespace'), `${action} must name the agentweaver namespace`);
      apiCalls.push({ args, settings });
    }
    if (action === 'get') {
      const resource = args[command + 1];
      const name = args[command + 2];
      if (resource === 'configmaps') {
        const map = maps.get(name);
        return { status: 0, stdout: map ? JSON.stringify(map) : '', stderr: '' };
      }
      if (resource === 'deployments') {
        return { status: 0, stdout: JSON.stringify(state.deployment), stderr: '' };
      }
    }
    if (action === 'create') {
      events.push('create-configmap');
      createdMap = JSON.parse(settings.input);
      createdMap.metadata.uid = 'new-provider-map-uid';
      createdMap.metadata.resourceVersion = 'provider-map-rv-1';
      maps.set(createdMap.metadata.name, createdMap);
      return { status: 0, stdout: `configmap/${createdMap.metadata.name}\n`, stderr: '' };
    }
    if (action === 'patch') {
      events.push('patch-deployment');
      if (options.failPatch) return { status: 1, stdout: '', stderr: 'redacted patch failure' };
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      patchedBody = patch;
      for (const operation of patch) {
        if (operation.path.endsWith('/configMapRef/name')) {
          state.deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name = operation.value;
        } else if (operation.path.endsWith('/env')) {
          state.deployment.spec.template.spec.containers[0].env = operation.value;
        } else if (operation.path === '/spec/template/metadata/annotations') {
          state.deployment.spec.template.metadata.annotations = operation.value;
        }
      }
      state.deployment.metadata.resourceVersion = 'deployment-rv-2';
      state.deployment.metadata.generation = 2;
      state.deployment.status = {
        observedGeneration: 2,
        updatedReplicas: 1,
        readyReplicas: 1,
        availableReplicas: 1,
      };
      patched = true;
      return { status: 0, stdout: JSON.stringify(state.deployment), stderr: '' };
    }
    if (action === 'rollout') {
      events.push('rollout');
      rolloutOptions = settings;
      if (options.failRollout) return { status: 1, stdout: '', stderr: 'redacted rollout failure' };
      return { status: 0, stdout: 'deployment successfully rolled out\n', stderr: '' };
    }
    throw new Error(`Unexpected kubectl command: ${args.slice(command).join(' ')}`);
  };
  const guarded = [];
  const kubeconfigs = [];
  const dependencies = {
    execKubectl,
    execAz: () => { throw new Error('Azure must not be used by the test.'); },
    guardTarget: value => { guarded.push(value); },
    withKubeconfig: (value, runAction) => { kubeconfigs.push(value); return runAction(['--kubeconfig', 'fixture']); },
  };
  return {
    state, maps, events, apiCalls, dependencies, guarded, kubeconfigs,
    get createdMap() { return createdMap; },
    get patched() { return patched; },
    get patchedBody() { return patchedBody; },
    get rolloutOptions() { return rolloutOptions; },
  };
}

function expectProviderError(action, code, field, redactedValue) {
  assert.throws(action, error => {
    assert.ok(error instanceof IdentityBrokerProviderSetupError);
    assert.equal(error.error, code);
    if (field) assert.equal(error.details.field, field);
    if (redactedValue) assert.equal(JSON.stringify(error.toJSON()).includes(redactedValue), false);
    return true;
  });
}

test('renders a deterministic offline plan with explicit secret references and registration URLs', async () => {
  const inputPath = join(dirname(fileURLToPath(import.meta.url)), `.provider-input-${process.pid}.json`);
  const outputPath = join(dirname(fileURLToPath(import.meta.url)), `.provider-plan-${process.pid}.json`);
  writeFileSync(inputPath, JSON.stringify(input), { flag: 'wx' });
  let externalCalls = 0;
  try {
    const result = await setupIdentityBrokerProviders({
      identityBrokerProviderConfig: inputPath,
      identityBrokerProviderMode: 'render',
      identityBrokerProviderOutput: outputPath,
      repoRoot,
      execute: false,
    }, {
      execAz: () => { externalCalls += 1; throw new Error('unexpected Azure access'); },
      execKubectl: () => { externalCalls += 1; throw new Error('unexpected Kubernetes access'); },
    });
    const plan = JSON.parse(readFileSync(outputPath, 'utf8'));
    const secretEnv = plan.deployment.env.find(entry =>
      entry.name === 'IdentityBroker__GitHubRepoApp__OAuthClientSecret');
    assert.equal(result.mode, 'render');
    assert.equal(plan.configMap.immutable, true);
    assert.equal(plan.configMap.data.IdentityBroker__WebOrigin, input.IdentityBroker.WebOrigin);
    assert.equal(secretEnv.value, undefined);
    assert.deepEqual(secretEnv.valueFrom.secretKeyRef, {
      name: 'github-repo-app-oauth', key: 'oauth-client-secret', optional: false,
    });
    assert.deepEqual(result.registrationChecklist.map(item => [item.field, item.value]), [
      ['Redirect URI', 'https://portal.example.test/auth/github/copilot-app/callback'],
      ['Redirect URI', 'https://portal.example.test/auth/github/repo-app/callback'],
      ['Setup URL', 'https://portal.example.test/auth/github/repo-app/callback'],
    ]);
    assert.equal(externalCalls, 0);
    assert.equal(readFileSync(outputPath, 'utf8'), `${JSON.stringify(plan, null, 2)}\n`);
  } finally {
    unlinkSync(inputPath);
    try { unlinkSync(outputPath); } catch { }
  }
});

test('rejects duplicate, unknown, empty, and incomplete provider input without echoing values', () => {
  expectProviderError(() => parseIdentityBrokerProviderInput(
    '{"IdentityBroker":{"WebOrigin":"https://one.test","WebOrigin":"https://two.test"}}'),
  'identity_broker_provider_input_invalid', '$');
  const unknown = structuredClone(input);
  unknown.IdentityBroker.GitHubRepoApp.PrivateKey = 'do-not-echo-this-value';
  expectProviderError(() => buildIdentityBrokerProviderPlan(unknown, repoRoot),
    'identity_broker_provider_input_invalid', 'IdentityBroker.GitHubRepoApp.PrivateKey',
    'do-not-echo-this-value');
  const empty = structuredClone(input);
  empty.IdentityBroker.CopilotConnection.ClientId = '';
  expectProviderError(() => buildIdentityBrokerProviderPlan(empty, repoRoot),
    'identity_broker_provider_input_invalid', 'IdentityBroker.CopilotConnection.ClientId');
  const partial = structuredClone(input);
  delete partial.IdentityBroker.GitHubRepoApp.AppSlug;
  expectProviderError(() => buildIdentityBrokerProviderPlan(partial, repoRoot),
    'identity_broker_provider_input_invalid', 'IdentityBroker.GitHubRepoApp.AppSlug');
});

test('validates owner roots and native Key Vault secret identifiers', () => {
  const nestedOwner = structuredClone(input);
  nestedOwner.IdentityBroker.CopilotConnection.ProjectsOwnerAddress =
    'https://projects.example.test/api/';
  expectProviderError(() => buildIdentityBrokerProviderPlan(nestedOwner, repoRoot),
    'identity_broker_provider_input_invalid',
    'IdentityBroker.CopilotConnection.ProjectsOwnerAddress');

  const invalidReferences = [
    ['URL-shaped ID', value => {
      value.IdentityBroker.CopilotConnection.ClientSecretReference.Id =
        'https://vault.example.test/secrets/client';
    }, 'IdentityBroker.CopilotConnection.ClientSecretReference.Id'],
    ['Key Vault path', value => {
      value.IdentityBroker.GitHubRepoApp.PrivateKeySecretId = 'repo/app-key';
    }, 'IdentityBroker.GitHubRepoApp.PrivateKeySecretId'],
    ['unsupported version character', value => {
      value.IdentityBroker.CopilotConnection.ClientSecretReference.Version = 'version?1';
    }, 'IdentityBroker.CopilotConnection.ClientSecretReference.Version'],
    ['oversized version', value => {
      value.IdentityBroker.GitHubRepoApp.PrivateKeySecretVersion = 'v'.repeat(257);
    }, 'IdentityBroker.GitHubRepoApp.PrivateKeySecretVersion'],
    ['oversized Key Vault name', value => {
      value.IdentityBroker.GitHubRepoApp.PrivateKeySecretId = 'a'.repeat(128);
    }, 'IdentityBroker.GitHubRepoApp.PrivateKeySecretId'],
  ];
  for (const [name, change, field] of invalidReferences) {
    const invalid = structuredClone(input);
    change(invalid);
    expectProviderError(() => buildIdentityBrokerProviderPlan(invalid, repoRoot),
      'identity_broker_provider_input_invalid', field);
  }

  const boundary = structuredClone(input);
  boundary.IdentityBroker.CopilotConnection.ClientSecretReference.Id = 'a'.repeat(127);
  boundary.IdentityBroker.CopilotConnection.ClientSecretReference.Version = 'v'.repeat(256);
  assert.doesNotThrow(() => buildIdentityBrokerProviderPlan(boundary, repoRoot));
});

test('offline render rejects an omitted provider section when the source has broad secret envFrom', () => {
  const omitted = structuredClone(input);
  delete omitted.IdentityBroker.GitHubRepoApp;
  delete omitted.SecretBindings;
  expectProviderError(() => buildIdentityBrokerProviderPlan(omitted, repoRoot),
    'identity_broker_provider_source_conflict', 'IdentityBroker.GitHubRepoApp');
});

test('activation creates the immutable map before a revision-fenced rollout and reads it back', async () => {
  const fixture = createKubectlFixture();
  const result = await activateIdentityBrokerProviderSetup(input, target, fixture.dependencies);
  assert.equal(result.changed, true);
  assert.equal(result.configMapCreated, true);
  assert.equal(fixture.createdMap.immutable, true);
  assert.equal(result.configMapUid, 'new-provider-map-uid');
  assert.equal(fixture.patched, true);
  assert.deepEqual(fixture.events, ['create-configmap', 'patch-deployment', 'rollout']);
  assert.equal(fixture.state.deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name,
    result.configMap);
  assert.deepEqual(fixture.state.deployment.spec.template.spec.containers[0].envFrom[1], {
    secretRef: { name: 'identity-broker-client-secrets', optional: true },
  });
  const secretEnv = fixture.state.deployment.spec.template.spec.containers[0].env.find(entry =>
    entry.name === 'IdentityBroker__GitHubRepoApp__OAuthClientSecret');
  assert.equal(secretEnv.value, undefined);
  assert.deepEqual(secretEnv.valueFrom.secretKeyRef, {
    name: 'github-repo-app-oauth', key: 'oauth-client-secret', optional: false,
  });
  assert.deepEqual(fixture.patchedBody.slice(0, 3), [
    { op: 'test', path: '/metadata/uid', value: 'broker-deployment-uid' },
    { op: 'test', path: '/metadata/resourceVersion', value: target.expectedDeploymentResourceVersion },
    {
      op: 'test',
      path: '/spec/template/spec/containers/0/envFrom/0/configMapRef/name',
      value: 'identity-broker-runtime-config-v3',
    },
  ]);
  assert.equal(fixture.apiCalls.every(call =>
    call.args.includes('--namespace') && call.args[call.args.indexOf('--namespace') + 1] === 'agentweaver'), true);
  assert.equal(fixture.rolloutOptions.timeout, 190_000);
});

test('a separate target renders, activates, and preserves provider setup from its own values', async () => {
  const pairs = targetValuePairs(repoRoot, 'awwus2np01');
  const separate = { ...target, resourceGroup: 'awwus2np01' };
  const vaultHost = 'IdentityBroker__SecretRedemption__VaultHost';
  assert.equal(buildIdentityBrokerProviderPlan(input, repoRoot, 'awwus2np01').configMap.data[vaultHost],
    'awwus2np01-kv1.vault.azure.net');
  expectProviderError(() => buildIdentityBrokerProviderPlan(input, repoRoot, 'other'),
    'identity_broker_provider_input_invalid', '--resource-group');

  const fixture = createKubectlFixture({ pairs });
  const result = await activateIdentityBrokerProviderSetup(input, separate, fixture.dependencies);
  assert.equal(result.changed, true);
  assert.equal(fixture.createdMap.data[vaultHost], 'awwus2np01-kv1.vault.azure.net');
  assert.equal(fixture.guarded[0].keyVaultName, 'awwus2np01-kv1');
  assert.equal(fixture.kubeconfigs[0].clusterName, 'awwus2np01-aks');

  const candidate = structuredClone(sourceObjects(pairs).deployment);
  const readConfigMap = name => fixture.maps.get(name);
  assert.equal(preserveIdentityBrokerProviderSetup([candidate], fixture.state.deployment, readConfigMap,
    repoRoot, 'awwus2np01').configMap, result.configMap);
  assert.throws(() => preserveIdentityBrokerProviderSetup([structuredClone(sourceObjects(pairs).deployment)],
    fixture.state.deployment, readConfigMap, repoRoot), IdentityBrokerProviderSetupError);

  const defaultSource = createKubectlFixture();
  await assert.rejects(activateIdentityBrokerProviderSetup(input, separate, defaultSource.dependencies),
    error => error.error === 'identity_broker_provider_source_conflict');
  assert.deepEqual(defaultSource.events, []);
  await assert.rejects(activateIdentityBrokerProviderSetup(input, { ...target, resourceGroup: 'other' },
    createKubectlFixture().dependencies), error => error.details.field === '--resource-group');
});

test('activation is a no-op for the exact current successor and rejects stale revisions or drift', async () => {
  const plan = buildIdentityBrokerProviderPlan(input, repoRoot);
  const activeMap = structuredClone(plan.configMap);
  activeMap.metadata.uid = 'active-map-uid';
  activeMap.metadata.resourceVersion = 'active-map-rv';
  const noOp = createKubectlFixture({ activeMap, activeName: activeMap.metadata.name, providers: true });
  const noOpResult = await activateIdentityBrokerProviderSetup(input, target, noOp.dependencies);
  assert.equal(noOpResult.changed, false);
  assert.deepEqual(noOp.events, []);

  const stale = createKubectlFixture();
  await assert.rejects(activateIdentityBrokerProviderSetup(input, {
    ...target, expectedConfigResourceVersion: 'stale',
  }, stale.dependencies), error => error.error === 'identity_broker_provider_revision_conflict');
  assert.deepEqual(stale.events, []);

  const drift = createKubectlFixture({ drift: true });
  await assert.rejects(activateIdentityBrokerProviderSetup(input, target, drift.dependencies),
    error => error.error === 'identity_broker_provider_source_conflict' &&
      error.details.field === 'Deployment.spec');
  assert.deepEqual(drift.events, []);
});

test('managed provider setup pins the OAuth Secret binding but allows non-secret updates', async () => {
  const plan = buildIdentityBrokerProviderPlan(input, repoRoot);
  const activeMap = structuredClone(plan.configMap);
  activeMap.metadata.uid = 'active-map-uid';
  activeMap.metadata.resourceVersion = 'active-map-rv';

  const changedBinding = structuredClone(input);
  changedBinding.SecretBindings.IdentityBroker__GitHubRepoApp__OAuthClientSecret
    .SecretKeyRef.Name = 'github-repo-app-oauth-next';
  const rejected = createKubectlFixture({
    activeMap, activeName: activeMap.metadata.name, providers: true,
  });
  await assert.rejects(activateIdentityBrokerProviderSetup(
    changedBinding, target, rejected.dependencies), error => {
    assert.equal(error.error, 'identity_broker_provider_source_conflict');
    assert.equal(error.details.field,
      'Deployment.env.IdentityBroker__GitHubRepoApp__OAuthClientSecret');
    return true;
  });
  assert.deepEqual(rejected.events, []);

  const nonSecretUpdate = structuredClone(input);
  nonSecretUpdate.IdentityBroker.CopilotConnection.ClientId = 'updated-copilot-client';
  const accepted = createKubectlFixture({
    activeMap, activeName: activeMap.metadata.name, providers: true,
  });
  const result = await activateIdentityBrokerProviderSetup(nonSecretUpdate, target, accepted.dependencies);
  assert.equal(result.changed, true);
  assert.equal(accepted.state.deployment.spec.template.spec.containers[0].env
    .find(entry => entry.name === 'IdentityBroker__CopilotConnection__ClientId').value,
  'updated-copilot-client');
  assert.deepEqual(accepted.state.deployment.spec.template.spec.containers[0].env
    .find(entry => entry.name === 'IdentityBroker__GitHubRepoApp__OAuthClientSecret')
    .valueFrom.secretKeyRef, {
    name: 'github-repo-app-oauth', key: 'oauth-client-secret', optional: false,
  });
});

test('a retry reports an existing provider rollout failure instead of returning no-op', async () => {
  const plan = buildIdentityBrokerProviderPlan(input, repoRoot);
  const activeMap = structuredClone(plan.configMap);
  activeMap.metadata.uid = 'active-map-uid';
  activeMap.metadata.resourceVersion = 'active-map-rv';
  const fixture = createKubectlFixture({ activeMap, activeName: activeMap.metadata.name, providers: true });
  fixture.state.deployment.status.readyReplicas = 0;
  fixture.state.deployment.status.availableReplicas = 0;

  await assert.rejects(activateIdentityBrokerProviderSetup(input, target, fixture.dependencies), error => {
    assert.equal(error.error, 'identity_broker_provider_activation_partial');
    assert.equal(error.details.phase, 'existing_rollout_not_ready');
    assert.equal(error.details.resources.activeConfigMap, activeMap.metadata.name);
    assert.equal(error.details.resources.configMapUid, activeMap.metadata.uid);
    assert.equal(error.details.resources.readyReplicas, 0);
    return true;
  });
  assert.deepEqual(fixture.events, []);
});

test('activation reports a failed patch only after safe readback and keeps the old reference', async () => {
  const fixture = createKubectlFixture({ failPatch: true });
  await assert.rejects(activateIdentityBrokerProviderSetup(input, target, fixture.dependencies), error => {
    assert.equal(error.error, 'identity_broker_provider_activation_partial');
    assert.equal(error.details.phase, 'activating_deployment');
    assert.equal(error.details.resources.activeConfigMap, 'identity-broker-runtime-config-v3');
    assert.equal(error.details.resources.deploymentResourceVersion, target.expectedDeploymentResourceVersion);
    assert.equal(JSON.stringify(error.toJSON()).includes('redacted patch failure'), false);
    return true;
  });
  assert.deepEqual(fixture.events, ['create-configmap', 'patch-deployment']);
  assert.equal(fixture.state.deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name,
    'identity-broker-runtime-config-v3');
});

test('activation reports the new active map and Deployment readback when rollout fails', async () => {
  const fixture = createKubectlFixture({ failRollout: true });
  const plan = buildIdentityBrokerProviderPlan(input, repoRoot);
  await assert.rejects(activateIdentityBrokerProviderSetup(input, target, fixture.dependencies), error => {
    assert.equal(error.error, 'identity_broker_provider_activation_partial');
    assert.equal(error.details.phase, 'waiting_for_rollout');
    assert.equal(error.details.resources.activeConfigMap, plan.configMap.metadata.name);
    assert.equal(error.details.resources.configMapUid, 'new-provider-map-uid');
    assert.equal(error.details.resources.deploymentResourceVersion, 'deployment-rv-2');
    assert.equal(error.details.resources.deploymentGeneration, 2);
    assert.equal(error.details.resources.observedGeneration, 2);
    assert.equal(JSON.stringify(error.toJSON()).includes('redacted rollout failure'), false);
    return true;
  });
  assert.deepEqual(fixture.events, ['create-configmap', 'patch-deployment', 'rollout']);
  assert.ok(fixture.maps.has('identity-broker-runtime-config-v3'));
  assert.equal(fixture.state.deployment.spec.template.spec.containers[0].envFrom[0].configMapRef.name,
    plan.configMap.metadata.name);
});
