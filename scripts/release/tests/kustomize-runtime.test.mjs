import assert from 'node:assert/strict';
import test from 'node:test';
import {
  createKustomizeImageEntries, parseKustomizeResources, validateKustomizeImagePins,
} from '../kustomize-runtime.mjs';

const digest = index => `sha256:${String(index).padStart(64, '0')}`;
const components = [
  'Agentweaver.AgentHost',
  'Agentweaver.Identity.Broker',
  'Agentweaver.EventsAndSessions',
  'Agentweaver.Gateway',
  'Agentweaver.Mcp',
  'Agentweaver.Web',
  'Agentweaver.Orchestrator',
  'Agentweaver.Environment',
  'Agentweaver.Knowledge',
  'Agentweaver.Projects.Config',
];

function publicationImages(ids = components) {
  return ids.map((id, index) => id === 'Agentweaver.AgentHost'
    ? { id, sandboxImageDigest: digest(index + 1) }
    : { id, image: `ghcr.io/sabbour/${id.toLowerCase()}@${digest(index + 1)}` });
}

test('parses rendered resources into a Kubernetes List and ignores empty documents', () => {
  // The blank YAML document between the two resources must be skipped.
  const resources = parseKustomizeResources([
    'apiVersion: v1',
    'kind: ConfigMap',
    'metadata:',
    '  name: settings',
    '---',
    '',
    '---',
    'apiVersion: v1',
    'kind: Namespace',
    'metadata:',
    '  name: agentweaver',
  ].join('\n'));

  assert.equal(resources.apiVersion, 'v1');
  assert.equal(resources.kind, 'List');
  assert.deepEqual(resources.items.map(({ kind, metadata }) => [kind, metadata.name]), [
    ['ConfigMap', 'settings'],
    ['Namespace', 'agentweaver'],
  ]);
});

test('rejects empty and malformed Kustomize output', () => {
  // Whitespace and YAML null both produce no resources; malformed YAML is a separate failure.
  assert.throws(() => parseKustomizeResources('  '), /Kustomize rendered no Kubernetes resources/);
  assert.throws(() => parseKustomizeResources('---\n...\n'),
    /Kustomize rendered no Kubernetes resources/);
  assert.throws(() => parseKustomizeResources('kind: [invalid'),
    /Kustomize output contains invalid YAML/);
});

test('creates exact digest entries from the publication receipt, including AgentHost sandbox digest', () => {
  // AgentHost uses its sandbox digest; other services carry an image reference with a digest.
  const images = publicationImages();
  const entries = createKustomizeImageEntries(images);

  assert.equal(entries.length, 10);
  assert.deepEqual(entries[0], {
    name: 'ghcr.io/sabbour/agentweaver-agenthost',
    newName: 'ghcr.io/sabbour/agentweaver-agenthost',
    digest: digest(1),
  });
  assert.equal(entries[5].digest, digest(6));
  assert.throws(() => createKustomizeImageEntries(images.slice(1)),
    /exact ten published service images/);
  assert.throws(() => createKustomizeImageEntries(publicationImages().map((image, index) =>
    index === 0 ? { ...image, sandboxImageDigest: 'latest' } : image)),
  /no exact image digest/);
  assert.throws(() => createKustomizeImageEntries(publicationImages([
    'same', 'SAME', ...components.slice(2),
  ])), /duplicate Kustomize image repositories/);
});

test('validates every Kustomize image transform against the publication receipt', () => {
  // Change one pin at a time to check duplicate names, wrong digests, and tags independently.
  const images = publicationImages();
  const entries = createKustomizeImageEntries(images);

  assert.deepEqual(validateKustomizeImagePins({ images: entries }, images), entries);
  assert.throws(() => validateKustomizeImagePins({}, images),
    /must pin exactly the ten image repositories/);
  assert.throws(() => validateKustomizeImagePins({
    images: entries.map((entry, index) => index === 1 ? { ...entry, name: entries[0].name } : entry),
  }, images), /invalid or duplicate image transform/);
  assert.throws(() => validateKustomizeImagePins({
    images: entries.map((entry, index) => index === 1 ? { ...entry, digest: digest(99) } : entry),
  }, images), /must match the publication receipt exactly/);
  assert.throws(() => validateKustomizeImagePins({
    images: entries.map((entry, index) => index === 1 ? { ...entry, newTag: 'latest' } : entry),
  }, images), /must match the publication receipt exactly/);
});
