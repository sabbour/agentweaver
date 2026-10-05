import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';
import { withP0UserKubeconfig } from './namespace-bootstrap.mjs';
import { observeExistingP0BrokerRouting, projectIdentityRoutingReadback } from './identity-broker-routing.mjs';

const NAMESPACE = 'agentweaver-v1-p0';
const CONFIG = 'identity-broker-runtime-config';
const GUID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const OPTIONS = { check: false, timeout: 35_000 };
const SENTINEL = 'registry.invalid/agentweaver-identity-broker@sha256:' + '0'.repeat(64);
const SCOPES = ['openid', 'profile', 'email', 'api.read', 'offline_access'];

export function assertBrokerRuntimeInputs(config) {
  if (config.resourceGroup !== 'aw-v1-p0' || config.clusterName !== 'aw-v1-p0-aks' ||
      !GUID.test(config.subscriptionId ?? '') || !GUID.test(config.tenantId ?? '') ||
      !GUID.test(config.upstreamClientId ?? '') || !GUID.test(config.acceptanceRunId ?? '') ||
      !/^ghcr\.io\/sabbour\/agentweaver\.identity\.broker@sha256:[0-9a-f]{64}$/.test(config.brokerImage ?? '')) {
    throw new Error('Broker runtime setup requires the exact P0 target, upstream public client, acceptance run UUID, and immutable Broker image.');
  }
  let redirect;
  try { redirect = new URL(config.acceptanceRedirectUri); } catch {
    throw new Error('Broker acceptance redirect must be an explicit HTTP loopback callback.');
  }
  if (redirect.protocol !== 'http:' || !['127.0.0.1', '[::1]'].includes(redirect.hostname) ||
      !redirect.port || Number(redirect.port) < 1 || redirect.pathname !== '/callback' || redirect.search || redirect.hash ||
      redirect.username || redirect.password) {
    throw new Error('Broker acceptance redirect must be an explicit HTTP loopback callback with a port and no credentials, query, or fragment.');
  }
}

export function brokerAcceptanceConfiguration(config, { issuer, identity }) {
  assertBrokerRuntimeInputs(config);
  if (!/^https:\/\/agentweaver\.[a-z0-9.-]+\.aksapp\.io\/$/.test(issuer) ||
      !GUID.test(identity?.clientId ?? '') || !GUID.test(identity?.principalId ?? '')) {
    throw new Error('Broker runtime configuration requires the aligned native managed issuer and exact runtime identity.');
  }
  const data = {
    ConnectionStrings__IdentityBroker: `Host=${config.resourceGroup}-pg.postgres.database.azure.com;Database=agentweaver;Username=${config.resourceGroup}-id-identity-broker;SSL Mode=VerifyFull`,
    IdentityBroker__Issuer: issuer,
    IdentityBroker__DataProtectionKeyPath: '/var/lib/identity-broker/key-ring',
    IdentityBroker__ExternalProvider__Authority: `https://login.microsoftonline.com/${config.tenantId}/v2.0`,
    IdentityBroker__ExternalProvider__ClientId: config.upstreamClientId,
    IdentityBroker__SecretRedemption__Audience: issuer,
    IdentityBroker__SecretRedemption__VaultUri: `https://${config.resourceGroup}-kv.vault.azure.net/`,
    IdentityBroker__SecretRedemption__WorkloadIdentityTenantId: config.tenantId,
    IdentityBroker__SecretRedemption__WorkloadIdentityClientId: identity.clientId,
    IdentityBroker__SecretRedemption__WorkloadIdentityTokenFilePath: '/var/run/secrets/azure/tokens/azure-identity-token',
    IdentityBroker__Clients__0__ClientId: `identity-acceptance-${config.acceptanceRunId}`,
    IdentityBroker__Clients__0__DisplayName: `P0 acceptance ${config.acceptanceRunId}`,
    IdentityBroker__Clients__0__Type: 'Public',
    IdentityBroker__Clients__0__RedirectUris__0: config.acceptanceRedirectUri,
    IdentityBroker__Clients__0__Resources__0: issuer,
  };
  SCOPES.forEach((scope, index) => { data[`IdentityBroker__Clients__0__Scopes__${index}`] = scope; });
  return data;
}

export function projectBrokerRuntimeResource(value) {
  if (Array.isArray(value?.items)) {
    if (value.items.length > 1) throw new Error('Exact Broker runtime lookup returned multiple objects.');
    return value.items.length ? projectBrokerRuntimeResource(value.items[0]) : null;
  }
  const result = { kind: value?.kind, metadata: {
    name: value?.metadata?.name, namespace: value?.metadata?.namespace, uid: value?.metadata?.uid,
    service: value?.metadata?.labels?.['agentweaver.io/service'],
    owner: value?.metadata?.labels?.['agentweaver.io/managed-by'],
  } };
  if (value?.kind === 'ConfigMap') {
    // Compare complete configuration without returning any operator-provided value.
    result.dataHash = orderedHash(value.data ?? {});
    result.immutable = value.immutable;
  } else if (['ServiceAccount', 'Deployment', 'Service', 'CiliumNetworkPolicy'].includes(value?.kind)) {
    result.specHash = brokerObjectHash(value);
  } else {
    throw new Error('Unexpected Broker runtime resource kind.');
  }
  return result;
}

export function brokerObjectHash(value) {
    const spec = structuredClone(value.kind === 'ServiceAccount'
      ? { automountServiceAccountToken: value.automountServiceAccountToken,
        annotations: Object.fromEntries(Object.entries(value.metadata?.annotations ?? {})
          .filter(([key]) => key.startsWith('azure.workload.identity/'))) }
      : value.spec);
    if (value.kind === 'Service') {
      // Allocated addresses and API defaults are not part of the source manifest.
      for (const key of ['clusterIP', 'clusterIPs', 'ipFamilies', 'ipFamilyPolicy',
        'internalTrafficPolicy', 'sessionAffinity']) delete spec[key];
    }
    if (value.kind === 'Deployment') {
      for (const key of ['revisionHistoryLimit', 'progressDeadlineSeconds']) delete spec[key];
      delete spec.strategy.rollingUpdate;
      const pod = spec.template.spec;
      for (const key of ['restartPolicy', 'dnsPolicy', 'schedulerName', 'enableServiceLinks']) delete pod[key];
      for (const container of pod.containers ?? []) {
        for (const key of ['terminationMessagePath', 'terminationMessagePolicy']) delete container[key];
        for (const probe of [container.livenessProbe, container.readinessProbe]) {
          if (probe) { delete probe.successThreshold; if (probe.httpGet) delete probe.httpGet.host; }
        }
      }
    }
    return orderedHash(spec);
}

function orderedHash(value) {
    function ordered(item) {
      if (Array.isArray(item)) return item.map(ordered);
      return item && typeof item === 'object'
        ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a.localeCompare(b))
          .map(([key, field]) => [key, ordered(field)]))
        : item;
    }
    return createHash('sha256').update(JSON.stringify(ordered(value))).digest('hex');
}

export function bootstrapIdentityBrokerRuntime(config, {
  withKubeconfig = withP0UserKubeconfig, execAz = runAz,
  execKubectl = (args, options) => run('kubectl', args, options),
} = {}) {
  assertBrokerRuntimeInputs(config);
  function azureJson(args, projectJson) {
    const result = execAz(args, { ...OPTIONS, projectJson, preserveProjectedJson: true });
    if (result.status !== 0) throw new Error(`Broker runtime target lookup failed: ${redact(result.stderr)}`);
    return JSON.parse(result.stdout);
  }
  const account = azureJson(['account', 'show', '-o', 'json'],
    value => ({ id: value.id, tenantId: value.tenantId, state: value.state }));
  if (account.id !== config.subscriptionId || account.tenantId !== config.tenantId || account.state !== 'Enabled') {
    throw new Error('Broker runtime account differs from the exact approved tenant and subscription.');
  }
  const identityName = `${config.resourceGroup}-id-identity-broker`;
  const identity = azureJson(['identity', 'show', '--resource-group', config.resourceGroup, '--name', identityName,
    '--subscription', config.subscriptionId, '-o', 'json'],
  value => ({ id: value.id, clientId: value.clientId, principalId: value.principalId }));
  const expectedIdentityId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/${identityName}`;
  if (identity.id?.toLowerCase() !== expectedIdentityId.toLowerCase() ||
      !GUID.test(identity.clientId ?? '') || !GUID.test(identity.principalId ?? '')) {
    throw new Error('Broker runtime identity does not match the approved native target.');
  }
  const issuer = azureJson(['aks', 'show', '--resource-group', config.resourceGroup, '--name', config.clusterName,
    '--subscription', config.subscriptionId, '-o', 'json'], value => ({ issuer: value.oidcIssuerProfile?.issuerUrl }));
  const fic = azureJson(['identity', 'federated-credential', 'show', '--resource-group', config.resourceGroup,
    '--identity-name', identityName, '--name', 'identity-broker-workload-identity',
    '--subscription', config.subscriptionId, '-o', 'json'],
  value => ({ issuer: value.issuer, subject: value.subject, audiences: value.audiences }));
  if (!issuer.issuer?.startsWith(`https://eastus2euap.oic.prod-aks.azure.com/${config.tenantId}/`) ||
      fic.issuer !== issuer.issuer || fic.subject !== `system:serviceaccount:${NAMESPACE}:identity-broker` ||
      fic.audiences?.length !== 1 || fic.audiences[0] !== 'api://AzureADTokenExchange') {
    throw new Error('Broker runtime federation does not match the actual cluster issuer and exact ServiceAccount.');
  }
  const application = azureJson(['ad', 'app', 'show', '--id', config.upstreamClientId, '-o', 'json'],
    value => ({ appId: value.appId, redirectUris: value.publicClient?.redirectUris }));
  return withKubeconfig(config, base => {
    const args = [...base, '--namespace', NAMESPACE, '--request-timeout=30s'];
    function read(resource, name, projection = projectBrokerRuntimeResource, namespace = NAMESPACE) {
      const result = execKubectl([...base, '--namespace', namespace, '--request-timeout=30s',
        'get', resource, '--field-selector', `metadata.name=${name}`, '-o', 'json'],
        { ...OPTIONS, projectJson: projection, preserveProjectedJson: true });
      if (result.status !== 0) throw new Error(`Broker runtime readback failed: ${redact(result.stderr)}`);
      return JSON.parse(result.stdout);
    }
    const route = read('httproutes', 'identity-broker', projectIdentityRoutingReadback);
    const gatewayNamespace = route?.spec?.parentRefs?.[0]?.namespace;
    if (gatewayNamespace !== 'agentweaver-v1-gateway') {
      throw new Error('Broker runtime route does not identify the exact approved Gateway namespace.');
    }
    const routing = observeExistingP0BrokerRouting({
      certificate: read('defaultdomaincertificates', 'identity-broker', projectIdentityRoutingReadback),
      gateway: read('gateways', 'identity-broker', projectIdentityRoutingReadback, gatewayNamespace),
      route,
      backendPolicy: read('backendtlspolicies', 'identity-broker', projectIdentityRoutingReadback),
    });
    if (!routing.hostnameAligned) throw new Error('Broker runtime setup requires independently observed aligned native routing specs.');
    if (application.appId !== config.upstreamClientId ||
        !application.redirectUris?.includes(`${routing.issuer}signin-oidc`)) {
      throw new Error('The exact upstream public application does not contain the native Broker callback; no app mutation was attempted.');
    }
    const data = brokerAcceptanceConfiguration(config, { issuer: routing.issuer, identity });
    const existing = read('configmaps', CONFIG);
    if (existing && (existing.metadata.name !== CONFIG || existing.metadata.namespace !== NAMESPACE ||
        existing.metadata.owner !== 'installer' || existing.metadata.service !== 'identity-broker' ||
        existing.immutable !== true || existing.dataHash !== orderedHash(data))) {
      throw new Error('Existing Broker runtime ConfigMap differs from this exact acceptance fixture; refusing replacement.');
    }
    const root = join(config.repoRoot ?? process.cwd(), 'deploy', 'k8s', 'base', 'identity-broker');
    const objects = ['serviceaccount.yaml', 'service.yaml', 'egress.yaml', 'deployment.yaml'].map(file => {
      const manifest = readFileSync(join(root, file), 'utf8')
        .replaceAll('CHANGEME-identity-broker-client-id', identity.clientId)
        .replaceAll('CHANGEME-tenant-id', config.tenantId)
        .replaceAll('CHANGEME-KEYVAULT-HOST', `${config.resourceGroup}-kv.vault.azure.net`)
        .replaceAll('CHANGEME-POSTGRES-HOST', `${config.resourceGroup}-pg.postgres.database.azure.com`)
        .replaceAll('CHANGEME-UPSTREAM-OIDC-AUTHORITY', 'login.microsoftonline.com')
        .replaceAll('CHANGEME-UPSTREAM-OIDC-METADATA-HOST', 'login.microsoftonline.com')
        .replaceAll(SENTINEL, config.brokerImage);
      if (manifest.includes('CHANGEME-') || manifest.includes('registry.invalid')) {
        throw new Error('Broker runtime source still contains a non-deployable placeholder.');
      }
      const rendered = execKubectl([...args, 'create', '--dry-run=client', '--filename', '-', '-o', 'json'],
        { ...OPTIONS, input: manifest, projectJson: value => value, preserveProjectedJson: true });
      if (rendered.status !== 0) throw new Error(`Broker runtime manifest render failed: ${redact(rendered.stderr)}`);
      return JSON.parse(rendered.stdout);
    });
    if (objects.length !== 4 || new Set(objects.map(object => object.kind)).size !== 4 || objects.some(object =>
      !['ServiceAccount', 'Service', 'CiliumNetworkPolicy', 'Deployment'].includes(object.kind) ||
      object.metadata?.namespace !== NAMESPACE ||
      !['identity-broker', 'identity-broker-egress'].includes(object.metadata?.name))) {
      throw new Error('Broker runtime rendered manifests do not identify the exact four source objects.');
    }
    const absent = [];
    for (const object of objects) {
      const expectedHash = brokerObjectHash(object);
      const current = read(object.kind, object.metadata.name);
      if (current && (current.metadata.name !== object.metadata.name || current.metadata.namespace !== NAMESPACE ||
          current.specHash !== expectedHash)) {
        throw new Error(`Existing Broker ${object.kind} differs from the exact source configuration; refusing replacement.`);
      }
      if (!current) absent.push(object);
    }
    if (!existing) {
      const configMap = { apiVersion: 'v1', kind: 'ConfigMap', metadata: {
        name: CONFIG, namespace: NAMESPACE, labels: {
          'agentweaver.io/service': 'identity-broker', 'agentweaver.io/managed-by': 'installer',
          'agentweaver.io/acceptance-run': config.acceptanceRunId,
        },
      }, immutable: true, data };
      const result = execKubectl([...args, 'create', '--filename', '-', '-o', 'name'],
        { ...OPTIONS, input: JSON.stringify(configMap) });
      if (result.status !== 0) throw new Error(`Broker runtime ConfigMap create failed: ${redact(result.stderr)}`);
    }
    for (const object of absent) {
      const result = execKubectl([...args, 'create', '--filename', '-', '-o', 'name'],
        { ...OPTIONS, input: JSON.stringify(object) });
      if (result.status !== 0) throw new Error(`Broker runtime ${object.kind} create failed: ${redact(result.stderr)}`);
      const current = read(object.kind, object.metadata.name);
      if (!current?.metadata.uid || current.specHash !== brokerObjectHash(object)) {
        throw new Error(`Broker runtime ${object.kind} did not read back its exact source configuration.`);
      }
    }
    const readback = read('configmaps', CONFIG);
    if (!readback?.metadata.uid || readback.dataHash !== orderedHash(data)) {
      throw new Error('Broker runtime ConfigMap readback did not preserve the exact intended configuration.');
    }
    return {
      namespace: NAMESPACE, configMap: CONFIG, configMapUid: readback.metadata.uid,
      configMapCreated: !existing, issuer: routing.issuer, configurationOnly: true,
      gatewayProgrammed: routing.gatewayProgrammed, routingBlocked: !routing.gatewayProgrammed,
      runtimeVerified: false, acceptanceClientId: data.IdentityBroker__Clients__0__ClientId,
      brokerImage: config.brokerImage, runtimeIdentityClientId: identity.clientId,
    };
  });
}
