import assert from 'node:assert/strict';
import test from 'node:test';
import { toSpec } from './workflows-to-graphspec.mjs';

test('workflow graph specs mark revision edges as semantic loopbacks', () => {
  const spec = toSpec({
    id: 'test-workflow',
    name: 'Test workflow',
    nodes: [
      { id: 'implement', type: 'prompt', label: 'Implement' },
      { id: 'review', type: 'check', label: 'Review' },
    ],
    edges: [
      { from: 'implement', to: 'review', when: 'pass' },
      { from: 'review', to: 'implement', when: 'revise' },
    ],
  });

  assert.deepEqual(spec.edges, [
    { from: 'implement', to: 'review', label: 'pass' },
    { from: 'review', to: 'implement', label: 'revise', loopback: true },
  ]);
});
