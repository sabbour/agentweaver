import assert from 'node:assert/strict';
import test from 'node:test';
import { finishSmokeLifecycle, runSmoke } from '../smoke/mcp-cli-smoke.mjs';

const tool = (name, required = []) => ({
  name,
  inputSchema: {
    type: 'object',
    properties: Object.fromEntries(required.map((key) => [key, { type: 'string' }])),
    required,
  },
});

const contract = {
  contractVersion: 'test',
  capabilities: [
    { capability: 'repo-app-connect', tools: ['github_repo_app_connect'] },
    { capability: 'list-projects', tools: ['project_list'] },
    { capability: 'create-project', tools: ['project_create'], in: { requires: { name: 'string', working_directory: 'string' } } },
    { capability: 'delete-project', tools: ['project_delete'], in: { requires: { project_id: 'string' } } },
    { capability: 'submit-run', tools: ['run_submit'], in: { requires: { project_id: 'string', task: 'string' } } },
    { capability: 'poll-run', tools: ['run_status'], in: { requires: { run_id: 'string' } } },
    { capability: 'confirm-outcome-spec', tools: ['coordinator_outcome_spec_confirm'], in: { requires: { run_id: 'string' } } },
    { capability: 'list-artifacts', tools: ['run_show_artifacts'], in: { requires: { run_id: 'string' } } },
    { capability: 'cleanup-run', tools: ['run_archive'], in: { requires: { run_id: 'string' } } },
  ],
};

function fakeClient(responses) {
  const calls = [];
  return {
    calls,
    async discoverTools() {
      return [
        tool('github_repo_app_connect'), tool('project_list'),
        tool('project_create', ['name', 'working_directory']),
        tool('project_delete', ['project_id']),
        tool('run_submit', ['project_id', 'task']), tool('run_status', ['run_id']),
        tool('coordinator_outcome_spec_confirm', ['run_id']),
        tool('run_show_artifacts', ['run_id']), tool('run_archive', ['run_id']),
      ];
    },
    async callTool(name, arguments_) {
      calls.push({ name, arguments: arguments_ });
      const response = responses[name];
      const value = name === 'run_status' && Array.isArray(response) ? response.shift() : response;
      if (value instanceof Error) return { isError: true, structuredContent: null, rawContent: value.message };
      return { isError: false, structuredContent: value, rawContent: JSON.stringify(value) };
    },
  };
}

test('runs project creation, submit, poll, artifacts, and cleanup end to end', async () => {
  const client = fakeClient({
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1', status: 'queued' },
    run_status: [
      { status: 'running', coordinator_status: 'awaiting_confirmation' },
      { status: 'completed' },
    ],
    coordinator_outcome_spec_confirm: { status: 'running' },
    run_show_artifacts: { artifacts: [{ path: 'smoke.txt' }] },
    run_archive: { status: 'archived' },
    project_delete: { status: 'deleted' },
  });

  const result = await runSmoke({
    client,
    contract,
    projectName: 'smoke',
    workingDirectory: '.',
    uniqueId: () => 'unique',
    sleepFn: async () => {},
  });

  assert.equal(result.banner, 'CLI→MCP SMOKE OK');
  assert.equal(result.project.source, 'created');
  assert.equal(result.artifactCount, 1);
  assert.deepEqual(client.calls.map((call) => call.name), [
    'project_create',
    'run_submit',
    'run_status',
    'coordinator_outcome_spec_confirm',
    'run_status',
    'run_show_artifacts',
    'run_archive',
    'project_delete',
  ]);
  assert.deepEqual(client.calls[0].arguments, {
    name: 'smoke-unique',
    working_directory: '.',
    origin: 'blank',
    blueprint_id: 'blueprint-software-development',
  });
  assert.equal(result.preflight.cleanupResult, 'completed');
  assert.equal(result.preflight.projectId, 'project-1');
  assert.equal(result.preflight.runId, 'run-1');
});

test('local project creation defaults to the current workspace and blank origin', async () => {
  const client = fakeClient({
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1' },
    run_status: { status: 'completed' },
    run_show_artifacts: { artifacts: [{}] },
    run_archive: {},
    project_delete: {},
  });
  await runSmoke({ client, contract, sleepFn: async () => {}, uniqueId: () => 'default' });
  assert.deepEqual(client.calls[0].arguments, {
    name: 'agentweaver-mcp-smoke-default',
    working_directory: '.',
    origin: 'blank',
    blueprint_id: 'blueprint-software-development',
  });
});

test('remote project creation requires an explicit deployed-workspace directory', async () => {
  const client = fakeClient({});
  await assert.rejects(
    runSmoke({
      client,
      contract,
      preflight: { transport: 'http' },
      uniqueId: () => 'remote',
    }),
    /AGENTWEAVER_SMOKE_WORKING_DIRECTORY/,
  );
  assert.equal(client.calls.length, 0);
});

test('remote project creation rejects local Windows working directories', async () => {
  for (const workingDirectory of ['C:\\repo\\smoke', '\\\\server\\share', '.']) {
    const client = fakeClient({});
    await assert.rejects(
      runSmoke({
        client,
        contract,
        preflight: { transport: 'http' },
        workingDirectory,
        uniqueId: () => 'remote',
      }),
      /absolute provider path|Windows paths/,
    );
    assert.equal(client.calls.length, 0);
  }
});

test('provided project must be explicitly disposable and is archived but never deleted', async () => {
  const client = fakeClient({
    run_submit: { run_id: 'run-1' },
    run_status: { status: 'completed' },
    run_show_artifacts: { artifacts: [{ path: 'smoke.txt' }] },
    run_archive: { status: 'archived' },
  });

  await assert.rejects(
    runSmoke({ client, contract, projectId: 'project-1', sleepFn: async () => {} }),
    /requires --project-is-disposable/,
  );
  assert.equal(client.calls.length, 0);

  await runSmoke({
    client, contract, projectId: 'project-1', projectIsDisposable: true, sleepFn: async () => {},
  });
  assert.equal(client.calls.some((call) => call.name === 'project_create'), false);
  assert.equal(client.calls.some((call) => call.name === 'project_delete'), false);
  assert.equal(client.calls.at(-1).name, 'run_archive');
});

for (const [stage, responses, expected] of [
  ['submission', { project_create: { id: 'project-1' }, run_submit: new Error('submit exploded'), project_delete: {} }, /run submission failed/],
  ['polling', { project_create: { id: 'project-1' }, run_submit: { run_id: 'run-1' }, run_status: new Error('poll exploded'), run_archive: {}, project_delete: {} }, /run polling failed/],
  ['terminal failure', { project_create: { id: 'project-1' }, run_submit: { run_id: 'run-1' }, run_status: { status: 'failed' }, run_archive: {}, project_delete: {} }, /run completion assertion failed/],
  ['artifact retrieval', { project_create: { id: 'project-1' }, run_submit: { run_id: 'run-1' }, run_status: { status: 'completed' }, run_show_artifacts: new Error('artifact exploded'), run_archive: {}, project_delete: {} }, /artifact retrieval failed/],
]) {
  test(`owned smoke project is deleted after ${stage} failure`, async () => {
    const client = fakeClient(responses);
    await assert.rejects(
      runSmoke({ client, contract, sleepFn: async () => {}, uniqueId: () => 'failure' }),
      expected,
    );
    assert.equal(client.calls.at(-1).name, 'project_delete');
    if (client.calls.some((call) => call.name === 'run_submit' && responses.run_submit?.run_id)) {
      assert.ok(client.calls.some((call) => call.name === 'run_archive'));
    }
  });
}

test('primary failure is preserved while archive and delete cleanup failures are reported separately', async () => {
  const client = fakeClient({
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1' },
    run_status: { status: 'failed' },
    run_archive: new Error('archive cleanup exploded'),
    project_delete: new Error('delete cleanup exploded'),
  });
  await assert.rejects(
    runSmoke({ client, contract, sleepFn: async () => {}, uniqueId: () => 'failure', logger: { error() {} } }),
    (error) => {
      assert.match(error.message, /run completion assertion failed/);
      assert.deepEqual(error.cleanupErrors, [
        'run cleanup failed (run_archive): archive cleanup exploded',
        'project cleanup failed (project_delete): delete cleanup exploded',
      ]);
      return true;
    },
  );
  assert.deepEqual(client.calls.slice(-2).map((call) => call.name), ['run_archive', 'project_delete']);
});

test('fails immediately with the coordinator assembly state and reason', async () => {
  const client = fakeClient({
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1' },
    run_status: { status: 'in_progress', coordinator_status: 'assembly_blocked', coordinator_status_reason: 'capability unavailable' },
    run_archive: {},
    project_delete: {},
  });
  await assert.rejects(
    runSmoke({ client, contract, sleepFn: async () => {}, uniqueId: () => 'blocked' }),
    /coordinator entered assembly_blocked: capability unavailable/,
  );
  assert.deepEqual(client.calls.slice(-2).map((call) => call.name), ['run_archive', 'project_delete']);
});

test('cancellation still archives the run and deletes the owned project', async () => {
  const client = fakeClient({
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1' },
    run_archive: {},
    project_delete: {},
  });

  test('a failed project creation cannot guess an id to delete', async () => {
    const client = fakeClient({ project_create: new Error('creation failed') });
    await assert.rejects(
      runSmoke({ client, contract, uniqueId: () => 'create-failure' }),
      /project creation failed/,
    );
    assert.deepEqual(client.calls.map((call) => call.name), ['project_create']);
  });
  await assert.rejects(
    runSmoke({ client, contract, isCancelled: () => true, uniqueId: () => 'cancelled' }),
    /smoke cancelled/,
  );
  assert.deepEqual(client.calls.slice(-2).map((call) => call.name), ['run_archive', 'project_delete']);
});

test('preflight write failure preserves the primary error and still closes the client', async () => {
  const primary = new Error('primary smoke failure');
  let closed = false;
  await assert.rejects(
    finishSmokeLifecycle({
      primaryError: primary,
      client: { close: async () => { closed = true; } },
      preflight: {},
      preflightOut: 'unwritable/preflight.json',
      mkdirImpl: async () => {},
      writeFileImpl: async () => { throw new Error('read-only artifact directory'); },
    }),
    (error) => {
      assert.equal(error, primary);
      assert.deepEqual(error.finalizationErrors, [
        'preflight evidence write failed: read-only artifact directory',
      ]);
      return true;
    },
  );
  assert.equal(closed, true);
});

test('preflight and close failures are surfaced deterministically without losing either', async () => {
  await assert.rejects(
    finishSmokeLifecycle({
      primaryError: null,
      client: { close: async () => { throw new Error('close exploded'); } },
      preflight: {},
      preflightOut: 'unwritable/preflight.json',
      mkdirImpl: async () => {},
      writeFileImpl: async () => { throw new Error('write exploded'); },
    }),
    (error) => {
      assert.ok(error instanceof AggregateError);
      assert.deepEqual(error.errors.map((item) => item.message), [
        'preflight evidence write failed: write exploded',
        'MCP client close failed: close exploded',
      ]);
      return true;
    },
  );
});
