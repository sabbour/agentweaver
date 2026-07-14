import { test } from 'node:test';
import assert from 'node:assert/strict';

import { aggregate, renderRollup } from '../meta-aggregate.mjs';
import { VERDICT_SCHEMA } from '../verdict-schema.mjs';

function verdict({
  batchId,
  scenarioId,
  surface,
  runId,
  p0 = 'PASS',
  p1 = 'PASS',
  frustrationLevel = 'none',
  finding = null,
  targetRevision = 'agentweaver@rev-a',
}) {
  const scoreMap = { none: 0, mild: 1, moderate: 2, severe: 3, abandoned: 4, not_assessed: null };
  return {
    schema: VERDICT_SCHEMA,
    persona: 'jordan',
    batchId,
    scenarioId,
    inputSeed: 'seed-1',
    adapterVersion: `${surface}@1`,
    personaCoreVersion: 'jordan@2',
    targetRevision,
    surface,
    runId,
    timestamp: '2026-07-14T19:00:00Z',
    p0: { verdict: p0, evidence: 'p0 evidence' },
    p1: { verdict: p1, evidence: 'p1 evidence', criteriaCoverage: [] },
    frustration: {
      level: frustrationLevel,
      score: scoreMap[frustrationLevel],
      signals: frustrationLevel === 'not_assessed' ? [] : [{ kind: 'signal', evidence: 'turn 2' }],
      rationale: 'rationale',
    },
    pushback: { count: 2, requirementMet: true, each: [] },
    cannotDetermine: [],
    findings: finding ? [finding] : [],
  };
}

test('aggregate groups verdicts strictly by batchId + scenarioId and correlates surfaces within each tuple', () => {
  const result = aggregate([
    verdict({
      batchId: 'batch-1',
      scenarioId: 'scenario-a',
      surface: 'api',
      runId: 'api-a',
      p0: 'PASS',
      p1: 'PASS',
      frustrationLevel: 'none',
      finding: { title: 'Backend clean baseline', kind: 'P0', evidence: 'api-a' },
    }),
    verdict({
      batchId: 'batch-1',
      scenarioId: 'scenario-a',
      surface: 'ui',
      runId: 'ui-a',
      p0: 'PASS',
      p1: 'PASS',
      frustrationLevel: 'severe',
      finding: { title: 'Review screen caused confusion', kind: 'usability', evidence: 'ui-a' },
    }),
    verdict({
      batchId: 'batch-1',
      scenarioId: 'scenario-a',
      surface: 'mcp',
      runId: 'mcp-a',
      p0: 'PASS',
      p1: 'PARTIAL',
      frustrationLevel: 'not_assessed',
      finding: { title: 'Review screen caused confusion', kind: 'usability', evidence: 'mcp-a' },
    }),
    verdict({
      batchId: 'batch-1',
      scenarioId: 'scenario-b',
      surface: 'api',
      runId: 'api-b',
      p0: 'FAIL',
      p1: 'PARTIAL',
      frustrationLevel: 'moderate',
      finding: { title: 'Coordinator returned 500', kind: 'P0', evidence: 'api-b' },
    }),
    verdict({
      batchId: 'batch-1',
      scenarioId: 'scenario-b',
      surface: 'ui',
      runId: 'ui-b',
      p0: 'PASS',
      p1: 'PARTIAL',
      frustrationLevel: 'severe',
      finding: { title: 'Coordinator returned 500', kind: 'P0', evidence: 'ui-b' },
    }),
  ]);

  assert.equal(result.groupCount, 2);

  const scenarioA = result.groups.find((group) => group.scenarioId === 'scenario-a');
  const scenarioB = result.groups.find((group) => group.scenarioId === 'scenario-b');
  assert.ok(scenarioA);
  assert.ok(scenarioB);

  assert.equal(scenarioA.surfaces.api.frustration.averageScore, 0);
  assert.equal(scenarioA.surfaces.mcp.frustration.averageScore, null);
  assert.ok(scenarioA.correlations.some((item) => item.kind === 'pure_ux_issue'));
  assert.ok(scenarioA.correlations.some((item) => item.kind === 'cross_surface_p1_divergence'));
  assert.equal(scenarioA.recurringFindings.length, 1);
  assert.deepEqual(scenarioA.recurringFindings[0].surfaces.sort(), ['mcp', 'ui']);

  assert.ok(scenarioB.correlations.some((item) => item.kind === 'backend_root_cause'));
  assert.equal(scenarioB.verdictCount, 2);
});

test('renderRollup prints one section per batch/scenario group', () => {
  const result = aggregate([
    verdict({ batchId: 'batch-2', scenarioId: 'scenario-z', surface: 'api', runId: 'api-z' }),
    verdict({ batchId: 'batch-2', scenarioId: 'scenario-z', surface: 'ui', runId: 'ui-z', frustrationLevel: 'mild' }),
  ]);
  const text = renderRollup(result);
  assert.match(text, /Cross-surface groups: 1/);
  assert.match(text, /BATCH batch-2 \/ SCENARIO scenario-z/);
  assert.match(text, /pure_ux_issue/);
});
