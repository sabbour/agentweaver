import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { renderProjectsProfile } from '../../../deploy/k8s/profiles/projects-config/render.mjs';

const rendererUrl = new URL('../../../deploy/k8s/profiles/projects-config/render.mjs', import.meta.url);
const exampleUrl = new URL('../../../deploy/k8s/profiles/projects-config/runtime-inputs.example.json', import.meta.url);
const example = JSON.parse(readFileSync(exampleUrl, 'utf8'));
function fixture() {
  const input = structuredClone(example);
  Object.assign(input, {
    image: `ghcr.io/sabbour/agentweaver.projects.config@sha256:${'a'.repeat(64)}`,
    imageUser: 1654, imageGroup: 1654, tlsSecretName: 'projects-config-test-tls',
    providerCatalog: { configMapName: 'projects-config-reviewed-catalog', sourceCommit: 'b'.repeat(40) },
  });
  Object.assign(input.configuration, {
    ProjectsConfig__Authentication__Authority: 'https://agentweaver.test.eastus2euap.aksapp.io/',
    ProjectsConfig__Authentication__Audience: 'projects-test-audience',
  });
  return input;
}
const item = (result, kind) => result.items.find(value => value.kind === kind);

test('example is deliberately incomplete and cannot render a deployable Projects profile', () => {
  assert.throws(() => renderProjectsProfile(example), /immutable product Projects image/);
});

test('pure renderer retains exact startup configuration and does not mutate its input', () => {
  const input = fixture();
  const before = structuredClone(input);
  const result = renderProjectsProfile(input);
  assert.deepEqual(input, before);
  assert.deepEqual(result.items.map(value => value.kind),
    ['ServiceAccount', 'ConfigMap', 'Service', 'Deployment', 'CiliumNetworkPolicy']);
  for (const value of result.items) assert.equal(value.metadata.namespace, 'agentweaver-v1-p0');
  assert.deepEqual(item(result, 'ConfigMap').data, input.configuration);
  assert.equal(item(result, 'ConfigMap').immutable, true);
  const sa = item(result, 'ServiceAccount');
  assert.equal(sa.metadata.name, 'projects-config');
  assert.equal(sa.metadata.annotations['azure.workload.identity/client-id'],
    input.configuration.ProjectsConfig__Database__WorkloadIdentity__ClientId);
  assert.equal(sa.metadata.annotations['azure.workload.identity/tenant-id'],
    input.configuration.ProjectsConfig__Database__WorkloadIdentity__TenantId);
});

test('missing root, catalog and runtime configuration fields fail explicitly', () => {
  for (const key of Object.keys(fixture())) {
    const input = fixture();
    delete input[key];
    assert.throws(() => renderProjectsProfile(input), /missing field/, key);
  }
  for (const section of ['providerCatalog', 'configuration']) {
    for (const key of Object.keys(fixture()[section])) {
      const input = fixture();
      delete input[section][key];
      assert.throws(() => renderProjectsProfile(input), /missing field/, `${section}.${key}`);
    }
    for (const value of [null, [], 'configuration']) {
      const input = fixture();
      input[section] = value;
      assert.throws(() => renderProjectsProfile(input), /explicit object/, section);
    }
  }
});

test('each runtime key rejects empty, padded, control-bearing and non-string values', () => {
  for (const key of Object.keys(fixture().configuration)) {
    for (const value of [null, '', ' ', ' padded ', 'bad\nvalue', 'bad\u0000value', 1]) {
      const input = fixture();
      input.configuration[key] = value;
      assert.throws(() => renderProjectsProfile(input), /explicit nonempty string/, key);
    }
  }
});

test('only the exact existing target, Projects identity, runtime role and database are accepted', () => {
  for (const key of ['subscriptionId', 'resourceGroup', 'clusterName']) {
    const input = fixture();
    input[key] = 'foreign';
    assert.throws(() => renderProjectsProfile(input), /exact existing approved/);
  }
  for (const key of ['ConnectionStrings__ProjectsConfig',
    'ProjectsConfig__Database__WorkloadIdentity__TenantId',
    'ProjectsConfig__Database__WorkloadIdentity__ClientId',
    'ProjectsConfig__Database__WorkloadIdentity__TokenFilePath']) {
    const input = fixture();
    input.configuration[key] = 'foreign';
    assert.throws(() => renderProjectsProfile(input), /admitted Projects runtime mapping/, key);
  }
  for (const role of ['foundation_probe_runtime', 'aw-v1-p0-id-events-and-sessions',
    'aw-v1-p0-id-identity-broker', 'postgres', 'projects_migration']) {
    const input = fixture();
    input.configuration.ConnectionStrings__ProjectsConfig =
      input.configuration.ConnectionStrings__ProjectsConfig.replace('aw-v1-p0-id-projects-config', role);
    assert.throws(() => renderProjectsProfile(input), /runtime mapping/, role);
  }
});

test('passwords, migration identities, Blob credentials, custom trust and commands have no input path', () => {
  for (const key of ['Password', 'ClientSecret', 'SQL', 'args', 'command', 'bootstrap', 'migrate', 'schema']) {
    const input = fixture();
    input[key] = 'forbidden';
    assert.throws(() => renderProjectsProfile(input), /unsupported field/, key);
  }
  for (const key of ['ConnectionStrings__ProjectsConfigMigration',
    'ProjectsConfig__Migration__WorkloadIdentity__ClientId', 'ProjectsConfig__SkillContentContainerUri',
    'ProjectsConfig__ProviderCatalog__Registrations__0__Id', 'SSL_CERT_FILE',
    'AZURE_CLIENT_SECRET', 'Kestrel__Endpoints__RemoteToolResults__Url']) {
    const input = fixture();
    input.configuration[key] = 'forbidden';
    assert.throws(() => renderProjectsProfile(input), /unsupported field/, key);
  }
  const input = fixture();
  input.providerCatalog.snapshot = {};
  assert.throws(() => renderProjectsProfile(input), /unsupported field/);
  for (const suffix of [';Password=secret', ';SSL Mode=Disable', ';Options=-c search_path=public']) {
    const value = fixture();
    value.configuration.ConnectionStrings__ProjectsConfig += suffix;
    assert.throws(() => renderProjectsProfile(value), /runtime mapping/);
  }
});

test('mutable, wrong-component, example and zero-digest product images are rejected', () => {
  for (const image of [null, 'ghcr.io/sabbour/agentweaver.projects.config:latest',
    `ghcr.io/sabbour/agentweaver.projects.config@sha256:${'0'.repeat(64)}`,
    ...['agentweaver-foundation-probe', 'agentweaver.identity.broker',
      'agentweaver.eventsandsessions', 'agentweaver-projects-config']
      .map(name => `ghcr.io/sabbour/${name}@sha256:${'a'.repeat(64)}`),
    `registry.invalid/agentweaver.projects.config@sha256:${'a'.repeat(64)}`]) {
    const input = fixture();
    input.image = image;
    assert.throws(() => renderProjectsProfile(input), /product Projects image/);
  }
});

test('operator-reviewed image user and group must be bounded positive integers', () => {
  for (const key of ['imageUser', 'imageGroup']) {
    for (const value of [null, 0, -1, '1654', 1.5, 2147483648, NaN, Infinity]) {
      const input = fixture();
      input[key] = value;
      assert.throws(() => renderProjectsProfile(input), /positive integer/, key);
    }
  }
  const input = fixture();
  input.imageUser = 2001;
  input.imageGroup = 2002;
  const pod = item(renderProjectsProfile(input), 'Deployment').spec.template.spec;
  assert.equal(pod.securityContext.runAsUser, 2001);
  assert.equal(pod.securityContext.runAsGroup, 2002);
  assert.equal(pod.securityContext.fsGroup, 2002);
  assert.equal(pod.volumes[0].secret.defaultMode, 0o440);
});

test('authentication requires the managed Broker HTTPS root without credentials or trust overrides', () => {
  for (const authority of ['http://agentweaver.test.aksapp.io/', 'https://foreign.example/',
    'https://agentweaver.test.aksapp.io:8443/', 'https://user@agentweaver.test.aksapp.io/',
    'https://agentweaver.test.aksapp.io/?query=x', 'https://agentweaver.test.aksapp.io/#fragment',
    'https://agentweaver.test.aksapp.io/path', 'https://agentweaver.test.aksapp.io%2f/']) {
    const input = fixture();
    input.configuration.ProjectsConfig__Authentication__Authority = authority;
    assert.throws(() => renderProjectsProfile(input), /Projects authority/);
  }
});

test('TLS requires an explicit existing Projects-only Secret name', () => {
  for (const name of [null, '', 'identity-broker-tls', 'events-and-sessions-tls',
    '../projects-config-tls', 'projects-config-' + 'a'.repeat(64)]) {
    const input = fixture();
    input.tlsSecretName = name;
    assert.throws(() => renderProjectsProfile(input), /existing Projects TLS Secret/);
  }
});

test('an existing catalog reference and nonzero source commit are required without creating a catalog', () => {
  for (const name of [null, '', ' ', '../catalog', 'UPPERCASE', 'catalog/other', 'a'.repeat(254),
    'projects-config-runtime-config']) {
    const input = fixture();
    input.providerCatalog.configMapName = name;
    assert.throws(() => renderProjectsProfile(input), /existing provider catalog ConfigMap/);
  }
  for (const commit of [null, '', 'main', '0'.repeat(40), 'a'.repeat(39), 'A'.repeat(40), 1]) {
    const input = fixture();
    input.providerCatalog.sourceCommit = commit;
    assert.throws(() => renderProjectsProfile(input), /nonzero source commit/);
  }
  const input = fixture();
  const result = renderProjectsProfile(input);
  const deployment = item(result, 'Deployment');
  assert.equal(deployment.spec.template.metadata.annotations['agentweaver.io/provider-catalog-source'],
    input.providerCatalog.sourceCommit);
  assert.equal(result.items.filter(value => value.kind === 'ConfigMap').length, 1);
  assert.equal(result.items.some(value => value.metadata.name === input.providerCatalog.configMapName), false);
});

test('catalog environment prefix cannot overwrite database, authentication or listener configuration', () => {
  const input = fixture();
  const result = renderProjectsProfile(input);
  const container = item(result, 'Deployment').spec.template.spec.containers[0];
  assert.deepEqual(container.envFrom, [
    { configMapRef: { name: 'projects-config-runtime-config', optional: false } },
    { prefix: 'ProjectsConfig__ProviderCatalog__',
      configMapRef: { name: input.providerCatalog.configMapName, optional: false } },
  ]);
  const prefix = container.envFrom[1].prefix;
  for (const key of [...Object.keys(input.configuration), ...container.env.map(value => value.name)])
    assert.notEqual(`${prefix}${key}`, key);
  assert.equal(`${prefix}Registrations__0__Id`,
    'ProjectsConfig__ProviderCatalog__Registrations__0__Id');
  assert.deepEqual(item(result, 'ConfigMap').data, input.configuration);
});

test('workload remains private, restricted and bounded without migration or additional TLS listeners', () => {
  const result = renderProjectsProfile(fixture());
  const service = item(result, 'Service');
  assert.equal(service.spec.type, 'ClusterIP');
  assert.deepEqual(service.spec.ports, [{ name: 'https', port: 8443, targetPort: 'https', protocol: 'TCP' }]);
  const pod = item(result, 'Deployment').spec.template.spec;
  assert.equal(pod.serviceAccountName, 'projects-config');
  assert.equal(pod.securityContext.runAsNonRoot, true);
  assert.equal(pod.securityContext.seccompProfile.type, 'RuntimeDefault');
  assert.equal(pod.containers.length, 1);
  const container = pod.containers[0];
  assert.deepEqual(container.securityContext, { allowPrivilegeEscalation: false,
    capabilities: { drop: ['ALL'] }, readOnlyRootFilesystem: true });
  assert.deepEqual(container.resources, { requests: { cpu: '100m', memory: '128Mi' },
    limits: { cpu: '500m', memory: '512Mi' } });
  assert.equal(container.volumeMounts[0].readOnly, true);
  assert.equal(container.volumeMounts[0].mountPath, '/var/run/projects-config-tls');
  assert.equal(pod.volumes[0].secret.optional, false);
  assert.equal(pod.volumes[1].emptyDir.sizeLimit, '64Mi');
  assert.equal(container.livenessProbe.httpGet.scheme, 'HTTPS');
  assert.equal(container.readinessProbe.httpGet.scheme, 'HTTPS');
  assert.equal(container.args, undefined);
  assert.equal(container.command, undefined);
  assert.equal(result.items.some(value => ['Secret', 'Job', 'Ingress', 'Gateway'].includes(value.kind)), false);
  assert.doesNotMatch(JSON.stringify(result), /Migration|--migrate|SSL_CERT|ClientSecret|SkillContent|blob\.core/);
});

test('network access is limited to scoped callers, health probes, DNS, issuer, Entra and PostgreSQL', () => {
  const policy = item(renderProjectsProfile(fixture()), 'CiliumNetworkPolicy').spec;
  assert.deepEqual(policy.endpointSelector, { matchLabels: { 'agentweaver.io/workload': 'projects-config' } });
  assert.deepEqual(policy.ingress[0].fromEntities, ['host']);
  assert.deepEqual(policy.ingress[1].fromEndpoints.map(value => value.matchLabels['agentweaver.io/workload']),
    ['identity-broker', 'orchestrator', 'gateway', 'events-and-sessions', 'knowledge', 'environment', 'agenthost']);
  for (const endpoint of policy.ingress[1].fromEndpoints)
    assert.equal(endpoint.matchLabels['k8s:io.kubernetes.pod.namespace'], 'agentweaver-v1-p0');
  assert.deepEqual(policy.egress[0].toPorts[0].rules.dns.map(value => value.matchName),
    ['agentweaver.test.eastus2euap.aksapp.io', 'login.microsoftonline.com',
      'aw-v1-p0-pg.postgres.database.azure.com', 'aw-v1-p0-pg.privatelink.postgres.database.azure.com']);
  assert.deepEqual(policy.egress[1].toFQDNs.map(value => value.matchName),
    ['agentweaver.test.eastus2euap.aksapp.io', 'login.microsoftonline.com']);
  assert.equal(policy.egress[1].toPorts[0].ports[0].port, '443');
  assert.deepEqual(policy.egress[2].toFQDNs, [{ matchName: 'aw-v1-p0-pg.postgres.database.azure.com' }]);
  assert.equal(policy.egress[2].toPorts[0].ports[0].port, '5432');
  assert.doesNotMatch(JSON.stringify(policy), /matchPattern|toCIDR|world|blob|\*/);
});

test('ordinary Broker and Probe compositions do not include the selected Projects profile', () => {
  for (const file of ['base/kustomization.yaml', 'acceptance/foundation-probe/kustomization.yaml']) {
    const source = readFileSync(new URL(`../../../deploy/k8s/${file}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /profiles|projects-config/);
  }
});

test('CLI rejects omitted inputs and the incomplete example without applying anything', () => {
  const renderer = fileURLToPath(rendererUrl);
  const missing = spawnSync(process.execPath, [renderer], { encoding: 'utf8' });
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /This only renders source; it never applies it/);
  assert.equal(missing.stdout, '');
  const incomplete = spawnSync(process.execPath, [renderer, fileURLToPath(exampleUrl)], { encoding: 'utf8' });
  assert.equal(incomplete.status, 1);
  assert.match(incomplete.stderr, /immutable product Projects image/);
  assert.equal(incomplete.stdout, '');
});
