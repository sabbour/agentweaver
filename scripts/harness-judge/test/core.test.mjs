import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { buildJudgePrompt, judgeEvidence } from '../core.mjs';
import { VERDICT_SCHEMA, validateVerdict } from '../verdict-schema.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const fixtureJudge = path.join(__dirname, 'fixtures', 'mock-judge-cli.mjs');
const contract = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'verdict-contract.json'), 'utf8'));

function evidence() {
  return {
    metadata: {
      batchId: 'batch-1',
      scenarioId: 'scenario-a',
      inputSeed: 'seed-1',
      adapterVersion: 'ui@1',
      personaCoreVersion: 'jordan@2',
      targetRevision: 'agentweaver@rev-a',
      surface: 'ui',
      runId: 'run-1',
      timestamp: '2026-07-14T19:00:00Z',
      persona: 'jordan',
    },
    persona: {
      name: 'jordan',
      briefText: '# Jordan',
      authoredCriteriaText: '# Success looks like',
      surfaceAdapterText: '# UI adapter',
    },
    turns: [
      {
        id: 1,
        intent: 'inspect the draft',
        action: 'open review step',
        objectiveFacts: { url: '/review' },
        evidence: [{ kind: 'dom', evidence: '<main>review</main>' }],
        frustrationSignals: [],
      },
    ],
  };
}

test('buildJudgePrompt includes the canonical join-key tuple and frustration contract', () => {
  const prompt = buildJudgePrompt(evidence(), { judgeMd: '# JUDGE', surfaceAppendix: '# UI appendix' });
  assert.match(prompt, /batch-1/);
  assert.match(prompt, /scenario-a/);
  assert.match(prompt, /none \| mild \| moderate \| severe \| abandoned \| not_assessed/);
  assert.match(prompt, /p0\.evidence.*p1\.evidence.*one non-empty string/i);
  assert.match(prompt, /criteriaCoverage.*JSON array/i);
  assert.match(prompt, /frustration\.signals.*JSON array of objects/i);
});

for (const [name, fields] of Object.entries(contract.valid)) {
  test(`judgeEvidence accepts representative ${name} verdict on the first attempt`, async () => {
    const candidate = {
      schema: VERDICT_SCHEMA,
      persona: 'jordan',
      ...evidence().metadata,
      ...fields,
      pushback: { count: 0, requirementMet: true, each: [] },
      findings: [],
    };
    const result = await judgeEvidence(evidence(), {
      judge: async () => candidate,
      retries: 1,
      retryDelayMs: 0,
    });

    assert.equal(result.attempts, 1);
    assert.deepStrictEqual(result.verdict, candidate);
    assert.equal(validateVerdict(result.verdict, { expectedMetadata: evidence().metadata }).ok, true);
  });
}

for (const [name, fields] of Object.entries(contract.invalid)) {
  test(`judgeEvidence keeps invalid ${name} output diagnostic-only`, async () => {
    const candidate = {
      schema: VERDICT_SCHEMA,
      persona: 'jordan',
      ...evidence().metadata,
      p0: { verdict: 'PASS', evidence: 'Objective mechanics succeeded.' },
      p1: { verdict: 'PASS', evidence: 'The goal was met.', criteriaCoverage: [] },
      frustration: { level: 'none', score: 0, signals: [], rationale: 'No friction was observed.' },
      pushback: { count: 0, requirementMet: true, each: [] },
      cannotDetermine: [],
      findings: [],
      ...fields,
    };
    const result = await judgeEvidence(evidence(), {
      judge: async () => candidate,
      retries: 0,
    });

    assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
    assert.equal(result.verdict.p1.verdict, 'CANNOT_DETERMINE');
    assert.equal(result.verdict.judgeError.kind, 'schema_invalid');
    assert.equal(validateVerdict(result.verdict, { expectedMetadata: evidence().metadata }).ok, true);
    assert.deepStrictEqual(result.rawVerdict, candidate);
    assert.notEqual(result.verdict, candidate);
  });
}

test('judgeEvidence falls back to a schema-valid explicit non-verdict when the judge returns invalid JSON', async () => {
  const command = `"${process.execPath}" "${fixtureJudge}" invalid-json`;
  const result = await judgeEvidence(evidence(), { judgeCmd: command, retries: 0, timeoutMs: 5_000 });
  assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.p1.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.frustration.level, 'not_assessed');
  assert.equal(result.verdict.frustration.score, null);
  assert.equal(result.verdict.batchId, 'batch-1');
  assert.equal(result.verdict.judgeError.kind, 'unparseable');
  assert.equal(result.rawVerdict.rawText, 'not valid json');
});

test('judgeEvidence falls back when the judge command times out', async () => {
  const command = `"${process.execPath}" "${fixtureJudge}" timeout`;
  const result = await judgeEvidence(evidence(), { judgeCmd: command, retries: 0, timeoutMs: 25 });
  assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.judgeError.kind, 'timeout');
});

test('judgeEvidence falls back when the judge command exits unsuccessfully', async () => {
  const command = `"${process.execPath}" "${fixtureJudge}" nonzero`;
  const result = await judgeEvidence(evidence(), { judgeCmd: command, retries: 0, timeoutMs: 5_000 });
  assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.judgeError.kind, 'nonzero_exit');
  assert.equal(result.verdict.judgeError.exitCode, 7);
});

test('judgeEvidence redacts descriptor credentials before callbacks and prompts', async () => {
  const canary = 'credential-canary-judge-42';
  const input = evidence();
  input.turns[0].evidence.push({
    kind: 'execution-context',
    evidence: { name: 'provider key', value: canary },
  });
  let callbackEvidence;
  const result = await judgeEvidence(input, {
    retries: 0,
    judge: async ({ evidence: value }) => {
      callbackEvidence = value;
      return { ok: false, error: { kind: 'test', message: 'fixture stop' } };
    },
  });
  assert.equal(JSON.stringify(callbackEvidence).includes(canary), false);
  assert.equal(result.prompt.includes(canary), false);
  assert.equal(JSON.stringify(result.verdict).includes(canary), false);
});
