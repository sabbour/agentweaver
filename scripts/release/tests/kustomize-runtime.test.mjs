import assert from 'node:assert/strict';
import test from 'node:test';
import { parseKustomizeResources } from '../kustomize-runtime.mjs';

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
