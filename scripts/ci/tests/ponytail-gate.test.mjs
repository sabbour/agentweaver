import assert from 'node:assert/strict';
import test from 'node:test';
import { validatePonytailGate } from '../ponytail-gate.mjs';

const SHA = 'a'.repeat(40);

function gate(overrides = {}) {
  return {
    kind: 'agentweaver.ponytail-review/v1',
    head_sha: SHA,
    implementer: 'Morpheus',
    reviewer: 'Smith',
    implementer_validation: {
      commands: ['npm run validate:layer'],
    },
    rubber_duck: {
      assumptions: ['The validator runs against the branch tip under review.'],
      simplifications: ['Reused the existing Node test runner.'],
      flaw: 'Fixed missing exact-tip binding.',
    },
    findings: [],
    ...overrides,
  };
}

test('accepts an independent passing review bound to the exact tip', () => {
  assert.deepEqual(validatePonytailGate(gate(), SHA), {
    admitted: true,
    headSha: SHA,
    unwaivedHighConfidence: [],
  });
});

test('rejects a self-review', () => {
  assert.throws(
    () => validatePonytailGate(gate({ reviewer: 'morpheus' }), SHA),
    /independent/u,
  );
});

test('blocks an unwaived high-confidence finding', () => {
  assert.deepEqual(validatePonytailGate(gate({
    findings: [{
      id: 'PT-001',
      confidence: 'high',
      summary: 'Unnecessary abstraction',
      location: 'src/example.ts:12',
    }],
  }), SHA), {
    admitted: false,
    headSha: SHA,
    unwaivedHighConfidence: ['PT-001'],
  });
});

test('accepts an explicit auditable waiver', () => {
  const result = validatePonytailGate(gate({
    findings: [{
      id: 'PT-001',
      confidence: 'high',
      summary: 'Extra compatibility branch',
      location: 'src/example.ts:12',
      waiver: {
        justification: 'Required for the supported legacy API.',
        approved_by: 'sabbour',
      },
    }],
  }), SHA);

  assert.equal(result.admitted, true);
});

test('rejects evidence for a different branch tip', () => {
  assert.throws(
    () => validatePonytailGate(gate(), 'b'.repeat(40)),
    /does not match expected tip/u,
  );
});
