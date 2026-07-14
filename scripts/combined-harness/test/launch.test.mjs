import assert from 'node:assert/strict';
import test from 'node:test';
import { buildCommands, runCombined } from '../launch.mjs';

const command = (surface) => JSON.stringify(['node', `${surface}-runner.mjs`, '--batch', '{batchId}', '--scenario', '{scenarioId}', '--out', '{verdictDir}/{scenarioId}.json']);

test('buildCommands replaces shared batch and scenario tokens', () => {
  const commands = buildCommands({
    'api-command': command('api'), 'ui-command': command('ui'), 'mcp-command': command('mcp'),
  }, { batchId: 'batch-1', scenarioId: 'case-1', verdictDir: 'verdicts' });
  assert.deepEqual(commands.map(({ command: argv }) => argv), [
    ['node', 'api-runner.mjs', '--batch', 'batch-1', '--scenario', 'case-1', '--out', 'verdicts/case-1.json'],
    ['node', 'ui-runner.mjs', '--batch', 'batch-1', '--scenario', 'case-1', '--out', 'verdicts/case-1.json'],
    ['node', 'mcp-runner.mjs', '--batch', 'batch-1', '--scenario', 'case-1', '--out', 'verdicts/case-1.json'],
  ]);
});

test('runs all children independently and aggregates successful sibling verdicts after a failure', async () => {
  const calls = [];
  const writes = [];
  const report = await runCombined({
    'scenario-id': 'case-1', 'batch-id': 'batch-1', 'verdict-dir': 'test-verdicts',
    'api-command': command('api'), 'ui-command': command('ui'), 'mcp-command': command('mcp'),
  }, {
    mkdir: async () => {},
    writeFile: async (file, content) => writes.push({ file, content }),
    runCommand: async (argv, options) => {
      calls.push({ argv, options });
      return argv[1] === 'ui-runner.mjs' ? { code: 2, signal: null, error: null } : { code: 0, signal: null, error: null };
    },
    readVerdicts: () => [
      { batchId: 'batch-1', scenarioId: 'case-1', surface: 'api' },
      { batchId: 'batch-1', scenarioId: 'case-1', surface: 'mcp' },
    ],
  });
  assert.equal(calls.length, 4);
  assert.deepEqual(calls.slice(0, 3).map(({ argv }) => argv[1]), ['api-runner.mjs', 'ui-runner.mjs', 'mcp-runner.mjs']);
  assert.deepEqual(calls[3].argv.slice(0, 2), ['node', 'scripts/harness-judge/meta-aggregate.mjs']);
  assert.equal(calls[0].options.env.AGENTWEAVER_BATCH_ID, 'batch-1');
  assert.equal(calls[0].options.env.AGENTWEAVER_SCENARIO_ID, 'case-1');
  assert.deepEqual(report.missingSurfaces, ['ui']);
  assert.equal(report.aggregation.code, 0);
  assert.equal(writes.length, 1);
});
