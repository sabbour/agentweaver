import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parse as parseYaml } from 'yaml';
import { guardAzureTarget } from './guardrails.mjs';
import { redact, run, runAz } from './exec.mjs';
import { assertP0KubernetesPermissions, withP0UserKubeconfig } from './namespace-bootstrap.mjs';
import { brokerObjectHash } from './identity-broker-runtime.mjs';
import { parseKustomizeResources } from '../../release/kustomize-runtime.mjs';
import { applyTargetValues, targetValuePairs } from '../../release/target-values.mjs';
import { installTarget } from './targets.mjs';

const NAMESPACE = 'agentweaver';
const BASE_CONFIG_MAP = 'identity-broker-runtime-config-v3';
const PROVIDER_SETUP_ANNOTATION = 'agentweaver.io/provider-setup-fingerprint';
const SECRET_ENV_NAME = 'IdentityBroker__GitHubRepoApp__OAuthClientSecret';
const PROVIDER_ENV_PREFIXES = [
  'IdentityBroker__WebOrigin',
  'IdentityBroker__CopilotConnection__',
  'IdentityBroker__GitHubRepoApp__',
];
const PROVIDER_FIELDS = {
  WebOrigin: 'IdentityBroker__WebOrigin',
  'CopilotConnection.ProjectsOwnerAddress':
    'IdentityBroker__CopilotConnection__ProjectsOwnerAddress',
  'CopilotConnection.ClientId': 'IdentityBroker__CopilotConnection__ClientId',
  'CopilotConnection.CallbackUri': 'IdentityBroker__CopilotConnection__CallbackUri',
  'CopilotConnection.ClientSecretReference.Id':
    'IdentityBroker__CopilotConnection__ClientSecretReference__Id',
  'CopilotConnection.ClientSecretReference.Version':
    'IdentityBroker__CopilotConnection__ClientSecretReference__Version',
  'GitHubRepoApp.OAuthClientId': 'IdentityBroker__GitHubRepoApp__OAuthClientId',
  'GitHubRepoApp.CallbackUri': 'IdentityBroker__GitHubRepoApp__CallbackUri',
  'GitHubRepoApp.AppId': 'IdentityBroker__GitHubRepoApp__AppId',
  'GitHubRepoApp.AppSlug': 'IdentityBroker__GitHubRepoApp__AppSlug',
  'GitHubRepoApp.PrivateKeySecretId':
    'IdentityBroker__GitHubRepoApp__PrivateKeySecretId',
  'GitHubRepoApp.PrivateKeySecretVersion':
    'IdentityBroker__GitHubRepoApp__PrivateKeySecretVersion',
};
const COPILOT_FIELDS = Object.keys(PROVIDER_FIELDS).filter(key => key.startsWith('CopilotConnection.'));
const REPO_APP_FIELDS = Object.keys(PROVIDER_FIELDS).filter(key => key.startsWith('GitHubRepoApp.'));
const ENV_NAMES = new Set([...Object.values(PROVIDER_FIELDS), SECRET_ENV_NAME]);
const RESOURCE_OPTIONS = { check: false, timeout: 35_000 };
const REVISION_FLAG_FIELDS = {
  config: '--expected-identity-broker-config-resource-version',
  deployment: '--expected-identity-broker-deployment-resource-version',
};

export class IdentityBrokerProviderSetupError extends Error {
  constructor(error, details = {}) {
    super(error);
    this.error = error;
    this.details = details;
  }

  toJSON() {
    return { error: this.error, ...this.details };
  }
}

function fail(error, details) {
  throw new IdentityBrokerProviderSetupError(error, details);
}

function invalid(field) {
  fail('identity_broker_provider_input_invalid', { field });
}

function conflict(field) {
  fail('identity_broker_provider_source_conflict', { field });
}

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function assertKeys(value, allowed, required, path) {
  if (!isRecord(value)) invalid(path);
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) invalid(`${path}.${key}`);
  }
  for (const key of required) {
    if (!Object.hasOwn(value, key)) invalid(`${path}.${key}`);
  }
}

function requiredText(value, field, max = 2048) {
  if (typeof value !== 'string' || value.length === 0 || value.length > max ||
      value.trim() !== value || /[\u0000-\u001f\u007f]/.test(value)) invalid(field);
  return value;
}

function secretIdentifier(value, field) {
  const text = requiredText(value, field, 256);
  if (!/^[A-Za-z0-9._:-]+$/.test(text)) invalid(field);
  return text;
}

function keyVaultSecretName(value, field) {
  const text = requiredText(value, field, 127);
  if (!/^[A-Za-z0-9-]+$/.test(text)) invalid(field);
  return text;
}

function httpsUrl(value, field, { noQuery = false } = {}) {
  const text = requiredText(value, field);
  let url;
  try { url = new URL(text); } catch { invalid(field); }
  if (url.protocol !== 'https:' || !url.hostname || url.username || url.password ||
      url.hash || noQuery && url.search || url.href !== text) invalid(field);
  return url;
}

function exactOrigin(value) {
  const field = 'IdentityBroker.WebOrigin';
  const text = requiredText(value, field);
  let url;
  try { url = new URL(text); } catch { invalid(field); }
  if (url.protocol !== 'https:' || !url.hostname || url.username || url.password ||
      url.pathname !== '/' || url.search || url.hash || url.origin !== text) invalid(field);
  return url.origin;
}

function targetPairs(repoRoot, resourceGroup) {
  if (!installTarget(resourceGroup)) invalid('--resource-group');
  try { return targetValuePairs(repoRoot, resourceGroup); } catch { conflict('deploy/k8s/targets'); }
}

function sourceFiles(repoRoot, pairs = []) {
  let configMap;
  let deployment;
  try {
    configMap = JSON.parse(applyTargetValues(readFileSync(join(repoRoot, 'deploy', 'k8s', 'runtime-configs',
      'identity-broker-runtime-config.json'), 'utf8'), pairs));
    deployment = parseYaml(applyTargetValues(readFileSync(join(repoRoot, 'deploy', 'k8s', 'base',
      'identity-broker', 'deployment.yaml'), 'utf8'), pairs));
  } catch {
    conflict('deploy/k8s');
  }
  if (configMap?.kind !== 'ConfigMap' || configMap.apiVersion !== 'v1' ||
      configMap.metadata?.name !== BASE_CONFIG_MAP || configMap.metadata?.namespace !== NAMESPACE ||
      configMap.immutable !== true || !isRecord(configMap.data) || !isRecord(deployment?.spec)) {
    conflict('deploy/k8s');
  }
  const containers = deployment.spec.template?.spec?.containers;
  const container = Array.isArray(containers)
    ? containers.find(item => item?.name === 'identity-broker')
    : undefined;
  if (!container || !Array.isArray(container.envFrom)) conflict('deploy/k8s/base/identity-broker/deployment.yaml');
  return { configMap, deployment, container };
}

function orderedHash(value) {
  function ordered(item) {
    if (Array.isArray(item)) return item.map(ordered);
    return isRecord(item)
      ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a.localeCompare(b))
        .map(([key, field]) => [key, ordered(field)]))
      : item;
  }
  return createHash('sha256').update(JSON.stringify(ordered(value))).digest('hex');
}

function configMapHash(data) {
  return orderedHash(data);
}

function hasBroadSecretEnvFrom(container) {
  return container.envFrom.some(source => isRecord(source?.secretRef));
}

function validateProviderInput(input, source) {
  assertKeys(input, ['IdentityBroker', 'SecretBindings'], ['IdentityBroker'], '$');
  const broker = input.IdentityBroker;
  assertKeys(broker, ['WebOrigin', 'CopilotConnection', 'GitHubRepoApp'], ['WebOrigin'], 'IdentityBroker');
  const hasCopilot = Object.hasOwn(broker, 'CopilotConnection');
  const hasRepoApp = Object.hasOwn(broker, 'GitHubRepoApp');
  if (!hasCopilot && !hasRepoApp) invalid('IdentityBroker');

  if (hasBroadSecretEnvFrom(source.container)) {
    if (!hasCopilot) conflict('IdentityBroker.CopilotConnection');
    if (!hasRepoApp) conflict('IdentityBroker.GitHubRepoApp');
  }

  const webOrigin = exactOrigin(broker.WebOrigin);
  const values = { [PROVIDER_FIELDS.WebOrigin]: webOrigin };
  if (hasCopilot) {
    const section = broker.CopilotConnection;
    assertKeys(section,
      ['ProjectsOwnerAddress', 'ClientId', 'CallbackUri', 'ClientSecretReference'],
      ['ProjectsOwnerAddress', 'ClientId', 'CallbackUri', 'ClientSecretReference'],
      'IdentityBroker.CopilotConnection');
    const ownerAddress = httpsUrl(section.ProjectsOwnerAddress,
      'IdentityBroker.CopilotConnection.ProjectsOwnerAddress', { noQuery: true });
    if (ownerAddress.pathname !== '/') invalid('IdentityBroker.CopilotConnection.ProjectsOwnerAddress');
    const clientId = requiredText(section.ClientId, 'IdentityBroker.CopilotConnection.ClientId', 256);
    const callback = requiredText(section.CallbackUri, 'IdentityBroker.CopilotConnection.CallbackUri');
    if (callback !== new URL('/auth/github/copilot-app/callback', webOrigin).href) {
      invalid('IdentityBroker.CopilotConnection.CallbackUri');
    }
    const reference = section.ClientSecretReference;
    assertKeys(reference, ['Id', 'Version'], ['Id', 'Version'],
      'IdentityBroker.CopilotConnection.ClientSecretReference');
    values[PROVIDER_FIELDS['CopilotConnection.ProjectsOwnerAddress']] = ownerAddress.href;
    values[PROVIDER_FIELDS['CopilotConnection.ClientId']] = clientId;
    values[PROVIDER_FIELDS['CopilotConnection.CallbackUri']] = callback;
    values[PROVIDER_FIELDS['CopilotConnection.ClientSecretReference.Id']] = keyVaultSecretName(
      reference.Id, 'IdentityBroker.CopilotConnection.ClientSecretReference.Id');
    values[PROVIDER_FIELDS['CopilotConnection.ClientSecretReference.Version']] = secretIdentifier(
      reference.Version, 'IdentityBroker.CopilotConnection.ClientSecretReference.Version');
  }

  let secretKeyRef;
  if (hasRepoApp) {
    const section = broker.GitHubRepoApp;
    assertKeys(section, [
      'OAuthClientId', 'CallbackUri', 'AppId', 'AppSlug', 'PrivateKeySecretId',
      'PrivateKeySecretVersion',
    ], [
      'OAuthClientId', 'CallbackUri', 'AppId', 'AppSlug', 'PrivateKeySecretId',
      'PrivateKeySecretVersion',
    ], 'IdentityBroker.GitHubRepoApp');
    const callback = requiredText(section.CallbackUri, 'IdentityBroker.GitHubRepoApp.CallbackUri');
    if (callback !== new URL('/auth/github/repo-app/callback', webOrigin).href) {
      invalid('IdentityBroker.GitHubRepoApp.CallbackUri');
    }
    if (!Number.isSafeInteger(section.AppId) || section.AppId < 1) {
      invalid('IdentityBroker.GitHubRepoApp.AppId');
    }
    const slug = requiredText(section.AppSlug, 'IdentityBroker.GitHubRepoApp.AppSlug', 100);
    if (!/^[A-Za-z0-9_-]+$/.test(slug)) invalid('IdentityBroker.GitHubRepoApp.AppSlug');
    values[PROVIDER_FIELDS['GitHubRepoApp.OAuthClientId']] = requiredText(
      section.OAuthClientId, 'IdentityBroker.GitHubRepoApp.OAuthClientId', 256);
    values[PROVIDER_FIELDS['GitHubRepoApp.CallbackUri']] = callback;
    values[PROVIDER_FIELDS['GitHubRepoApp.AppId']] = String(section.AppId);
    values[PROVIDER_FIELDS['GitHubRepoApp.AppSlug']] = slug;
    values[PROVIDER_FIELDS['GitHubRepoApp.PrivateKeySecretId']] = keyVaultSecretName(
      section.PrivateKeySecretId, 'IdentityBroker.GitHubRepoApp.PrivateKeySecretId');
    values[PROVIDER_FIELDS['GitHubRepoApp.PrivateKeySecretVersion']] = secretIdentifier(
      section.PrivateKeySecretVersion, 'IdentityBroker.GitHubRepoApp.PrivateKeySecretVersion');
  }

  if (hasRepoApp) {
    assertKeys(input.SecretBindings, [SECRET_ENV_NAME], [SECRET_ENV_NAME], 'SecretBindings');
    const binding = input.SecretBindings[SECRET_ENV_NAME];
    assertKeys(binding, ['SecretKeyRef'], ['SecretKeyRef'], `SecretBindings.${SECRET_ENV_NAME}`);
    const reference = binding.SecretKeyRef;
    assertKeys(reference, ['Name', 'Key'], ['Name', 'Key'],
      `SecretBindings.${SECRET_ENV_NAME}.SecretKeyRef`);
    const name = requiredText(reference.Name, `SecretBindings.${SECRET_ENV_NAME}.SecretKeyRef.Name`, 253);
    const key = requiredText(reference.Key, `SecretBindings.${SECRET_ENV_NAME}.SecretKeyRef.Key`, 253);
    if (!/^[a-z0-9](?:[-a-z0-9.]*[a-z0-9])?$/.test(name) ||
        name.split('.').some(part => part.length > 63 ||
          !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/.test(part))) {
      invalid(`SecretBindings.${SECRET_ENV_NAME}.SecretKeyRef.Name`);
    }
    if (!/^[A-Za-z0-9._-]+$/.test(key)) {
      invalid(`SecretBindings.${SECRET_ENV_NAME}.SecretKeyRef.Key`);
    }
    secretKeyRef = { name, key, optional: false };
  } else if (Object.hasOwn(input, 'SecretBindings')) {
    invalid('SecretBindings');
  }

  for (const key of Object.keys(source.configMap.data)) {
    if (key !== PROVIDER_FIELDS.WebOrigin &&
        (PROVIDER_ENV_PREFIXES.some(prefix => key.startsWith(prefix)) || key === SECRET_ENV_NAME)) {
      conflict(`deploy/k8s/runtime-configs/identity-broker-runtime-config.json#data.${key}`);
    }
  }

  return { values, secretKeyRef, webOrigin, hasCopilot, hasRepoApp };
}

function sourceBase(source) {
  const data = Object.fromEntries(Object.entries(source.configMap.data).sort(([a], [b]) => a.localeCompare(b)));
  return data;
}

function providerEnvironment(values, secretKeyRef) {
  const env = Object.entries(values).map(([name, value]) => ({ name, value }));
  if (secretKeyRef) {
    env.push({
      name: SECRET_ENV_NAME,
      valueFrom: { secretKeyRef: { name: secretKeyRef.name, key: secretKeyRef.key, optional: false } },
    });
  }
  return env.sort((a, b) => a.name.localeCompare(b.name));
}

function providerFingerprint(configMapName, configMapData, secretKeyRef) {
  return orderedHash({
    configMapName,
    configMapDataHash: configMapHash(configMapData),
    secretKeyRef: secretKeyRef ? { name: secretKeyRef.name, key: secretKeyRef.key } : null,
  });
}

function providerConfigMapName(data) {
  return `${BASE_CONFIG_MAP}-${configMapHash(data).slice(0, 16)}`;
}

function renderPlan(input, repoRoot = process.cwd(), source = sourceFiles(repoRoot)) {
  const settings = validateProviderInput(input, source);
  const data = Object.fromEntries(Object.entries({
    ...sourceBase(source),
    ...settings.values,
  }).sort(([a], [b]) => a.localeCompare(b)));
  const name = providerConfigMapName(data);
  const fingerprint = providerFingerprint(name, data, settings.secretKeyRef);
  const callback = path => new URL(path, settings.webOrigin).href;
  return {
    mode: 'render',
    configMap: {
      apiVersion: 'v1',
      kind: 'ConfigMap',
      metadata: {
        name,
        namespace: NAMESPACE,
        labels: {
          'agentweaver.io/service': 'identity-broker',
          'agentweaver.io/managed-by': 'installer',
          'agentweaver.io/provider-setup': 'true',
        },
      },
      immutable: true,
      data,
    },
    deployment: {
      name: 'identity-broker',
      namespace: NAMESPACE,
      configMapRef: { name },
      env: providerEnvironment(settings.values, settings.secretKeyRef),
      podTemplateAnnotations: { [PROVIDER_SETUP_ANNOTATION]: fingerprint },
    },
    registrationChecklist: [
      { provider: 'Copilot OAuth', field: 'Redirect URI', value: callback('/auth/github/copilot-app/callback') },
      { provider: 'GitHub Repo App OAuth', field: 'Redirect URI', value: callback('/auth/github/repo-app/callback') },
      { provider: 'GitHub Repo App', field: 'Setup URL', value: callback('/auth/github/repo-app/callback') },
    ],
  };
}

export function parseIdentityBrokerProviderInput(sourceText) {
  try {
    parseYaml(sourceText, { uniqueKeys: true });
    return JSON.parse(sourceText);
  } catch {
    invalid('$');
  }
}

export function readIdentityBrokerProviderInput(inputPath) {
  if (typeof inputPath !== 'string' || inputPath.length === 0) invalid('--identity-broker-provider-config');
  try {
    return parseIdentityBrokerProviderInput(readFileSync(inputPath, 'utf8'));
  } catch (error) {
    if (error instanceof IdentityBrokerProviderSetupError) throw error;
    invalid('--identity-broker-provider-config');
  }
}

export function buildIdentityBrokerProviderPlan(input, repoRoot = process.cwd(), resourceGroup = 'agentweaver') {
  return renderPlan(input, repoRoot, sourceFiles(repoRoot, targetPairs(repoRoot, resourceGroup)));
}

/** Carries the validated provider successor and release write fence through Kustomize deployment. */
export function preserveIdentityBrokerProviderSetup(
  resources, liveDeployment, readConfigMap, repoRoot = process.cwd(), resourceGroup = 'agentweaver',
) {
  const matches = resources.filter(value => value?.kind === 'Deployment' &&
    value.metadata?.namespace === NAMESPACE && value.metadata?.name === 'identity-broker');
  if (matches.length !== 1) conflict('deploy/k8s/deployment/identity-broker');
  const source = sourceFiles(repoRoot, targetPairs(repoRoot, resourceGroup));
  const candidate = matches[0];
  const current = deploymentParts(liveDeployment);
  const activeName = current.configRefs[0].configMapRef.name;
  const activeMap = readConfigMap(activeName);
  if (!activeMap) conflict(`ConfigMap/${activeName}`);
  const baseData = sourceBase(source);
  const state = readProviderSetupState(liveDeployment, activeMap, source, baseData);
  const deploymentFence = {
    deploymentUid: liveDeployment.metadata.uid,
    deploymentResourceVersion: liveDeployment.metadata.resourceVersion,
  };
  if (!state.managed) {
    if (state.entries.length) conflict('Deployment.env');
    return deploymentFence;
  }
  assertDeploymentSource(liveDeployment, candidate, activeName, state.entries);
  const candidateParts = deploymentParts(candidate, 'source Deployment');
  const ref = candidateParts.configRefs[0].configMapRef;
  if (ref.name !== BASE_CONFIG_MAP) conflict('deploy/k8s/deployment/identity-broker.envFrom');
  ref.name = activeName;
  candidateParts.container.env = [
    ...(Array.isArray(candidateParts.container.env) ? candidateParts.container.env : [])
      .filter(item => !ENV_NAMES.has(item.name)),
    ...state.entries,
  ];
  candidate.spec.template.metadata ??= {};
  candidate.spec.template.metadata.annotations ??= {};
  candidate.spec.template.metadata.annotations[PROVIDER_SETUP_ANNOTATION] =
    liveDeployment.spec.template.metadata.annotations[PROVIDER_SETUP_ANNOTATION];
  return { ...deploymentFence, configMap: activeName, configMapUid: activeMap.metadata.uid };
}

function assertTarget(config, source) {
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(config.subscriptionId ?? '')) {
    invalid('--subscription');
  }
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(config.tenantId ?? '')) {
    invalid('--tenant');
  }
  if (config.allowedSubscriptionId !== config.subscriptionId) invalid('--allowed-subscription');
  if (config.allowedTenantId !== config.tenantId) invalid('--allowed-tenant');
  for (const [key, flag] of Object.entries(REVISION_FLAG_FIELDS)) {
    const value = config[key === 'config' ? 'expectedConfigResourceVersion' : 'expectedDeploymentResourceVersion'];
    if (typeof value !== 'string' || value.length === 0 || /[\u0000-\u001f\u007f]/.test(value)) invalid(flag);
  }
  const vaultHost = source.configMap.data.IdentityBroker__SecretRedemption__VaultHost;
  if (typeof vaultHost !== 'string' || !vaultHost.endsWith('.vault.azure.net')) conflict('deploy/k8s/runtime-configs/identity-broker-runtime-config.json');
  return {
    ...config,
    keyVaultName: vaultHost.slice(0, -'.vault.azure.net'.length),
    allowedSubscriptionId: config.subscriptionId,
    allowedTenantId: config.tenantId,
  };
}

function parseResource(result, field, { optional = false } = {}) {
  if (!result || result.status !== 0) {
    if (optional && result?.status === 0) return undefined;
    conflict(field);
  }
  try {
    const value = JSON.parse(result.stdout);
    if (optional && Array.isArray(value?.items) && value.items.length === 0) return undefined;
    if (!value || !isRecord(value) || Array.isArray(value)) conflict(field);
    return value;
  } catch (error) {
    if (error instanceof IdentityBrokerProviderSetupError) throw error;
    conflict(field);
  }
}

function sourceDeployment(resources) {
  const matches = resources.filter(value => value?.kind === 'Deployment' &&
    value.metadata?.namespace === NAMESPACE && value.metadata?.name === 'identity-broker');
  if (matches.length !== 1) conflict('deploy/k8s/base/identity-broker/deployment.yaml');
  return matches[0];
}

function providerEntries(container) {
  const all = Array.isArray(container?.env) ? container.env : [];
  const seen = new Set();
  for (const item of all) {
    if (!item || typeof item.name !== 'string') conflict('Deployment.spec.template.spec.containers.env');
    if (seen.has(item.name)) conflict(`Deployment.spec.template.spec.containers.env.${item.name}`);
    seen.add(item.name);
    if (PROVIDER_ENV_PREFIXES.some(prefix => item.name.startsWith(prefix)) && !ENV_NAMES.has(item.name)) {
      conflict(`Deployment.spec.template.spec.containers.env.${item.name}`);
    }
  }
  return all.filter(item => ENV_NAMES.has(item.name));
}

function secretReference(entry, field = SECRET_ENV_NAME) {
  if (!entry || Object.hasOwn(entry, 'value') || Object.hasOwn(entry, 'valueFrom') === false ||
      !isRecord(entry.valueFrom) || Object.keys(entry.valueFrom).length !== 1 ||
      !isRecord(entry.valueFrom.secretKeyRef)) conflict(field);
  const ref = entry.valueFrom.secretKeyRef;
  if (Object.keys(ref).some(key => !['name', 'key', 'optional'].includes(key)) ||
      typeof ref.name !== 'string' || typeof ref.key !== 'string' || ref.optional !== false) conflict(field);
  return { name: ref.name, key: ref.key };
}

function exactProviderEnv(entries, data) {
  const byName = new Map(entries.map(entry => [entry.name, entry]));
  const providerNames = Object.keys(data).filter(name =>
    PROVIDER_ENV_PREFIXES.some(prefix => name.startsWith(prefix)));
  if (providerNames.some(name => !ENV_NAMES.has(name))) conflict('IdentityBroker');
  const appConfigured = Object.keys(data).some(name => name.startsWith('IdentityBroker__GitHubRepoApp__'));
  const expectedNames = [...providerNames, ...(appConfigured ? [SECRET_ENV_NAME] : [])].sort();
  const actualNames = entries.map(entry => entry.name).sort();
  if (JSON.stringify(actualNames) !== JSON.stringify(expectedNames)) conflict('Deployment.env');
  for (const name of providerNames) {
    const entry = byName.get(name);
    if (Object.keys(entry).some(key => !['name', 'value'].includes(key)) ||
        entry.value !== data[name]) conflict(`Deployment.env.${name}`);
  }
  return appConfigured ? secretReference(byName.get(SECRET_ENV_NAME)) : null;
}

function inputFromProviderMap(data, secretKeyRef) {
  const broker = { WebOrigin: data[PROVIDER_FIELDS.WebOrigin] };
  const copilotNames = COPILOT_FIELDS.map(key => PROVIDER_FIELDS[key]);
  const repoAppNames = REPO_APP_FIELDS.map(key => PROVIDER_FIELDS[key]);
  const hasCopilot = copilotNames.some(name => Object.hasOwn(data, name));
  const hasRepoApp = repoAppNames.some(name => Object.hasOwn(data, name));
  if (hasCopilot && !copilotNames.every(name => Object.hasOwn(data, name)) ||
      hasRepoApp && !repoAppNames.every(name => Object.hasOwn(data, name)) ||
      !hasCopilot && !hasRepoApp || hasRepoApp && !secretKeyRef) conflict('ConfigMap.data.IdentityBroker');
  if (hasCopilot) {
    broker.CopilotConnection = {
      ProjectsOwnerAddress: data[PROVIDER_FIELDS['CopilotConnection.ProjectsOwnerAddress']],
      ClientId: data[PROVIDER_FIELDS['CopilotConnection.ClientId']],
      CallbackUri: data[PROVIDER_FIELDS['CopilotConnection.CallbackUri']],
      ClientSecretReference: {
        Id: data[PROVIDER_FIELDS['CopilotConnection.ClientSecretReference.Id']],
        Version: data[PROVIDER_FIELDS['CopilotConnection.ClientSecretReference.Version']],
      },
    };
  }
  const input = { IdentityBroker: broker };
  if (hasRepoApp) {
    broker.GitHubRepoApp = {
      OAuthClientId: data[PROVIDER_FIELDS['GitHubRepoApp.OAuthClientId']],
      CallbackUri: data[PROVIDER_FIELDS['GitHubRepoApp.CallbackUri']],
      AppId: Number(data[PROVIDER_FIELDS['GitHubRepoApp.AppId']]),
      AppSlug: data[PROVIDER_FIELDS['GitHubRepoApp.AppSlug']],
      PrivateKeySecretId: data[PROVIDER_FIELDS['GitHubRepoApp.PrivateKeySecretId']],
      PrivateKeySecretVersion: data[PROVIDER_FIELDS['GitHubRepoApp.PrivateKeySecretVersion']],
    };
    input.SecretBindings = {
      [SECRET_ENV_NAME]: { SecretKeyRef: { Name: secretKeyRef.name, Key: secretKeyRef.key } },
    };
  }
  return input;
}

function assertProviderMap(map, name, baseData) {
  if (!map || map.kind !== 'ConfigMap' || map.apiVersion !== 'v1' ||
      map.metadata?.namespace !== NAMESPACE || map.metadata?.name !== name ||
      map.immutable !== true || !map.metadata?.uid ||
      map.metadata.labels?.['agentweaver.io/service'] !== 'identity-broker' ||
      map.metadata.labels?.['agentweaver.io/managed-by'] !== 'installer' ||
      map.metadata.labels?.['agentweaver.io/provider-setup'] !== 'true' ||
      map.binaryData && Object.keys(map.binaryData).length ||
      !isRecord(map.data)) conflict('ConfigMap');
  for (const [key, value] of Object.entries(baseData)) {
    if (key !== PROVIDER_FIELDS.WebOrigin && map.data[key] !== value) conflict(`ConfigMap.data.${key}`);
  }
  for (const [key, value] of Object.entries(map.data)) {
    if (typeof value !== 'string') conflict(`ConfigMap.data.${key}`);
    if (!Object.hasOwn(baseData, key) &&
        !PROVIDER_ENV_PREFIXES.some(prefix => key.startsWith(prefix))) conflict(`ConfigMap.data.${key}`);
  }
  const expectedName = providerConfigMapName(map.data);
  if (name !== expectedName) conflict('ConfigMap.metadata.name');
}

function assertBaseConfigMap(map, source) {
  if (!map || map.kind !== 'ConfigMap' || map.apiVersion !== 'v1' ||
      map.metadata?.name !== BASE_CONFIG_MAP || map.metadata?.namespace !== NAMESPACE ||
      map.immutable !== true || !map.metadata?.uid || !isRecord(map.data) ||
      Object.keys(map.data).length !== Object.keys(source.configMap.data).length ||
      configMapHash(map.data) !== configMapHash(source.configMap.data) ||
      map.binaryData && Object.keys(map.binaryData).length) conflict(`ConfigMap/${BASE_CONFIG_MAP}`);
}

function deploymentParts(deployment, field = 'Deployment') {
  if (!deployment || deployment.kind !== 'Deployment' || deployment.apiVersion !== 'apps/v1' ||
      deployment.metadata?.name !== 'identity-broker' || deployment.metadata?.namespace !== NAMESPACE ||
      field !== 'source Deployment' &&
        (!deployment.metadata.uid || !deployment.metadata.resourceVersion)) conflict(field);
  const containers = deployment.spec?.template?.spec?.containers;
  const matches = Array.isArray(containers)
    ? containers.map((container, index) => ({ container, index }))
      .filter(({ container }) => container?.name === 'identity-broker')
    : [];
  if (matches.length !== 1) conflict(`${field}.spec.template.spec.containers`);
  const container = matches[0].container;
  if (!Array.isArray(container.envFrom)) conflict(`${field}.envFrom`);
  const configRefs = container.envFrom.filter(item => typeof item?.configMapRef?.name === 'string');
  if (configRefs.length !== 1) conflict(`${field}.envFrom`);
  return { container, containerIndex: matches[0].index, configRefs };
}

function normalizeForSourceComparison(live, source, currentMapName, providerEnv) {
  const actual = structuredClone(live);
  const baseline = structuredClone(source);
  const actualParts = deploymentParts(actual);
  const baselineParts = deploymentParts(baseline, 'source Deployment');
  const imagePattern = /^ghcr\.io\/sabbour\/agentweaver\.identity\.broker(?:@sha256:[a-f0-9]{64}|:sha-[a-f0-9]{40})$/;
  if (!imagePattern.test(actualParts.container.image ?? '') ||
      !imagePattern.test(baselineParts.container.image ?? '')) conflict('Deployment.image');
  actualParts.container.image = baselineParts.container.image;
  const actualEnvFrom = actualParts.container.envFrom;
  const baselineEnvFrom = baselineParts.container.envFrom;
  if (actualEnvFrom.length !== baselineEnvFrom.length) conflict('Deployment.envFrom');
  for (let index = 0; index < baselineEnvFrom.length; index += 1) {
    const actualSource = structuredClone(actualEnvFrom[index]);
    const expectedSource = structuredClone(baselineEnvFrom[index]);
    if (actualSource.configMapRef && expectedSource.configMapRef) {
      if (actualSource.configMapRef.name !== currentMapName) conflict('Deployment.envFrom.configMapRef');
      actualSource.configMapRef.name = expectedSource.configMapRef.name;
    }
    if (orderedHash(actualSource) !== orderedHash(expectedSource)) conflict('Deployment.envFrom');
    actualEnvFrom[index] = actualSource;
  }
  actualParts.container.env = (actualParts.container.env ?? [])
    .filter(item => !ENV_NAMES.has(item.name));
  baselineParts.container.env = (baselineParts.container.env ?? [])
    .filter(item => !ENV_NAMES.has(item.name));
  const actualAnnotations = actual.spec.template.metadata?.annotations;
  const baselineAnnotations = baseline.spec.template.metadata?.annotations;
  if (actualAnnotations) {
    delete actualAnnotations[PROVIDER_SETUP_ANNOTATION];
    if (!Object.keys(actualAnnotations).length) delete actual.spec.template.metadata.annotations;
  }
  if (baselineAnnotations) {
    delete baselineAnnotations[PROVIDER_SETUP_ANNOTATION];
    if (!Object.keys(baselineAnnotations).length) delete baseline.spec.template.metadata.annotations;
  }
  if (providerEnv.length && (providerEnv.some(item => !ENV_NAMES.has(item.name)) ||
      new Set(providerEnv.map(item => item.name)).size !== providerEnv.length)) conflict('Deployment.env');
  return { actual, baseline, actualParts, baselineParts };
}

function readProviderSetupState(deployment, activeMap, source, baseData) {
  const parts = deploymentParts(deployment);
  const entries = providerEntries(parts.container);
  const marker = deployment.spec?.template?.metadata?.annotations?.[PROVIDER_SETUP_ANNOTATION];
  const activeName = parts.configRefs[0].configMapRef.name;
  if (activeName === BASE_CONFIG_MAP) {
    if (marker) conflict(`Deployment.metadata.annotations.${PROVIDER_SETUP_ANNOTATION}`);
    if (activeMap?.metadata?.name !== BASE_CONFIG_MAP) conflict('Deployment.envFrom.configMapRef');
    const secret = entries.find(entry => entry.name === SECRET_ENV_NAME);
    const secretKeyRef = secret ? secretReference(secret) : null;
    for (const entry of entries) {
      if (entry.name === SECRET_ENV_NAME) continue;
      if (Object.keys(entry).some(key => !['name', 'value'].includes(key)) ||
          entry.value !== undefined && typeof entry.value !== 'string') conflict(`Deployment.env.${entry.name}`);
    }
    return { activeName, entries, secretKeyRef, managed: false };
  }

  if (!new RegExp(`^${BASE_CONFIG_MAP}-[a-f0-9]{16}$`).test(activeName) || !marker || !activeMap) {
    conflict('Deployment.envFrom.configMapRef');
  }
  assertProviderMap(activeMap, activeName, baseData);
  const secretKeyRef = exactProviderEnv(entries, activeMap.data);
  let settings;
  try { settings = validateProviderInput(inputFromProviderMap(activeMap.data, secretKeyRef), source); } catch {
    conflict('ConfigMap.data.IdentityBroker');
  }
  const expectedData = { ...baseData, ...settings.values };
  if (configMapHash(expectedData) !== configMapHash(activeMap.data)) conflict('ConfigMap.data');
  if (providerFingerprint(activeName, activeMap.data, secretKeyRef) !== marker) {
    conflict(`Deployment.metadata.annotations.${PROVIDER_SETUP_ANNOTATION}`);
  }
  return { activeName, entries, secretKeyRef, managed: true };
}

function sourceDataAndDeployment(repoRoot, execKubectl, source, pairs) {
  const rendered = execKubectl(['kustomize', join(repoRoot, 'deploy', 'k8s')], {
    ...RESOURCE_OPTIONS,
  });
  if (rendered.status !== 0) conflict('deploy/k8s');
  let resources;
  try {
    resources = parseKustomizeResources(applyTargetValues(rendered.stdout, pairs)).items;
  } catch {
    conflict('deploy/k8s');
  }
  const baseMap = resources.find(item => item?.kind === 'ConfigMap' &&
    item.metadata?.name === BASE_CONFIG_MAP && item.metadata?.namespace === NAMESPACE);
  const deployment = sourceDeployment(resources);
  if (!baseMap || baseMap.immutable !== true ||
      configMapHash(baseMap.data) !== configMapHash(source.configMap.data)) conflict('deploy/k8s');
  return { baseMap, deployment };
}

function assertDeploymentSource(live, source, activeName, entries) {
  const normalized = normalizeForSourceComparison(live, source, activeName, entries);
  const { actual, baseline } = normalized;
  const actualHash = brokerObjectHash(actual);
  const baselineHash = brokerObjectHash(baseline);
  if (actualHash !== baselineHash) conflict('Deployment.spec');
  return normalized;
}

function sameEntries(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function resultFromReadback(deployment, configMap, expectedMapName, expectedEnv, expectedFingerprint, baseData) {
  const parts = deploymentParts(deployment, 'Deployment readback');
  if (parts.configRefs[0].configMapRef.name !== expectedMapName ||
      !sameEntries(providerEntries(parts.container), expectedEnv) ||
      deployment.spec?.template?.metadata?.annotations?.[PROVIDER_SETUP_ANNOTATION] !== expectedFingerprint) {
    conflict('Deployment readback');
  }
  const replicas = deployment.spec?.replicas ?? 1;
  const generation = deployment.metadata.generation;
  if (!Number.isSafeInteger(generation) || !Number.isSafeInteger(replicas) ||
      deployment.status?.observedGeneration < generation ||
      deployment.status?.updatedReplicas !== replicas ||
      deployment.status?.readyReplicas !== replicas ||
      deployment.status?.availableReplicas !== replicas) {
    conflict('Deployment rollout readback');
  }
  assertProviderMap(configMap, expectedMapName, baseData);
  return { generation, replicas };
}

function activationError(phase, names, cause) {
  const details = { phase, resources: names };
  if (cause !== undefined) details.cause = cause;
  return new IdentityBrokerProviderSetupError('identity_broker_provider_activation_partial', {
    ...details,
  });
}

function safePreflightCause(error) {
  const message = redact(error instanceof Error ? error.message : String(error ?? ''));
  const cause = typeof message === 'string'
    ? message.replace(/[\u0000-\u001f\u007f]/g, ' ').trim().slice(0, 256)
    : '';
  return cause || 'Unknown preflight error';
}

function currentRolloutReadback(deployment, activeMapName, activeMap) {
  const replicas = deployment.spec?.replicas ?? 1;
  const generation = deployment.metadata?.generation;
  const observedGeneration = deployment.status?.observedGeneration;
  const readyReplicas = deployment.status?.readyReplicas;
  if (!Number.isSafeInteger(generation) || !Number.isSafeInteger(replicas) ||
      !Number.isSafeInteger(observedGeneration) || observedGeneration < generation || (replicas > 0 &&
      (deployment.status?.updatedReplicas !== replicas || readyReplicas !== replicas ||
        deployment.status?.availableReplicas !== replicas))) {
    throw activationError('existing_rollout_not_ready', {
      activeConfigMap: activeMapName,
      configMapUid: activeMap?.metadata?.uid,
      deploymentResourceVersion: deployment.metadata?.resourceVersion,
      deploymentGeneration: generation,
      observedGeneration,
      readyReplicas,
    });
  }
  return { generation, replicas };
}

function readWithKubectl(execKubectl, base, resource, name, { optional = false } = {}) {
  const result = execKubectl([...base, 'get', resource, name, '-o', 'json', '--namespace', NAMESPACE,
    ...(optional ? ['--ignore-not-found'] : [])], {
    ...RESOURCE_OPTIONS,
    projectJson: value => value,
    preserveProjectedJson: true,
    allowEmptyJson: optional,
  });
  if (optional && result.status === 0 && !String(result.stdout ?? '').trim()) return undefined;
  return parseResource(result, `${resource}/${name}`, { optional });
}

function addOrReplace(operations, path, value, exists) {
  operations.push({ op: exists ? 'replace' : 'add', path, value });
}

function activationPlan(input, config, dependencies) {
  const repoRoot = config.repoRoot ?? process.cwd();
  const pairs = targetPairs(repoRoot, config.resourceGroup);
  const source = sourceFiles(repoRoot, pairs);
  const rendered = renderPlan(input, repoRoot, source);
  const target = assertTarget(config, source);
  const sourceObjects = sourceDataAndDeployment(repoRoot, dependencies.execKubectl, source, pairs);
  if (config.expectedConfigResourceVersion === undefined ||
      config.expectedDeploymentResourceVersion === undefined) invalid(REVISION_FLAG_FIELDS.config);
  return { repoRoot, source, rendered, target, sourceObjects };
}

function activateInCluster(plan, base, { execKubectl }, config) {
  const names = { configMap: plan.rendered.configMap.metadata.name, deployment: 'identity-broker' };
  let phase = 'preflight';
  let mapCreated = false;
  let deploymentUpdated = false;
  let writeAttempted = false;
  function read(resource, name, optional = false) {
    return readWithKubectl(execKubectl, base, resource, name, { optional });
  }
  function readState() {
    return {
      baseMap: read('configmaps', BASE_CONFIG_MAP),
      deployment: read('deployments', 'identity-broker'),
    };
  }
  const resourceVersionConflict = (resource, expectedFlag) => {
    throw new IdentityBrokerProviderSetupError('identity_broker_provider_revision_conflict', {
      field: expectedFlag,
      resource,
    });
  };

  let current;
  let activeMap;
  let desiredMap;
  let currentState;
  try {
    current = readState();
    if (current.baseMap.metadata?.resourceVersion !== config.expectedConfigResourceVersion) {
      resourceVersionConflict(`ConfigMap/${BASE_CONFIG_MAP}`, REVISION_FLAG_FIELDS.config);
    }
    if (current.deployment.metadata?.resourceVersion !== config.expectedDeploymentResourceVersion) {
      resourceVersionConflict('Deployment/identity-broker', REVISION_FLAG_FIELDS.deployment);
    }
    assertBaseConfigMap(current.baseMap, plan.source);
    const parts = deploymentParts(current.deployment);
    const activeName = parts.configRefs[0].configMapRef.name;
    activeMap = activeName === BASE_CONFIG_MAP ? current.baseMap : read('configmaps', activeName, true);
    currentState = readProviderSetupState(
      current.deployment, activeMap, plan.source, sourceBase(plan.source));
    assertDeploymentSource(current.deployment, plan.sourceObjects.deployment,
      activeName, currentState.entries);
    desiredMap = plan.rendered.configMap;

    if (currentState.entries.length) {
      const currentByName = new Map(currentState.entries.map(entry => [entry.name, entry]));
      for (const entry of plan.rendered.deployment.env) {
        const existing = currentByName.get(entry.name);
        if (!existing) continue;
        if (entry.name === SECRET_ENV_NAME) {
          if (!sameEntries([secretReference(existing)], [secretReference(entry)])) {
            conflict(`Deployment.env.${SECRET_ENV_NAME}`);
          }
        } else if (!currentState.managed &&
            (Object.keys(existing).some(key => !['name', 'value'].includes(key)) ||
              existing.value !== entry.value)) {
          conflict(`Deployment.env.${entry.name}`);
        }
      }
    }

    const existingDesiredMap = plan.rendered.configMap.metadata.name === activeName
      ? activeMap : read('configmaps', plan.rendered.configMap.metadata.name, true);
    if (existingDesiredMap) assertProviderMap(existingDesiredMap,
      plan.rendered.configMap.metadata.name, sourceBase(plan.source));

    if (currentState.managed && activeName === desiredMap.metadata.name &&
        sameEntries(currentState.entries, plan.rendered.deployment.env) &&
        current.deployment.spec?.template?.metadata?.annotations?.[PROVIDER_SETUP_ANNOTATION] ===
          plan.rendered.deployment.podTemplateAnnotations[PROVIDER_SETUP_ANNOTATION]) {
      const readback = currentRolloutReadback(current.deployment, activeName, activeMap);
      return {
        changed: false,
        namespace: NAMESPACE,
        configMap: activeName,
        configMapUid: activeMap.metadata.uid,
        deployment: 'identity-broker',
        generation: readback.generation,
        replicas: readback.replicas,
      };
    }
    if (!currentState.managed && activeName === BASE_CONFIG_MAP &&
        sameEntries(currentState.entries, plan.rendered.deployment.env)) {
      const readback = currentRolloutReadback(current.deployment, activeName, activeMap);
      return {
        changed: false,
        namespace: NAMESPACE,
        configMap: activeName,
        configMapUid: activeMap.metadata.uid,
        deployment: 'identity-broker',
        generation: readback.generation,
        replicas: readback.replicas,
      };
    }

    const newMapExists = Boolean(existingDesiredMap);
    const writePermissions = [
      ...(!newMapExists ? [['create', 'configmaps', NAMESPACE]] : []),
      ['patch', 'deployments', NAMESPACE],
      ['watch', 'deployments', NAMESPACE],
    ];
    assertP0KubernetesPermissions(base, execKubectl, writePermissions, 'Identity Broker provider setup');

    if (!newMapExists) {
      phase = 'creating_configmap';
      writeAttempted = true;
      const created = execKubectl([...base, 'create', '-f', '-', '-o', 'name',
        '--namespace', NAMESPACE], {
        ...RESOURCE_OPTIONS,
        input: JSON.stringify(desiredMap),
      });
      if (created.status !== 0) {
        const afterCreate = read('configmaps', desiredMap.metadata.name, true);
        if (!afterCreate) throw activationError('configmap_create_failed', names);
        assertProviderMap(afterCreate, desiredMap.metadata.name, sourceBase(plan.source));
      } else {
        mapCreated = true;
      }
      const mapReadback = read('configmaps', desiredMap.metadata.name);
      assertProviderMap(mapReadback, desiredMap.metadata.name, sourceBase(plan.source));
      if (configMapHash(mapReadback.data) !== configMapHash(desiredMap.data)) {
        conflict('ConfigMap.data');
      }
      names.configMapUid = mapReadback.metadata.uid;
      phase = 'configmap_created';
    } else {
      names.configMapUid = existingDesiredMap.metadata.uid;
    }

    const currentParts = deploymentParts(current.deployment);
    const mapRefIndex = currentParts.container.envFrom.findIndex(item =>
      item?.configMapRef?.name === currentState.activeName);
    if (mapRefIndex < 0) conflict('Deployment.envFrom.configMapRef');
    const nextEnv = [
      ...(Array.isArray(currentParts.container.env) ? currentParts.container.env : [])
        .filter(item => !ENV_NAMES.has(item.name)),
      ...plan.rendered.deployment.env,
    ];
    const oldAnnotations = current.deployment.spec?.template?.metadata?.annotations;
    const nextAnnotations = {
      ...(isRecord(oldAnnotations) ? oldAnnotations : {}),
      ...plan.rendered.deployment.podTemplateAnnotations,
    };
    const patch = [
      { op: 'test', path: '/metadata/uid', value: current.deployment.metadata.uid },
      { op: 'test', path: '/metadata/resourceVersion', value: current.deployment.metadata.resourceVersion },
      { op: 'test', path: `/spec/template/spec/containers/${currentParts.containerIndex}/envFrom/${mapRefIndex}/configMapRef/name`,
        value: currentState.activeName },
      { op: 'replace', path: `/spec/template/spec/containers/${currentParts.containerIndex}/envFrom/${mapRefIndex}/configMapRef/name`,
        value: desiredMap.metadata.name },
      { op: 'replace', path: `/spec/template/spec/containers/${currentParts.containerIndex}/env`,
        value: nextEnv },
    ];
    const annotationPath = '/spec/template/metadata/annotations';
    addOrReplace(patch, annotationPath, nextAnnotations, isRecord(oldAnnotations));
    phase = 'activating_deployment';
    writeAttempted = true;
    const patched = execKubectl([...base, 'patch', 'deployment', 'identity-broker', '--type=json',
      '--patch', JSON.stringify(patch), '-o', 'json', '--namespace', NAMESPACE], {
      ...RESOURCE_OPTIONS,
      projectJson: value => value,
      preserveProjectedJson: true,
    });
    if (patched.status !== 0) throw activationError('deployment_patch_failed', names);
    deploymentUpdated = true;
    let updated = parseResource(patched, 'Deployment/identity-broker');
    if (updated.metadata?.generation <= current.deployment.metadata.generation) {
      throw activationError('deployment_generation_not_advanced', names);
    }
    phase = 'waiting_for_rollout';
    const rollout = execKubectl([...base, 'rollout', 'status', 'deployment/identity-broker',
      '--timeout=180s', '--namespace', NAMESPACE], { ...RESOURCE_OPTIONS, timeout: 190_000 });
    if (rollout.status !== 0) throw activationError('rollout_failed', names);
    phase = 'reading_back';
    updated = read('deployments', 'identity-broker');
    const mapReadback = read('configmaps', desiredMap.metadata.name);
    assertProviderMap(mapReadback, desiredMap.metadata.name, sourceBase(plan.source));
    const ready = resultFromReadback(updated, mapReadback, desiredMap.metadata.name,
      plan.rendered.deployment.env,
      plan.rendered.deployment.podTemplateAnnotations[PROVIDER_SETUP_ANNOTATION],
      sourceBase(plan.source));
    return {
      changed: true,
      configMapCreated: mapCreated,
      deploymentUpdated,
      namespace: NAMESPACE,
      configMap: desiredMap.metadata.name,
      configMapUid: mapReadback.metadata.uid,
      previousConfigMap: currentState.activeName,
      deployment: 'identity-broker',
      generation: ready.generation,
      replicas: ready.replicas,
    };
  } catch (error) {
    if (!writeAttempted) {
      if (error instanceof IdentityBrokerProviderSetupError) throw error;
      throw activationError('preflight', names, safePreflightCause(error));
    }
    let readback = {};
    try {
      const actual = readState();
      const activeName = deploymentParts(actual.deployment).configRefs[0].configMapRef.name;
      const map = read('configmaps', activeName, true);
      readback = {
        activeConfigMap: activeName,
        configMapUid: map?.metadata?.uid,
        configMapDataHash: map?.data ? configMapHash(map.data) : undefined,
        deploymentResourceVersion: actual.deployment.metadata?.resourceVersion,
        deploymentGeneration: actual.deployment.metadata?.generation,
        observedGeneration: actual.deployment.status?.observedGeneration,
        readyReplicas: actual.deployment.status?.readyReplicas,
      };
    } catch {
      readback = { readback: 'unavailable' };
    }
    throw activationError(phase, { ...names, ...readback });
  }
}

export async function activateIdentityBrokerProviderSetup(input, config, dependencies = {}) {
  const execAz = dependencies.execAz ?? runAz;
  const execKubectl = dependencies.execKubectl ?? ((args, options) => run('kubectl', args, options));
  const withKubeconfig = dependencies.withKubeconfig ?? withP0UserKubeconfig;
  const guardTarget = dependencies.guardTarget ?? guardAzureTarget;
  try {
    const plan = activationPlan(input, config, { execKubectl });
    guardTarget(plan.target, execAz);
    return withKubeconfig({
      resourceGroup: plan.target.resourceGroup,
      subscriptionId: plan.target.subscriptionId,
      clusterName: `${plan.target.resourceGroup}-aks`,
    }, base => activateInCluster(
      plan,
      base,
      { execKubectl },
      config,
    ), {
      execAz,
      execKubectl,
      requiredPermissions: [['get', 'configmaps', NAMESPACE], ['get', 'deployments', NAMESPACE]],
      permissionContext: 'Identity Broker provider setup',
    });
  } catch (error) {
    if (error instanceof IdentityBrokerProviderSetupError) throw error;
    throw activationError('preflight',
      { configMap: BASE_CONFIG_MAP, deployment: 'identity-broker' }, safePreflightCause(error));
  }
}

export function writeIdentityBrokerProviderPlan(plan, outputPath) {
  if (typeof outputPath !== 'string' || outputPath.length === 0) invalid('--identity-broker-provider-output');
  try {
    writeFileSync(outputPath, `${JSON.stringify(plan, null, 2)}\n`, { flag: 'wx', mode: 0o600 });
  } catch {
    invalid('--identity-broker-provider-output');
  }
}

export function providerSetupErrorLine(error) {
  if (error instanceof IdentityBrokerProviderSetupError) return JSON.stringify(error.toJSON());
  return JSON.stringify({
    error: 'identity_broker_provider_activation_partial',
    phase: 'preflight',
    resources: { deployment: 'identity-broker' },
  });
}
