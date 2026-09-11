import assert from 'node:assert/strict';
import test from 'node:test';
import {
  loadOrchestrationLayoutContract,
  toDiagramLayoutContract,
} from './orchestration-layout-contract.mjs';
import { toWorkflowGraphSpec } from './workflows-to-graphspec.mjs';

test('generated workflow diagram specs embed the canonical compatible layout contract', async () => {
  const contract = await loadOrchestrationLayoutContract();
  const layout = toDiagramLayoutContract(contract);
  const spec = toWorkflowGraphSpec({
    id: 'test-workflow',
    name: 'Test workflow',
    nodes: [
      { id: 'start', type: 'prompt', label: 'Start' },
      { id: 'finish', type: 'terminal', label: 'Finish' },
    ],
    edges: [{ from: 'start', to: 'finish' }],
  }, layout);

  assert.deepEqual(spec.layout, {
    contract: 'agentweaver.orchestration-layout.v1',
    schemaVersion: 1,
    ranking: 'stable-longest-path',
    cycleHandling: 'exclude-back-edges',
    edgeRouting: 'orthogonal',
    serpentineMinRanks: 3,
  });
  assert.equal(spec.direction, 'TB');
});
