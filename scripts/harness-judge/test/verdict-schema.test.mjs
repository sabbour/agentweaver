import { test } from 'node:test';
import assert from 'node:assert/strict';

import { VERDICT_SCHEMA, validateVerdict } from '../verdict-schema.mjs';

function validVerdict(overrides = {}) {
  return {
    schema: VERDICT_SCHEMA,
    persona: 'jordan',
    batchId: 'batch-1',
    scenarioId: 'jordan-blank-to-plan',
    inputSeed: 'seed-1',
    adapterVersion: 'ui@1',
    personaCoreVersion: 'jordan@2',
    targetRevision: 'agentweaver@abc123',
    surface: 'ui',
    runId: 'run-1',
    timestamp: '2026-07-14T19:00:00Z',
    p0: { verdict: 'PASS', evidence: 'all objective mechanics succeeded' },
    p1: { verdict: 'PARTIAL', evidence: 'one criterion missed', criteriaCoverage: [] },
    frustration: {
      level: 'moderate',
      score: 2,
      signals: [{ kind: 'loop', evidence: 'turn 4 repeated the same action' }],
      rationale: 'The persona needed several redundant attempts.',
    },
    pushback: { count: 2, requirementMet: true, each: [] },
    cannotDetermine: [],
    findings: [{ title: 'Repeated loop in review flow', kind: 'usability', evidence: 'turns 4-6' }],
    ...overrides,
  };
}

test('validateVerdict accepts a fully conforming cross-surface verdict', () => {
  const result = validateVerdict(validVerdict(), {
    expectedMetadata: {
      batchId: 'batch-1',
      scenarioId: 'jordan-blank-to-plan',
      inputSeed: 'seed-1',
      adapterVersion: 'ui@1',
      personaCoreVersion: 'jordan@2',
      targetRevision: 'agentweaver@abc123',
      surface: 'ui',
      runId: 'run-1',
      timestamp: '2026-07-14T19:00:00Z',
    },
  });
  assert.equal(result.ok, true);
});

test('validateVerdict rejects not_assessed frustration with a numeric score', () => {
  const result = validateVerdict(validVerdict({
    frustration: {
      level: 'not_assessed',
      score: 0,
      signals: [],
      rationale: 'no read',
    },
  }));
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /frustration\.score must be null/i);
});

test('validateVerdict rejects join-key mismatches against expected metadata', () => {
  const result = validateVerdict(validVerdict(), {
    expectedMetadata: {
      batchId: 'batch-2',
      scenarioId: 'jordan-blank-to-plan',
      inputSeed: 'seed-1',
      adapterVersion: 'ui@1',
      personaCoreVersion: 'jordan@2',
      targetRevision: 'agentweaver@abc123',
      surface: 'ui',
      runId: 'run-1',
      timestamp: '2026-07-14T19:00:00Z',
    },
  });
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /batchId must equal expected metadata value/i);
});
