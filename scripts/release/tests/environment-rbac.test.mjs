import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse, parseAllDocuments } from 'yaml';
import { ENVIRONMENT_RBAC_REQUIREMENTS } from '../deploy-kustomize.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const manifestPath = path.join(root, 'deploy', 'k8s', 'proposed', 'environment-agent-sandbox-rbac.yaml');
const manifestText = readFileSync(manifestPath, 'utf8');
const resources = parseAllDocuments(manifestText).map(document => document.toJSON());
const platformEgressDir = path.join(root, 'deploy', 'k8s', 'rbac', 'platform-egress');
const platformEgressManifest = path.join(platformEgressDir, 'environment-platform-egress.yaml');

const expectedPlatformEgressObjects = [
  {
    apiVersion: 'rbac.authorization.k8s.io/v1',
    kind: 'Role',
    metadata: { name: 'environment-platform-egress', namespace: 'agentweaver' },
    rules: [{
      apiGroups: ['cilium.io'],
      resources: ['ciliumnetworkpolicies'],
      resourceNames: ['agenthost-platform-egress'],
      verbs: ['get', 'update', 'patch'],
    }],
  },
  {
    apiVersion: 'rbac.authorization.k8s.io/v1',
    kind: 'RoleBinding',
    metadata: { name: 'environment-platform-egress', namespace: 'agentweaver' },
    subjects: [{ kind: 'ServiceAccount', name: 'environment', namespace: 'agentweaver' }],
    roleRef: {
      apiGroup: 'rbac.authorization.k8s.io',
      kind: 'Role',
      name: 'environment-platform-egress',
    },
  },
];

function sortedObjects(items) {
  return [...items].sort((left, right) =>
    `${left.kind}/${left.metadata.name}`.localeCompare(`${right.kind}/${right.metadata.name}`));
}

function renderKustomization(directory) {
  return parseAllDocuments(execFileSync('kubectl', ['kustomize', directory], {
    cwd: root,
    encoding: 'utf8',
  })).map(document => document.toJSON());
}

function ruleEntries(resource, scope) {
  return resource.rules.flatMap(rule => rule.resources.flatMap(name => rule.verbs.map(verb =>
    `${scope}|${rule.apiGroups[0]}|${name}|${verb}|${rule.resourceNames?.[0] ?? ''}`)));
}

function expectedEntries(scope, rules) {
  return rules.flatMap(([group, resources, verbs, resourceName]) =>
    resources.flatMap(resource => verbs.map(verb =>
      `${scope}|${group}|${resource}|${verb}|${resourceName ?? ''}`)));
}

test('proposed Environment RBAC matches the reviewed namespaced and cluster permissions', () => {
  const role = resources.find(resource => resource.kind === 'Role');
  const clusterRole = resources.find(resource => resource.kind === 'ClusterRole');
  assert.equal(role.metadata.namespace, 'agentweaver');
  assert.equal(clusterRole.metadata.name, role.metadata.name);
  assert.deepEqual(resources.filter(resource => resource.kind === 'RoleBinding').map(binding => [
    binding.metadata.namespace,
    binding.subjects,
    binding.roleRef,
  ]), [[
    'agentweaver',
    [{ kind: 'ServiceAccount', name: 'environment', namespace: 'agentweaver' }],
    { apiGroup: 'rbac.authorization.k8s.io', kind: 'Role', name: role.metadata.name },
  ]]);
  assert.deepEqual(resources.filter(resource => resource.kind === 'ClusterRoleBinding').map(binding => [
    binding.subjects,
    binding.roleRef,
  ]), [[
    [{ kind: 'ServiceAccount', name: 'environment', namespace: 'agentweaver' }],
    { apiGroup: 'rbac.authorization.k8s.io', kind: 'ClusterRole', name: clusterRole.metadata.name },
  ]]);

  const expected = [
    ...expectedEntries('namespaced', [
      ['extensions.agents.x-k8s.io', ['sandboxclaims'], ['get', 'list', 'create', 'patch', 'delete']],
      ['extensions.agents.x-k8s.io', ['sandboxtemplates', 'sandboxwarmpools'], ['get', 'create', 'delete']],
      ['agents.x-k8s.io', ['sandboxes'], ['get', 'list']],
      ['', ['pods'], ['get', 'list']],
      ['', ['services'], ['get']],
      ['', ['configmaps'], ['get'], 'agenthost-runtime-v1'],
      ['', ['events'], ['list']],
      ['', ['persistentvolumeclaims'], ['get', 'create', 'delete']],
      ['cilium.io', ['ciliumnetworkpolicies'], ['get', 'create', 'update', 'delete']],
      ['cilium.io', ['ciliumnetworkpolicies'], ['patch'], 'agenthost-platform-egress'],
      ['', ['pods'], ['create', 'patch', 'delete']],
      ['', ['pods/log'], ['get']],
      ['', ['pods/exec', 'pods/attach'], ['create']],
    ]),
    ...expectedEntries('cluster', [
      ['node.k8s.io', ['runtimeclasses'], ['get'], 'kata-vm-isolation'],
      ['storage.k8s.io', ['storageclasses'], ['get']],
      ['', ['persistentvolumes'], ['get', 'patch']],
    ]),
  ].sort();
  assert.deepEqual([
    ...ruleEntries(role, 'namespaced'),
    ...ruleEntries(clusterRole, 'cluster'),
  ].sort(), expected);
  const expectedChecks = expected.map(entry => {
    const [scope, group, resource, verb, resourceName] = entry.split('|');
    return `${scope}|${group ? `${resource}.${group}` : resource}|${verb}|${resourceName}`;
  }).sort();
  assert.deepEqual(ENVIRONMENT_RBAC_REQUIREMENTS.map(({ verb, resource, resourceName, subresource, cluster }) =>
    `${cluster ? 'cluster' : 'namespaced'}|${subresource ? `${resource}/${subresource}` : resource}|` +
    `${verb}|${resourceName ?? ''}`).sort(),
  expectedChecks);
  assert.doesNotMatch(manifestText, /(^|[\s\[])watch([\s,\]]|$)|resources:\s*\[\s*\*\s*\]/);
  assert.doesNotMatch(manifestText, /resources:\s*\[[^\]]*\b(secrets|nodes|namespaces)\b/);
  assert.doesNotMatch(manifestText, /verbs:\s*\[[^\]]*\b(escalate|bind)\b/);
});

test('proposed RBAC stays outside the active Kustomize apply set', () => {
  const overlay = parse(readFileSync(path.join(root, 'deploy', 'k8s', 'kustomization.yaml'), 'utf8'));
  assert.equal(overlay.resources.some(resource => resource.includes('proposed/environment-agent-sandbox-rbac')), false);
  assert.equal(overlay.resources.some(resource => resource.includes('rbac/platform-egress')), false);
  assert.equal(resources.some(resource =>
    ['Role', 'RoleBinding', 'ClusterRole', 'ClusterRoleBinding'].includes(resource.kind)), true);
});

test('named platform-egress bundle contains exactly the reviewed Role and RoleBinding', () => {
  const bundleText = readFileSync(platformEgressManifest, 'utf8');
  const bundleObjects = parseAllDocuments(bundleText).map(document => document.toJSON());
  const kustomization = parse(readFileSync(path.join(platformEgressDir, 'kustomization.yaml'), 'utf8'));

  assert.deepEqual(sortedObjects(bundleObjects), sortedObjects(expectedPlatformEgressObjects));
  assert.deepEqual(kustomization, {
    apiVersion: 'kustomize.config.k8s.io/v1beta1',
    kind: 'Kustomization',
    namespace: 'agentweaver',
    resources: ['environment-platform-egress.yaml'],
  });
});

test('standalone render is exact and workload-root render excludes provider RBAC and policy', () => {
  const standalone = renderKustomization(platformEgressDir);
  assert.deepEqual(sortedObjects(standalone), sortedObjects(expectedPlatformEgressObjects));

  const workload = renderKustomization(path.join(root, 'deploy', 'k8s'));
  assert.equal(workload.some(resource =>
    ['Role', 'RoleBinding', 'ClusterRole', 'ClusterRoleBinding'].includes(resource.kind) &&
    resource.metadata.name === 'environment-platform-egress'), false);
  assert.equal(workload.some(resource =>
    resource.kind === 'CiliumNetworkPolicy' &&
    resource.metadata.name === 'agenthost-platform-egress'), false);

  const modelEgress = workload.find(resource =>
    resource.kind === 'CiliumNetworkPolicy' && resource.metadata.name === 'agenthost-model-egress');
  assert.deepEqual(modelEgress?.spec, {
    endpointSelector: { matchLabels: { 'agentweaver.io/workload': 'agenthost' } },
    egress: [{
      toEndpoints: [{
        matchLabels: {
          'k8s:io.kubernetes.pod.namespace': 'kube-system',
          'k8s:k8s-app': 'kube-dns',
        },
      }],
      toPorts: [{ ports: [{ port: '53', protocol: 'UDP' }, { port: '53', protocol: 'TCP' }] }],
    }],
  });
});

test('administrative guides gate installation and preserve existing RBAC during recovery', () => {
  const deploymentGuide = readFileSync(path.join(root, 'deploy', 'k8s', 'README.md'), 'utf8');
  const azureGuide = readFileSync(path.join(root, 'docs', 'guide', 'deploy-to-azure.md'), 'utf8');
  const procedure = deploymentGuide.slice(deploymentGuide.indexOf('### Install the named platform-egress Role'));

  assert.match(procedure, /reviewed target[\s\S]*confirms that Cilium is the selected platform egress provider/i);
  assert.match(procedure, /missing Cilium permission alone does not confirm/i);
  assert.match(procedure, /Stop if\s+either get fails/i);
  assert.ok(procedure.indexOf('kubectl get role environment-platform-egress') <
    procedure.indexOf('kubectl create -f -'));
  assert.ok(procedure.indexOf('kubectl get rolebinding environment-platform-egress') <
    procedure.indexOf('kubectl create -f -'));
  assert.match(procedure, /Compare the Role[\s\S]*Compare the RoleBinding/i);
  assert.match(procedure, /Stop if either object differs/i);
  assert.match(procedure, /Record each object's initial presence, UID/i);
  assert.match(procedure, /Skip matching pre-existing\s+objects/i);
  assert.match(procedure, /\$missingKinds = .*only object kinds recorded absent/i);
  assert.match(procedure, /\[regex\]::Split[\s\S]*\$missingKinds -contains \$kind[\s\S]*kubectl create -f -/i);
  assert.match(procedure, /only when `create` reports `created` and\s+readback confirms/i);
  assert.match(procedure, /If creation or\s+ownership is uncertain/i);
  assert.doesNotMatch(procedure, /kubectl\s+apply\b/i);
  assert.match(procedure, /Do not assume the grant\s+is absent/i);
  assert.match(procedure, /only when this installation created it/i);
  assert.match(procedure, /its UID matches the record/i);
  assert.match(procedure, /Preserve every pre-existing object and grant/i);
  assert.match(procedure, /If both objects existed before installation, delete neither/i);

  const narrowLink = 'deploy/k8s/README.md#install-the-named-platform-egress-role';
  assert.ok(azureGuide.includes(narrowLink));
  assert.match(azureGuide, /inspect, compare, create missing objects, and record both objects/i);
  assert.match(azureGuide, /reviewed target configuration confirms Cilium as the selected\s+platform egress provider/i);
  assert.match(azureGuide, /If the check cannot run or cannot[\s\S]*resolve the caller's authorization/i);
  assert.match(azureGuide, /missing Cilium permission alone does not confirm/i);
  assert.match(azureGuide, /Sandbox, BuildTest, or cluster-scoped permissions/i);

  const architecture = readFileSync(path.join(root, 'docs', 'architecture', 'environment-egress.md'), 'utf8');
  assert.match(architecture, /administrator-applied Role[\s\S]*get`, `update`, and `patch`/i);
  assert.match(architecture, /provider uses the named grant to read and replace/i);
  assert.match(architecture, /Note over C,K: Environment Role grants get, update, and patch/i);
});
