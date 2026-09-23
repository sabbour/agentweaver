import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { saveVerdict } from '../save-verdict.mjs';
import { VERDICT_SCHEMA } from '../verdict-schema.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SCRIPT = path.join(HERE, '..', 'save-verdict.mjs');

function metadata(overrides = {}) {
  return {
    batchId: 'batch-1',
    scenarioId: 'scenario-a',
    inputSeed: 'seed-1',
    adapterVersion: 'ui@1',
    personaCoreVersion: 'jordan@2',
    targetRevision: 'agentweaver@rev-a',
    surface: 'ui',
    runId: 'run-1',
    timestamp: '2026-09-23T19:00:00Z',
    persona: 'jordan',
    ...overrides,
  };
}

function verdict(join = metadata(), secret = 'alpha-value') {
  return {
    schema: VERDICT_SCHEMA,
    persona: join.persona,
    ...join,
    p0: { verdict: 'PASS', evidence: `credential=${secret}` },
    p1: { verdict: 'PASS', evidence: 'completed', criteriaCoverage: [] },
    frustration: { level: 'none', score: 0, signals: [], rationale: 'No frustration observed.' },
    pushback: { count: 0, requirementMet: true, each: [] },
    cannotDetermine: [],
    findings: [{ title: 'Credential evidence', kind: 'observation', evidence: `credential=${secret}` }],
  };
}

test('saveVerdict sanitizes returned and persisted custom-Judge verdicts', () => {
  const join = metadata();
  const raw = JSON.stringify(verdict(join));
  const result = saveVerdict(raw, join);
  assert.equal(result.ok, true);
  assert.equal(result.verdict.p0.evidence, 'credential=[REDACTED]');
  assert.equal(JSON.stringify(result).includes('alpha-value'), false);

  const directory = mkdtempSync(path.join(tmpdir(), 'agentweaver-save-verdict-'));
  try {
    const rawFile = path.join(directory, 'raw.txt');
    const evidenceFile = path.join(directory, 'evidence.json');
    const outFile = path.join(directory, 'verdict.json');
    writeFileSync(rawFile, raw, 'utf8');
    writeFileSync(evidenceFile, JSON.stringify({ metadata: join }), 'utf8');

    const saved = spawnSync(process.execPath, [
      SCRIPT, rawFile, '--evidence', evidenceFile, '--out', outFile,
    ], { encoding: 'utf8' });
    assert.equal(saved.status, 0, saved.stderr);
    const persisted = readFileSync(outFile, 'utf8');
    assert.equal(persisted.includes('alpha-value'), false);
    assert.equal(JSON.parse(persisted).findings[0].evidence, 'credential=[REDACTED]');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('saveVerdict sanitizes fallback schema-error payloads', () => {
  const join = metadata({ batchId: 'credential=alpha-value' });
  const result = saveVerdict(JSON.stringify(verdict({ ...join, batchId: 'wrong' })), join);
  assert.equal(result.ok, false);
  assert.equal(result.error.kind, 'schema_invalid');
  assert.equal(JSON.stringify(result).includes('alpha-value'), false);
  assert.equal(result.verdict.batchId, 'credential=[REDACTED]');
  assert.match(result.error.message, /credential=\[REDACTED\]/);
});
