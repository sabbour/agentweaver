import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { TEST_SHARDS } from '../dotnet-test-shards.mjs';
import {
  buildMarkdown,
  formatRatio,
  summarizeDotnet,
  summarizeJs,
} from '../coverage-summary.mjs';

function fixtureDirectory(t) {
  const directory = mkdtempSync(path.join(tmpdir(), 'agentweaver-coverage-summary-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

test('formatRatio reports a covered/total percentage and refuses a zero/invalid denominator', () => {
  assert.equal(formatRatio(5, 10), '5/10 (50.00%)');
  assert.equal(formatRatio(0, 0), 'n/a');
  assert.equal(formatRatio(undefined, 10), 'n/a');
  assert.equal(formatRatio(5, undefined), 'n/a');
});

test('summarizeDotnet marks a job that never ran as "not run" without fabricating numbers', () => {
  const section = summarizeDotnet(null, 'skipped');
  assert.equal(section.status, 'not run');
  assert.equal(section.complete, false);
  assert.deepEqual(section.rows, []);
});

test('summarizeDotnet reports missing status.json honestly when the job failed before producing one', (t) => {
  const dir = fixtureDirectory(t);
  const section = summarizeDotnet(dir, 'failure');
  assert.equal(section.complete, false);
  assert.match(section.notes.join(' '), /No status\.json was found/);
});

test('summarizeDotnet surfaces a partial run\'s missing shards and absent assemblies', (t) => {
  const dir = fixtureDirectory(t);
  writeFileSync(path.join(dir, 'status.json'), JSON.stringify({
    revision: 'abc123',
    scope: ['Agentweaver.Api'],
    complete: false,
    completed: [{ id: 'orchestration', tests: 12 }],
    missing: ['kata-runtime: unsupported (bubblewrap user namespace is required)'],
    absentAssemblies: ['Agentweaver.Squad'],
    combined: null,
  }));
  const section = summarizeDotnet(dir, 'failure');
  assert.equal(section.complete, false);
  assert.match(section.notes.join(' '), /Shards verified: 1\/\d+/);
  assert.match(section.notes.join(' '), /kata-runtime: unsupported/);
  assert.match(section.notes.join(' '), /Agentweaver\.Squad/);
  assert.match(section.notes.join(' '), new RegExp(`1/${TEST_SHARDS.length}`));
});

test('summarizeDotnet renders combined coverage totals for a complete run', (t) => {
  const dir = fixtureDirectory(t);
  mkdirSync(path.join(dir, 'combined'), { recursive: true });
  writeFileSync(path.join(dir, 'status.json'), JSON.stringify({
    revision: 'abc123',
    scope: ['Agentweaver.Api'],
    complete: true,
    completed: TEST_SHARDS.map((shard) => ({ id: shard.id, tests: 1 })),
    missing: [],
    absentAssemblies: [],
    combined: 'combined/Cobertura.xml',
  }));
  writeFileSync(path.join(dir, 'combined', 'Summary.json'), JSON.stringify({
    summary: {
      coveredlines: 10, coverablelines: 20,
      coveredbranches: 3, totalbranches: 6,
      coveredmethods: 2, totalmethods: 4,
    },
  }));
  const section = summarizeDotnet(dir, 'success');
  assert.equal(section.complete, true);
  assert.deepEqual(section.rows, [
    ['Lines', '10/20 (50.00%)'],
    ['Branches', '3/6 (50.00%)'],
    ['Methods', '2/4 (50.00%)'],
  ]);
});

test('summarizeDotnet fails closed on a malformed combined report even when status.json claims success', (t) => {
  const dir = fixtureDirectory(t);
  mkdirSync(path.join(dir, 'combined'), { recursive: true });
  writeFileSync(path.join(dir, 'status.json'), JSON.stringify({
    revision: 'abc123',
    complete: true,
    completed: TEST_SHARDS.map((shard) => ({ id: shard.id, tests: 1 })),
    missing: [],
    absentAssemblies: [],
    combined: 'combined/Cobertura.xml',
  }));
  // Malformed: covered exceeds total.
  writeFileSync(path.join(dir, 'combined', 'Summary.json'), JSON.stringify({
    summary: {
      coveredlines: 30, coverablelines: 20,
      coveredbranches: 3, totalbranches: 6,
      coveredmethods: 2, totalmethods: 4,
    },
  }));
  const section = summarizeDotnet(dir, 'success');
  assert.equal(section.complete, false, 'a malformed combined report must never render as complete');
  assert.match(section.notes.join(' '), /not trustworthy/);
});

test('summarizeDotnet fails closed on an incomplete shard set even when status.json claims success', (t) => {
  const dir = fixtureDirectory(t);
  mkdirSync(path.join(dir, 'combined'), { recursive: true });
  writeFileSync(path.join(dir, 'status.json'), JSON.stringify({
    revision: 'abc123',
    complete: true,
    completed: [{ id: 'orchestration', tests: 1 }],
    missing: [],
    absentAssemblies: [],
    combined: 'combined/Cobertura.xml',
  }));
  writeFileSync(path.join(dir, 'combined', 'Summary.json'), JSON.stringify({
    summary: {
      coveredlines: 10, coverablelines: 20,
      coveredbranches: 3, totalbranches: 6,
      coveredmethods: 2, totalmethods: 4,
    },
  }));
  const section = summarizeDotnet(dir, 'success');
  assert.equal(section.complete, false, 'fewer completed shards than the expected matrix must not render complete');
});

test('summarizeDotnet treats a cancelled job with a partial artifact as partial/failed, not "not run"', (t) => {
  const dir = fixtureDirectory(t);
  writeFileSync(path.join(dir, 'status.json'), JSON.stringify({
    revision: 'abc123',
    complete: false,
    completed: [{ id: 'orchestration', tests: 1 }],
    missing: ['cancelled before remaining shards ran'],
    absentAssemblies: [],
    combined: null,
  }));
  const section = summarizeDotnet(dir, 'cancelled');
  assert.notEqual(section.status, 'not run');
  assert.equal(section.complete, false);
  assert.match(section.notes.join(' '), /Completed shards: orchestration/);
});

test('summarizeJs reports a missing coverage-summary.json honestly', (t) => {
  const dir = fixtureDirectory(t);
  const section = summarizeJs('Web', dir, 'failure');
  assert.equal(section.complete, false);
  assert.match(section.notes.join(' '), /No coverage-summary\.json was found/);
});

test('summarizeJs only reports complete when the job itself succeeded', (t) => {
  const dir = fixtureDirectory(t);
  writeFileSync(path.join(dir, 'coverage-summary.json'), JSON.stringify({
    total: {
      lines: { covered: 7, total: 10 },
      branches: { covered: 2, total: 4 },
      functions: { covered: 1, total: 2 },
    },
    'src/file.ts': {},
  }));
  const failed = summarizeJs('Node', dir, 'failure');
  assert.equal(failed.complete, false, 'a failed job stays partial even with a report present');
  assert.deepEqual(failed.rows, [
    ['Lines', '7/10 (70.00%)'],
    ['Branches', '2/4 (50.00%)'],
    ['Functions', '1/2 (50.00%)'],
  ]);

  const succeeded = summarizeJs('Node', dir, 'success');
  assert.equal(succeeded.complete, true);
});

test('summarizeJs fails closed on a malformed summary even when the job succeeded', (t) => {
  const dir = fixtureDirectory(t);
  writeFileSync(path.join(dir, 'coverage-summary.json'), JSON.stringify({
    total: {
      // Malformed: covered exceeds total.
      lines: { covered: 11, total: 10 },
      branches: { covered: 2, total: 4 },
      functions: { covered: 1, total: 2 },
    },
  }));
  const section = summarizeJs('Web', dir, 'success');
  assert.equal(section.complete, false, 'a malformed summary must never render as complete');
  assert.match(section.notes.join(' '), /not trustworthy/);
});

test('buildMarkdown never applies a pass/fail threshold and flags any non-complete family', () => {
  const markdown = buildMarkdown({
    sha: 'deadbeef',
    dotnet: { label: '.NET', status: 'success', complete: true, rows: [['Lines', '10/10 (100.00%)']], notes: [] },
    web: { label: 'Web', status: 'failure', complete: false, rows: [], notes: ['No coverage-summary.json was found.'] },
    node: { label: 'Node', status: 'not run', complete: false, rows: [], notes: ['Job did not run.'] },
  });
  assert.match(markdown, /deadbeef/);
  assert.match(markdown, /At least one family is partial, failed, or did not run/);
  assert.doesNotMatch(markdown, /threshold of \d/);
  assert.match(markdown, /No pass\/fail threshold/);
});

test('buildMarkdown reports full completeness only when every attempted family is complete', () => {
  const complete = { label: 'X', status: 'success', complete: true, rows: [], notes: [] };
  const markdown = buildMarkdown({ sha: 'cafef00d', dotnet: complete, web: complete, node: complete });
  assert.match(markdown, /All attempted families are complete\./);
});
