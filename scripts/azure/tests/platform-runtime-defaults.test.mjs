import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  parsePlatformRuntimeDefaultsArgs,
  runPlatformRuntimeDefaults,
} from '../lib/platform-runtime-defaults.mjs';

const issuer = 'https://identity.example/';
const audience = 'https://gateway.example/';
const defaults = {
  modelSelection: null,
  modelTaskDefaults: [
    { taskKind: 'planning', selection: { reference: 'gpt-6.1-sol', reasoningEffort: 'high' } },
    { taskKind: 'codeWriting', selection: { reference: 'gpt-6-luna', reasoningEffort: 'high' } },
  ],
  modelRoleDefaults: [],
  modelAgentDefaults: [],
  egressBaseline: [],
  runLimits: {
    maxModelTurns: 12,
    maxToolCalls: 100,
    maxChildren: 4,
    maxConcurrentChildren: 2,
    maxWallTimeSeconds: 3600,
    maxPromptTokens: 20000,
  },
};

function createToken(overrides = {}) {
  const claims = {
    iss: issuer,
    aud: audience,
    sub: 'platform-admin',
    exp: Math.floor(Date.now() / 1000) + 3600,
    scope: 'api.read projects.admin projects.orchestrator',
    ...overrides,
  };
  return `eyJhbGciOiJub25lIn0.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.signature`;
}

const testToken = createToken();

function options(overrides = {}) {
  return {
    baseUrl: 'https://gateway.example/',
    issuer,
    audience,
    tenantId: 'controlled-tenant',
    inputPath: 'deploy/k8s/runtime-configs/platform-runtime-defaults.json',
    execute: false,
    ...overrides,
  };
}

function createDependencies({ responses, token = testToken, input = defaults } = {}) {
  const calls = [];
  const logs = [];
  return {
    calls,
    logs,
    overrides: {
      readFile: () => JSON.stringify(input),
      signIn: async () => token,
      fetch: async (url, request = {}) => {
        calls.push({ url, request });
        const result = responses.shift();
        return {
          ok: result.status >= 200 && result.status < 300,
          status: result.status,
          json: async () => result.body,
        };
      },
      log: message => logs.push(message),
    },
  };
}

test('arguments require an HTTPS Gateway, issuer, and audience', () => {
  assert.equal(parsePlatformRuntimeDefaultsArgs(['--help']).help, true);
  assert.throws(() => parsePlatformRuntimeDefaultsArgs([]), /--base-url must be an absolute HTTPS URL/);
  assert.throws(() => parsePlatformRuntimeDefaultsArgs([
    '--base-url', 'http://gateway.example/', '--issuer', issuer, '--audience', audience,
  ]), /--base-url must be an absolute HTTPS URL/);
  assert.throws(() => parsePlatformRuntimeDefaultsArgs([
    '--base-url', 'https://gateway.example/', '--issuer', 'https://identity.example/path',
    '--audience', audience,
  ]), /--issuer must be an absolute HTTPS URL/);
  assert.equal(parsePlatformRuntimeDefaultsArgs([
    '--base-url', 'https://gateway.example/', '--issuer', issuer, '--audience', audience,
    '--tenant', 'controlled-tenant',
  ]).execute, false);
  assert.throws(() => parsePlatformRuntimeDefaultsArgs([
    '--base-url', 'https://gateway.example/', '--issuer', issuer, '--audience', audience,
  ]), /--tenant is required/);
});

test('dry run reports exact current defaults without writing', async () => {
  const dependencies = createDependencies({
    responses: [{ status: 200, body: { revision: 4, defaults } }],
  });
  const result = await runPlatformRuntimeDefaults(options(), dependencies.overrides);
  assert.deepEqual(result, { result: 'exact', revision: 4, exitCode: 0 });
  assert.equal(dependencies.calls.length, 1);
  assert.equal(dependencies.calls[0].request.method, undefined);
  assert.match(dependencies.calls[0].url, /\/api\/v1\/platform\/runtime-defaults$/);
});

test('dry run returns exit code two when current defaults differ', async () => {
  const dependencies = createDependencies({
    responses: [{ status: 200, body: { revision: 0, defaults: null } }],
  });
  const result = await runPlatformRuntimeDefaults(options(), dependencies.overrides);
  assert.deepEqual(result, { result: 'differs', revision: 0, exitCode: 2 });
  assert.equal(dependencies.calls.length, 1);
  assert.equal(dependencies.calls[0].request.method, undefined);
});

test('execute is a no-op when current defaults already match', async () => {
  const dependencies = createDependencies({
    responses: [{ status: 200, body: { revision: 8, defaults } }],
  });
  const result = await runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides);
  assert.deepEqual(result, { result: 'exact', revision: 8, exitCode: 0 });
  assert.equal(dependencies.calls.length, 1);
  assert.equal(dependencies.calls[0].request.method, undefined);
  assert.equal(dependencies.logs.at(-1),
    'Platform runtime defaults already match the reviewed input at revision 8; no update was needed.');
});

test('execute uses the current expected revision and verifies exact readback', async () => {
  const dependencies = createDependencies({
    responses: [
      { status: 200, body: { revision: 6, defaults: null } },
      { status: 200, body: { revision: 7, defaults } },
      { status: 200, body: { revision: 7, defaults } },
    ],
  });
  const result = await runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides);
  assert.deepEqual(result, { result: 'exact', revision: 7, exitCode: 0 });
  assert.equal(dependencies.calls.length, 3);
  const put = dependencies.calls[1].request;
  assert.equal(put.method, 'PUT');
  assert.deepEqual(JSON.parse(put.body), { expectedRevision: 6, defaults });
  assert.equal(put.headers.Authorization, `Bearer ${testToken}`);
  assert.deepEqual(dependencies.calls.map(call => call.request.headers['X-Agentweaver-Tenant']),
    ['controlled-tenant', 'controlled-tenant', 'controlled-tenant']);
  assert.equal(dependencies.logs.at(-1), 'Platform runtime defaults were written and verified at revision 7.');
});

test('execute fails when the returned configuration is not an exact readback', async () => {
  const altered = { ...defaults, egressBaseline: [{ host: 'unexpected.example' }] };
  const dependencies = createDependencies({
    responses: [
      { status: 200, body: { revision: 2, defaults: null } },
      { status: 200, body: { revision: 3, defaults } },
      { status: 200, body: { revision: 3, defaults: altered } },
    ],
  });
  await assert.rejects(
    runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides),
    /does not exactly match the reviewed input/);
});

test('omitted nullable model and budget fields match raw nulls in compare and execute', async () => {
  const input = {
    ...defaults,
    modelSelection: { reference: 'byok-model', reasoningEffort: 'low' },
    runLimits: {
      ...defaults.runLimits,
      spendBudgets: [{
        budgetId: 'byok-run', revision: 1, scope: 'run', meterSource: 'byok.tokens',
        unit: 'USD', hardLimit: 5, reservePerDispatch: 0.5,
      }],
    },
  };
  const normalized = structuredClone(input);
  normalized.modelSelection.credentialReference = null;
  normalized.runLimits.spendBudgets[0].softLimit = null;
  for (const [reviewed, readback] of [[input, normalized], [normalized, input]]) {
    for (const execute of [false, true]) {
      const dependencies = createDependencies({
        input: reviewed, responses: [{ status: 200, body: { revision: 4, defaults: readback } }],
      });
      assert.deepEqual(await runPlatformRuntimeDefaults(options({ execute }), dependencies.overrides),
        { result: 'exact', revision: 4, exitCode: 0 });
      assert.equal(dependencies.calls.length, 1);
    }
  }
  const dependencies = createDependencies({
    input,
    responses: [
      { status: 200, body: { revision: 6, defaults: null } },
      { status: 200, body: { revision: 7, defaults: normalized } },
      { status: 200, body: { revision: 7, defaults: normalized } },
    ],
  });
  assert.deepEqual(await runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides),
    { result: 'exact', revision: 7, exitCode: 0 });
  const unexpectedCredential = structuredClone(normalized);
  unexpectedCredential.modelSelection.credentialReference = { id: 'ref-a', version: 'v1' };
  const mismatch = createDependencies({
    input, responses: [{ status: 200, body: { revision: 7, defaults: unexpectedCredential } }],
  });
  assert.equal((await runPlatformRuntimeDefaults(options(), mismatch.overrides)).exitCode, 2);
});

test('readback rejects changed values, unknown nulls, and redacted credential references', async () => {
  const input = {
    ...defaults,
    modelSelection: {
      reference: 'byok-model', reasoningEffort: 'low',
      credentialReference: { id: 'ref-a', version: 'v1' },
    },
    runLimits: {
      ...defaults.runLimits,
      spendBudgets: [{
        budgetId: 'byok-run', revision: 1, scope: 'run', meterSource: 'byok.tokens',
        unit: 'USD', softLimit: 2, hardLimit: 5, reservePerDispatch: 0.5,
      }],
    },
  };
  const changed = [
    value => { value.modelSelection.credentialReference.version = 'v2'; },
    value => { value.modelSelection.credentialReference = null; },
    value => { value.modelSelection.credentialReference = '[REDACTED]'; },
    value => { value.modelSelection.credentialReference.id = '[REDACTED]'; },
    value => { value.modelSelection.credentialReference.id = '[REDACTED_CANARY]'; },
    value => { value.modelSelection.reference = 'another-model'; },
    value => { value.runLimits.spendBudgets[0].softLimit = null; },
    value => { value.runLimits.spendBudgets[0].hardLimit = 6; },
    value => { value.runLimits.spendBudgets[0].unexpected = null; },
    value => { value['modelSelection.credentialReference'] = null; },
  ];
  const exact = createDependencies({
    input, responses: [{ status: 200, body: { revision: 2, defaults: structuredClone(input) } }],
  });
  assert.equal((await runPlatformRuntimeDefaults(options(), exact.overrides)).exitCode, 0);
  for (const change of changed) {
    const readback = structuredClone(input);
    change(readback);
    const dependencies = createDependencies({
      input,
      responses: [
        { status: 200, body: { revision: 2, defaults: null } },
        { status: 200, body: { revision: 3, defaults: input } },
        { status: 200, body: { revision: 3, defaults: readback } },
      ],
    });
    await assert.rejects(runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides),
      /does not exactly match the reviewed input/);
  }
  for (const marker of ['[REDACTED]', '[REDACTED_CANARY]', '[REDACTED_GITHUB_TOKEN]']) {
    const redactedInput = structuredClone(input);
    redactedInput.modelSelection.credentialReference.id = marker;
    const dependencies = createDependencies({ input: redactedInput, responses: [] });
    await assert.rejects(runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides),
      /reviewed input file contains a redaction marker/);
    assert.equal(dependencies.calls.length, 0);
  }
});

test('a redacted current response stops execute before any write', async () => {
  const redacted = structuredClone(defaults);
  redacted.modelSelection = { reference: 'byok-model', credentialReference: '[REDACTED]' };
  const dependencies = createDependencies({
    responses: [{ status: 200, body: { revision: 4, defaults: redacted } }],
  });
  await assert.rejects(runPlatformRuntimeDefaults(options({ execute: true }), dependencies.overrides),
    /GET contains a redaction marker/);
  assert.equal(dependencies.calls.length, 1);
});

test('an under-scoped or bound token is rejected before any API call', async () => {
  const dependencies = createDependencies({
    token: createToken({ scope: 'api.read', project_id: 'project-1', run_id: 'run-1' }),
    responses: [],
  });
  await assert.rejects(
    runPlatformRuntimeDefaults(options(), dependencies.overrides),
    /does not match the selected actor, authority, and run/);
  assert.equal(dependencies.calls.length, 0);
});
