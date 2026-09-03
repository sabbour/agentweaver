import assert from 'node:assert/strict';
import test from 'node:test';
import { buildCommands, runCombined, sanitizeCommand, sanitizeEnvironment } from '../launch.mjs';

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

test('combined remote API and MCP flows require explicit Agentweaver authentication', async () => {
  for (const [surface, target] of [
    ['api', 'https://example.test'],
    ['mcp', 'https://example.test/mcp'],
  ]) {
    await assert.rejects(
      runCombined({
        'scenario-id': 'case-1',
        surfaces: surface,
        [`${surface}-command`]: JSON.stringify(['node', 'runner.mjs', '--target', target]),
      }, {
        env: {},
        mkdir: async () => {},
        runCommand: async () => ({ code: 0 }),
        readVerdicts: () => [],
      }),
      /requires an explicit --token or AGENTWEAVER_TOKEN/,
    );
  }
});

test('runs all children independently and aggregates successful sibling verdicts after a failure', async () => {
  const calls = [];
  const writes = [];
  const report = await runCombined({
    'scenario-id': 'case-1', 'batch-id': 'batch-1', 'verdict-dir': 'test-verdicts',
    'api-command': command('api'), 'ui-command': command('ui'), 'mcp-command': command('mcp'),
  }, {
    env: { AGENTWEAVER_TOKEN: 'explicit-test-token' },
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

test('persisted process reports redact token-bearing arguments and environment values', () => {
  const jwt = 'eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEifQ.signaturevalue';
  assert.deepEqual(
    sanitizeCommand(['node', 'runner.mjs', '--token', jwt, `--authorization=Bearer ${jwt}`, 'AGENTWEAVER_TOKEN=opaque', '--scenario', 'safe']),
    ['node', 'runner.mjs', '--token', '[REDACTED]', '--authorization=[REDACTED]', 'AGENTWEAVER_TOKEN=[REDACTED]', '--scenario', 'safe'],
  );
  assert.deepEqual(sanitizeEnvironment({
    AGENTWEAVER_TOKEN: jwt,
    SAFE: `prefix Bearer ${jwt}`,
  }), {
    AGENTWEAVER_TOKEN: '[REDACTED]',
    SAFE: 'prefix Bearer [REDACTED]',
  });
});

test('combined report never persists extra child outcome fields', async () => {
  const writes = [];
  await runCombined({
    'scenario-id': 'case-1', 'batch-id': 'batch-1', 'verdict-dir': 'test-verdicts',
    surfaces: 'mcp', 'mcp-command': JSON.stringify(['node', 'runner.mjs', '--token', 'top-secret']),
  }, {
    mkdir: async () => {},
    writeFile: async (_file, content) => writes.push(content),
    runCommand: async () => ({ code: 0, signal: null, error: null, env: { TOKEN: 'leak' }, stdout: 'leak' }),
    readVerdicts: () => [{ batchId: 'batch-1', scenarioId: 'case-1', surface: 'mcp' }],
  });

  const report = JSON.parse(writes[0]);
  assert.equal(report.processes[0].command.at(-1), '[REDACTED]');
  assert.equal('env' in report.processes[0], false);
  assert.equal('stdout' in report.processes[0], false);
  assert.equal(report.preflight[0].surface, 'mcp');
  assert.equal(report.preflight[0].cleanupResult, 'delegated-to-surface');
});

test('combined report strips query credentials from targets, commands, and process errors', async () => {
  const canary = 'query-secret-canary';
  const writes = [];
  await runCombined({
    'scenario-id': 'case-1',
    'batch-id': 'batch-1',
    'verdict-dir': 'test-verdicts',
    surfaces: 'mcp',
    'mcp-command': JSON.stringify([
      'node', 'runner.mjs', '--target',
      `https://example.test/mcp?${canary}=${canary}#${canary}`,
    ]),
  }, {
    mkdir: async () => {},
    writeFile: async (_file, content) => writes.push(content),
    env: { AGENTWEAVER_TOKEN: 'explicit-test-token' },
    runCommand: async () => ({ code: 1, signal: null, error: `failed at https://example.test/mcp?${canary}=${canary}` }),
    readVerdicts: () => [],
  });
  assert.doesNotMatch(writes[0], new RegExp(canary));
});
