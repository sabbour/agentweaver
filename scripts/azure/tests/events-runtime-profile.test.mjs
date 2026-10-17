import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { renderEventsProfile } from '../../../deploy/k8s/profiles/events-and-sessions/render.mjs';
import { runtimeContractSettings, runtimeListFixture } from '../../release/tests/runtime-list-fixture.mjs';

const exampleUrl = new URL('../../../deploy/k8s/profiles/events-and-sessions/runtime-inputs.example.json', import.meta.url);
const example = JSON.parse(readFileSync(exampleUrl, 'utf8'));
const runtimeConfigUrl = new URL('../../../deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json', import.meta.url);
const rateCardKey = 'EventsAndSessions__Cost__Copilot__RateCard__Json';
const defaultRateCardJson = JSON.parse(readFileSync(runtimeConfigUrl, 'utf8')).data[rateCardKey];
function fixture() {
  const input = structuredClone(example);
  Object.assign(input, {
    image: `ghcr.io/sabbour/agentweaver.eventsandsessions@sha256:${'a'.repeat(64)}`,
    imageUser: 1654, imageGroup: 1654, tlsSecretName: 'events-and-sessions-test-tls',
  });
  Object.assign(input.configuration, {
    Identity__Issuer: 'https://agentweaver.test.eastus2euap.aksapp.io/',
    Identity__Audience: 'events-test-audience',
    EventsAndSessions__Knowledge__BaseAddress: 'https://knowledge.agentweaver-v1-p0.svc.cluster.local:8443/',
    ProjectsConfig__BaseAddress: 'https://projects-config.agentweaver-v1-p0.svc.cluster.local:8443/',
    ProjectsConfig__AuthorizationContext__OwnerBaseAddress: 'https://projects-config.agentweaver-v1-p0.svc.cluster.local:8443/',
    ProjectsConfig__AuthorizationContext__Audience: 'projects-test-audience',
    EventsAndSessions__OrchestratorOwner__OwnerBaseAddress: 'https://orchestrator.agentweaver-v1-p0.svc.cluster.local:8443/',
    EventsAndSessions__OrchestratorOwner__Audience: 'core-test-audience',
  });
  return input;
}
const item = (result, kind) => result.items.find(value => value.kind === kind);
function eventsContractSettings(deployment) {
  const settings = runtimeContractSettings(deployment);
  const annotations = Object.fromEntries(Object.entries(settings.annotations ?? {})
    .filter(([key]) => key !== 'agentweaver.io/runtime-config-sha256'));
  return {
    ...settings,
    annotations: Object.keys(annotations).length > 0 ? annotations : undefined,
    env: settings.env.filter(value =>
      value.name !== 'EventsAndSessions__Cost__Copilot__OptionsRevision'),
  };
}

test('example is deliberately incomplete and cannot render a deployable profile', () => {
  assert.throws(() => renderEventsProfile(example), /immutable product Events image/);
});

test('pure renderer retains every required existing startup key and does not mutate input', () => {
  const input = fixture();
  assert.equal(input.configuration.EventsAndSessions__Provider__ResourceId,
    'events-sessions-provider:p1-v1.0.0');
  assert.equal(input.configuration.EventsAndSessions__Provider__ResourceGeneration, '1');
  assert.equal(input.configuration.EventsAndSessions__SessionMaterial__ContainerUri,
    'https://awv1p0blob.blob.core.windows.net/session-material');
  assert.equal(input.configuration.AgentHost__ModelBindingsRevision, 'p1-v1.0.1');
  assert.equal(input.configuration.AgentHost__ModelBindings__reasoning__ModelId, 'gpt-6.1-sol');
  assert.equal(input.configuration.AgentHost__ModelBindings__reasoning__SourceMode, 'HostedCopilot');
  assert.equal(input.configuration.AgentHost__ModelBindings__coding__ModelId, 'gpt-6-luna');
  assert.equal(input.configuration.AgentHost__ModelBindings__coding__SourceMode, 'HostedCopilot');
  assert.equal(input.configuration['AgentHost__ModelBindings__gpt-6.1-sol__ModelId'], 'gpt-6.1-sol');
  assert.equal(input.configuration['AgentHost__ModelBindings__gpt-6.1-sol__SourceMode'], 'HostedCopilot');
  assert.equal(input.configuration['AgentHost__ModelBindings__gpt-6-luna__ModelId'], 'gpt-6-luna');
  assert.equal(input.configuration['AgentHost__ModelBindings__gpt-6-luna__SourceMode'], 'HostedCopilot');
  const before = structuredClone(input);
  const result = renderEventsProfile(input);
  assert.deepEqual(input, before);
  assert.deepEqual(result.items.map(value => value.kind),
    ['ServiceAccount', 'ConfigMap', 'Service', 'Deployment', 'CiliumNetworkPolicy']);
  assert.deepEqual(item(result, 'ConfigMap').data, {
    ...input.configuration,
    [rateCardKey]: input.configuration[rateCardKey] ?? defaultRateCardJson,
  });
  assert.equal(item(result, 'ConfigMap').immutable, false);
  for (const value of result.items) assert.equal(value.metadata.namespace, 'agentweaver-v1-p0');
  const sa = item(result, 'ServiceAccount');
  assert.equal(sa.metadata.name, 'events-and-sessions');
  assert.equal(sa.metadata.annotations['azure.workload.identity/client-id'], input.configuration.EventsAndSessions__Database__WorkloadIdentity__ClientId);
  assert.equal(sa.metadata.annotations['azure.workload.identity/tenant-id'], input.configuration.EventsAndSessions__Database__WorkloadIdentity__TenantId);
});

test('every required field rejects missing, empty, control-bearing and non-string values', () => {
  for (const key of Object.keys(fixture().configuration)) {
    for (const value of [null, '', ' ', 'bad\nvalue', 1]) {
      const input = fixture();
      input.configuration[key] = value;
      assert.throws(() => renderEventsProfile(input), Error, key);
    }
    const input = fixture();
    delete input.configuration[key];
    assert.throws(() => renderEventsProfile(input), Error, key);
  }
  for (const key of Object.keys(fixture())) {
    const input = fixture();
    delete input[key];
    assert.throws(() => renderEventsProfile(input), /missing field/, key);
  }
});

test('only the exact admitted target, runtime identity, role, token path and database are accepted', () => {
  for (const key of ['subscriptionId', 'resourceGroup', 'clusterName']) {
    const input = fixture();
    input[key] = 'foreign';
    assert.throws(() => renderEventsProfile(input), /exact existing approved/);
  }
  for (const key of ['ConnectionStrings__EventsAndSessions',
    'EventsAndSessions__Database__WorkloadIdentity__TenantId',
    'EventsAndSessions__Database__WorkloadIdentity__ClientId',
    'EventsAndSessions__Database__WorkloadIdentity__TokenFilePath',
    'EventsAndSessions__Provider__DatabaseName', 'EventsAndSessions__Provider__Schema']) {
    const input = fixture();
    input.configuration[key] = 'foreign';
    assert.throws(() => renderEventsProfile(input), /admitted Events runtime mapping/, key);
  }
  for (const role of ['foundation_probe_runtime', 'aw-v1-p0-id-identity-broker', 'postgres', 'events_migration']) {
    const input = fixture();
    input.configuration.ConnectionStrings__EventsAndSessions =
      input.configuration.ConnectionStrings__EventsAndSessions.replace('aw-v1-p0-id-events-and-sessions', role);
    assert.throws(() => renderEventsProfile(input), /runtime mapping/);
  }
});

test('passwords, secrets, DDL, migration flags, remote tool TLS and unrecognized fields have no input path', () => {
  for (const key of ['Password', 'ClientSecret', 'SQL', 'args', 'command', 'bootstrap', 'migrate']) {
    const input = fixture();
    input[key] = 'forbidden';
    assert.throws(() => renderEventsProfile(input), /unsupported field/);
  }
  for (const key of ['ConnectionStrings__EventsAndSessionsMigration',
    'EventsAndSessions__Migration__WorkloadIdentity__ClientId',
    'EventsAndSessions__RemoteToolResults__Tls__ListenerPort', 'SSL_CERT_FILE',
    'AZURE_CLIENT_SECRET', 'Kestrel__Endpoints__RemoteToolResults__Url']) {
    const input = fixture();
    input.configuration[key] = 'forbidden';
    assert.throws(() => renderEventsProfile(input), /unsupported field/);
  }
  for (const suffix of [';Password=secret', ';SSL Mode=Disable', ';Options=-c search_path=public']) {
    const input = fixture();
    input.configuration.ConnectionStrings__EventsAndSessions += suffix;
    assert.throws(() => renderEventsProfile(input), /runtime mapping/);
  }
});

test('mutable, wrong-component, example and zero-digest images are rejected', () => {
  for (const image of [null, 'ghcr.io/sabbour/agentweaver.eventsandsessions:latest',
    `ghcr.io/sabbour/agentweaver.eventsandsessions@sha256:${'0'.repeat(64)}`,
    ...['agentweaver-foundation-probe', 'agentweaver.identity.broker', 'agentweaver-agenthost',
      'agentweaver-events-and-sessions'].map(name => `ghcr.io/sabbour/${name}@sha256:${'a'.repeat(64)}`),
    `registry.invalid/agentweaver.eventsandsessions@sha256:${'a'.repeat(64)}`]) {
    const input = fixture();
    input.image = image;
    assert.throws(() => renderEventsProfile(input), /product Events image/);
  }
});

test('operator-reviewed numeric image user and group are required, bounded and copied to file access settings', () => {
  for (const key of ['imageUser', 'imageGroup']) {
    for (const value of [null, 0, -1, '1654', 1.5, 2147483648, NaN, Infinity]) {
      const input = fixture();
      input[key] = value;
      assert.throws(() => renderEventsProfile(input), /positive integer/, key);
    }
  }
  const input = fixture();
  input.imageUser = 2001;
  input.imageGroup = 2002;
  const pod = item(renderEventsProfile(input), 'Deployment').spec.template.spec;
  assert.equal(pod.securityContext.runAsUser, 2001);
  assert.equal(pod.securityContext.runAsGroup, 2002);
  assert.equal(pod.securityContext.fsGroup, 2002);
  assert.equal(pod.volumes[0].secret.defaultMode, 0o440);
});

test('Blob requires one explicit valid container in the exact existing account with no credential or path encoding', () => {
  for (const uri of [
    null, 'https://awv1p0blob.blob.core.windows.net/', 'http://awv1p0blob.blob.core.windows.net/test',
    'https://foreign.blob.core.windows.net/test', 'https://127.0.0.1/test',
    'https://user:password@awv1p0blob.blob.core.windows.net/test',
    'https://awv1p0blob.blob.core.windows.net/test?sig=secret',
    'https://awv1p0blob.blob.core.windows.net/test#fragment',
    'https://awv1p0blob.blob.core.windows.net/test/extra',
    'https://awv1p0blob.blob.core.windows.net/test%2fextra',
    'https://awv1p0blob.blob.core.windows.net/%74est',
    'https://awv1p0blob.blob.core.windows.net/test/../other',
    'https://awv1p0blob.blob.core.windows.net/test\\extra',
    'https://awv1p0blob.blob.core.windows.net/test--container',
    'https://awv1p0blob.blob.core.windows.net/ab',
    `https://awv1p0blob.blob.core.windows.net/${'a'.repeat(64)}`,
    'https://awv1p0blob.blob.core.windows.net/test/',
  ]) {
    const input = fixture();
    input.configuration.EventsAndSessions__SessionMaterial__ContainerUri = uri;
    assert.throws(() => renderEventsProfile(input));
  }
});

test('owners require exact private service names, explicit HTTPS ports, and the same Projects authority address', () => {
  for (const key of ['EventsAndSessions__Knowledge__BaseAddress', 'ProjectsConfig__BaseAddress',
    'EventsAndSessions__OrchestratorOwner__OwnerBaseAddress']) {
    for (const value of ['https://foreign.example:8443/', 'http://knowledge.agentweaver-v1-p0.svc.cluster.local:8443/',
      'https://knowledge.agentweaver-v1-p0.svc.cluster.local/', 'https://knowledge.agentweaver-v1-p0.svc.cluster.local:8443/path',
      'https://knowledge.agentweaver-v1-p0.svc.cluster.local:8443/?token=x']) {
      const input = fixture();
      input.configuration[key] = value;
      assert.throws(() => renderEventsProfile(input), Error, key);
    }
  }
  const input = fixture();
  input.configuration.ProjectsConfig__AuthorizationContext__OwnerBaseAddress =
    'https://projects-config.agentweaver-v1-p0.svc.cluster.local:9443/';
  assert.throws(() => renderEventsProfile(input), /same exact Projects owner/);
});

test('provider values, managed issuer and existing Events-only TLS Secret are checked', () => {
  for (const [key, values] of [
    ['EventsAndSessions__Provider__ResourceGeneration', ['0', '-1', '1.5', '01', '9007199254740992']],
    ['EventsAndSessions__Provider__ResourceId', ['bad/path', 'a'.repeat(257)]],
    ['Identity__Issuer', ['https://foreign.example/', 'https://agentweaver.test.aksapp.io:8443/',
      'https://user@agentweaver.test.aksapp.io/', 'https://agentweaver.test.aksapp.io/?query=x']],
  ]) {
    for (const value of values) {
      const input = fixture();
      input.configuration[key] = value;
      assert.throws(() => renderEventsProfile(input), Error, key);
    }
  }
  for (const value of [null, '', 'identity-broker-tls', '../events-and-sessions-tls', 'events-and-sessions-' + 'a'.repeat(64)]) {
    const input = fixture();
    input.tlsSecretName = value;
    assert.throws(() => renderEventsProfile(input), /existing Events TLS Secret/);
  }
});

test('workload is private, restricted, read-only and bounded without migration or extra TLS listeners', () => {
  const result = renderEventsProfile(fixture());
  const service = item(result, 'Service');
  assert.equal(service.spec.type, 'ClusterIP');
  assert.deepEqual(service.spec.ports, [{ name: 'https', port: 8443, targetPort: 'https', protocol: 'TCP' }]);
  const pod = item(result, 'Deployment').spec.template.spec;
  assert.equal(pod.serviceAccountName, 'events-and-sessions');
  assert.equal(pod.securityContext.runAsNonRoot, true);
  assert.equal(pod.securityContext.seccompProfile.type, 'RuntimeDefault');
  assert.equal(pod.containers.length, 1);
  const container = pod.containers[0];
  assert.deepEqual(container.securityContext, { allowPrivilegeEscalation: false,
    capabilities: { drop: ['ALL'] }, readOnlyRootFilesystem: true });
  assert.deepEqual(container.resources, { requests: { cpu: '100m', memory: '128Mi' },
    limits: { cpu: '500m', memory: '512Mi' } });
  assert.equal(container.volumeMounts[0].readOnly, true);
  assert.equal(container.volumeMounts[0].mountPath, '/var/run/events-and-sessions-tls');
  assert.equal(pod.volumes[0].secret.optional, false);
  assert.equal(pod.volumes[1].emptyDir.sizeLimit, '64Mi');
  assert.equal(container.livenessProbe.httpGet.scheme, 'HTTPS');
  assert.equal(container.readinessProbe.httpGet.scheme, 'HTTPS');
  assert.equal(container.args, undefined);
  assert.equal(container.command, undefined);
  assert.equal(result.items.some(value => ['Secret', 'Job', 'Ingress', 'Gateway'].includes(value.kind)), false);
  assert.doesNotMatch(JSON.stringify(result), /RemoteToolResults|Migration|--migrate|SSL_CERT|secretRef/);
});

test('release runtime preserves the accepted Events identity, listener and health contract', () => {
  const accepted = item(renderEventsProfile(fixture()), 'Deployment');
  const runtime = runtimeListFixture().items.find(value =>
    value.kind === 'Deployment' && value.metadata.name === 'events-and-sessions');
  assert.deepEqual(eventsContractSettings(runtime), eventsContractSettings(accepted));
});

test('network policy allows only exact callers, owners, required Azure endpoints and DNS names', () => {
  const input = fixture();
  input.configuration.EventsAndSessions__Knowledge__BaseAddress =
    'https://knowledge.agentweaver-v1-p0.svc.cluster.local:9443/';
  const policy = item(renderEventsProfile(input), 'CiliumNetworkPolicy').spec;
  assert.deepEqual(policy.endpointSelector, { matchLabels: { 'agentweaver.io/workload': 'events-and-sessions' } });
  assert.deepEqual(policy.ingress[0].fromEntities, ['host']);
  assert.deepEqual(policy.ingress[1].fromEndpoints.map(value => value.matchLabels['agentweaver.io/workload']),
    ['agenthost', 'orchestrator', 'gateway', 'knowledge', 'projects-config']);
  for (const endpoint of policy.ingress[1].fromEndpoints)
    assert.equal(endpoint.matchLabels['k8s:io.kubernetes.pod.namespace'], 'agentweaver-v1-p0');
  assert.equal(policy.egress[1].toPorts[0].ports[0].port, '9443');
  assert.deepEqual(policy.egress.slice(1, 4).map(value => value.toEndpoints[0].matchLabels['agentweaver.io/workload']),
    ['knowledge', 'projects-config', 'orchestrator']);
  assert.deepEqual(policy.egress[4].toFQDNs.map(value => value.matchName),
    ['agentweaver.test.eastus2euap.aksapp.io', 'login.microsoftonline.com', 'awv1p0blob.blob.core.windows.net']);
  assert.deepEqual(policy.egress[5].toFQDNs, [{ matchName: 'aw-v1-p0-pg.postgres.database.azure.com' }]);
  assert.equal(policy.egress[5].toPorts[0].ports[0].port, '5432');
  assert.doesNotMatch(JSON.stringify(policy), /matchPattern|toCIDR|world|\*/);
});

test('ordinary Broker and Probe compositions do not include this selected profile', () => {
  for (const file of ['base/kustomization.yaml', 'acceptance/foundation-probe/kustomization.yaml']) {
    const source = readFileSync(new URL(`../../../deploy/k8s/${file}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /profiles|events-and-sessions/);
  }
});
