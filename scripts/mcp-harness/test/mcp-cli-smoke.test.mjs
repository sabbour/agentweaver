import assert from 'node:assert/strict';
import test from 'node:test';
import { runSmoke } from '../smoke/mcp-cli-smoke.mjs';

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
        tool('run_submit', ['project_id', 'task']), tool('run_status', ['run_id']),
        tool('coordinator_outcome_spec_confirm', ['run_id']),
        tool('run_show_artifacts', ['run_id']), tool('run_archive', ['run_id']),
      ];
    },
    async callTool(name, arguments_) {
      calls.push({ name, arguments: arguments_ });
      const response = responses[name];
      const value = name === 'run_status' && Array.isArray(response) ? response.shift() : response;
      return { isError: false, structuredContent: value, rawContent: JSON.stringify(value) };
    },
  };
}

test('runs project creation, submit, poll, artifacts, and cleanup end to end', async () => {
  const client = fakeClient({
    project_list: [],
    project_create: { id: 'project-1' },
    run_submit: { run_id: 'run-1', status: 'queued' },
    run_status: [
      { status: 'running', coordinator_status: 'awaiting_confirmation' },
      { status: 'completed' },
    ],
    coordinator_outcome_spec_confirm: { status: 'running' },
    run_show_artifacts: { artifacts: [{ path: 'smoke.txt' }] },
    run_archive: { status: 'archived' },
  });

  const result = await runSmoke({
    client,
    contract,
    projectName: 'smoke',
    workingDirectory: '.',
    sleepFn: async () => {},
  });

  assert.equal(result.banner, 'CLI→MCP SMOKE OK');
  assert.equal(result.project.source, 'created');
  assert.equal(result.artifactCount, 1);
  assert.deepEqual(client.calls.map((call) => call.name), [
    'project_list',
    'project_create',
    'run_submit',
    'run_status',
    'coordinator_outcome_spec_confirm',
    'run_status',
    'run_show_artifacts',
    'run_archive',
  ]);
});

test('reuses an existing project and reports the failing workflow step', async () => {
  const client = fakeClient({
    project_list: [{ id: 'project-1', name: 'smoke' }],
    run_submit: { run_id: 'run-1' },
    run_status: { status: 'failed' },
    run_archive: { status: 'archived' },
  });

  await assert.rejects(
    runSmoke({ client, contract, projectName: 'smoke', sleepFn: async () => {} }),
    /run completion assertion failed \(run_status\): expected succeeded\/completed, got failed/,
  );
  assert.equal(client.calls.at(-1).name, 'run_archive');
  assert.equal(client.calls.some((call) => call.name === 'project_create'), false);
});
