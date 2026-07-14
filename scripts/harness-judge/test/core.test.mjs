import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { buildJudgePrompt, judgeEvidence } from '../core.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const fixtureJudge = path.join(__dirname, 'fixtures', 'mock-judge-cli.mjs');

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
});

test('judgeEvidence falls back to a schema-valid explicit non-verdict when the judge returns invalid JSON', async () => {
  const command = `"${process.execPath}" "${fixtureJudge}" invalid-json`;
  const result = await judgeEvidence(evidence(), { judgeCmd: command, retries: 0, timeoutMs: 5_000 });
  assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.p1.verdict, 'CANNOT_DETERMINE');
  assert.equal(result.verdict.frustration.level, 'not_assessed');
  assert.equal(result.verdict.frustration.score, null);
  assert.equal(result.verdict.batchId, 'batch-1');
  assert.equal(result.verdict.judgeError.kind, 'unparseable');
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
