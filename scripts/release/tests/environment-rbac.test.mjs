import assert from 'node:assert/strict';
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
  assert.equal(resources.some(resource =>
    ['Role', 'RoleBinding', 'ClusterRole', 'ClusterRoleBinding'].includes(resource.kind)), true);
});
