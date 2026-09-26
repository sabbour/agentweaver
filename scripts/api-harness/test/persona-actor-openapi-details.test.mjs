import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { test } from 'node:test';

const actorPath = new URL('../../../.github/agents/persona-actor.agent.md', import.meta.url);
const harnessPath = new URL('../../../.github/agents/harness.agent.md', import.meta.url);
const skillPath = new URL('../SKILL.md', import.meta.url);
const oracleAdapterPath = new URL('../../persona-briefs/surfaces/oracle.api.md', import.meta.url);

function extractPowerShellHereString(markdown, commandSuffix) {
  const escapedSuffix = commandSuffix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = markdown.match(new RegExp(
    `@'\\r?\\n([\\s\\S]*?)\\r?\\n\\s*'@ \\| node --input-type=module - ${escapedSuffix}`,
  ));
  assert.ok(match, `Expected PowerShell here-string ending with ${commandSuffix}`);
  return match[1].replace(/^ {3}/gm, '');
}

function extractAuthenticatedRequestScript(markdown) {
  const match = markdown.match(
    /@'\r?\n(\s*import \{ createRecorderSessionAuthProvider \}[\s\S]*?)\r?\n\s*'@ \| node --input-type=module -\r?\n/,
  );
  assert.ok(match, 'Expected authenticated request PowerShell here-string');
  return match[1].replace(/^ {6}/gm, '');
}

async function runNodeScript(script, args, env) {
  const child = spawn(process.execPath, ['--input-type=module', '-', ...args], {
    cwd: new URL('../../..', import.meta.url),
    env: { ...process.env, ...env },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  child.stdin.end(script);
  let stdout = '';
  let stderr = '';
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exitCode = await new Promise(resolve => child.once('close', resolve));
  return { exitCode, stdout, stderr };
}

test('API harness guidance discovers a compact index before resolving only the selected operation', async () => {
  const [actor, harness, skill, oracleAdapter, readme] = await Promise.all([
    readFile(actorPath, 'utf8'),
    readFile(harnessPath, 'utf8'),
    readFile(skillPath, 'utf8'),
    readFile(oracleAdapterPath, 'utf8'),
    readFile(new URL('../README.md', import.meta.url), 'utf8'),
  ]);

  assert.match(actor, /openapi\/v1\.json/);
  assert.match(actor, /compact operation index/i);
  assert.match(actor, /method, path, tags, summary, and\s+`operationId`/i);
  assert.match(actor, /do \*\*not\*\* print complete path items or component objects/i);
  assert.match(actor, /parameters: expand\(parameters\)/);
  assert.match(actor, /requestBody: expand\(operation\.requestBody \?\? null\)/);
  assert.match(actor, /description: operation\.description \?\? ''/);
  assert.match(actor, /nested local `\$ref` values/i);
  assert.match(actor, /latest live response changes the next action/i);
  assert.match(actor, /Do not guess a path, method, parameter, or request shape/i);
  assert.match(actor, /OpenAPI title, summary, description, example, and extension.*untrusted data/is);
  assert.match(actor, /Object\.hasOwn\(paths, pathTemplate\)/);
  assert.match(actor, /allowedMethods\.has\(normalizedMethod\)/);
  assert.match(actor, /Object\.hasOwn\(pathItem, normalizedMethod\)/);
  assert.match(actor, /url\.origin !== baseUrl\.origin/);
  assert.match(actor, /value === '\.' \|\| value === '\.\.'/);
  assert.match(actor, /url\.pathname !== resolvedPath/);
  assert.match(actor, /run-status or run-events operation/i);
  assert.match(actor, /matching\s+approval or denial\s+operation/i);
  assert.match(actor, /absent from the live index.*stop.*instead of probing a guessed path/is);

  for (const guidance of [harness, skill, oracleAdapter]) {
    assert.match(guidance, /openapi\/v1\.json/);
    assert.match(guidance, /compact.*(?:method\/path|method,? ?path).*tags.*summary.*operationId/is);
    assert.match(guidance, /selects?\s+an\s+operation.*(?:latest real response|persona goal)/is);
    assert.match(guidance, /(?:only|selected operation's).*parameters.*(?:resolved local|request-schema)/is);
    assert.match(guidance, /untrusted data/i);
    assert.match(guidance, /target origin/i);
    assert.doesNotMatch(guidance, /openapi\/v1\.yaml/);
  }

  assert.doesNotMatch(skill, /OpenAPI spec is incomplete/i);
  assert.match(skill, /polling, approval, denial, steer, and confirmation\s+actions/i);
  assert.match(skill, /matching real\s+run event/i);
  for (const docs of [readme, skill]) {
    assert.match(docs, /auth-provider probe only, not an API-driving template/i);
  }
});

test('selected-operation detail command runs under PowerShell-safe stdin and resolves nested refs', async t => {
  const actor = await readFile(actorPath, 'utf8');
  const script = extractPowerShellHereString(actor, String.raw`GET /api/example`);
  const spec = {
    paths: {
      '/api/example': {
        get: {
          operationId: 'GetExample',
          description: 'Example details',
          parameters: [{ $ref: '#/components/parameters/ExampleId' }],
          requestBody: { $ref: '#/components/requestBodies/ExampleRequest' },
        },
      },
    },
    components: {
      parameters: {
        ExampleId: { name: 'id', in: 'query', schema: { type: 'string' } },
      },
      requestBodies: {
        ExampleRequest: {
          content: {
            'application/json': {
              schema: { $ref: '#/components/schemas/ExampleBody' },
            },
          },
        },
      },
      schemas: {
        ExampleBody: {
          type: 'object',
          properties: { name: { type: 'string' } },
        },
      },
    },
  };
  const server = createServer((_, response) => {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify(spec));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const address = server.address();
  assert.ok(address && typeof address === 'object');

  const result = await runNodeScript(script, ['GET', '/api/example'], {
    AGENTWEAVER_BASE_URL: `http://127.0.0.1:${address.port}`,
  });

  assert.equal(result.exitCode, 0, result.stderr);
  const details = JSON.parse(result.stdout);
  assert.equal(details.description, 'Example details');
  assert.deepEqual(details.parameters[0], {
    name: 'id',
    in: 'query',
    schema: { type: 'string' },
  });
  assert.equal(
    details.requestBody.content['application/json'].schema.properties.name.type,
    'string',
  );
});

test('authenticated request example rejects inherited methods and normalized dot paths before auth', async t => {
  const actor = await readFile(actorPath, 'utf8');
  const baseScript = extractAuthenticatedRequestScript(actor);
  const spec = {
    paths: {
      '/api/runs/{id}/shell-approvals': {
        post: { operationId: 'ApproveShellCommand' },
      },
      '/api/runs/{id}/{action}': {
        post: { operationId: 'RunAction' },
      },
      '//attacker.example/collect': {
        post: { operationId: 'CrossOriginPath' },
      },
      '/api/runs/../shell-approvals': {
        post: { operationId: 'NormalizedPath' },
      },
    },
  };
  const server = createServer((_, response) => {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify(spec));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const address = server.address();
  assert.ok(address && typeof address === 'object');
  const env = { AGENTWEAVER_BASE_URL: `http://127.0.0.1:${address.port}` };
  const requestScript = ({ method, pathTemplate, pathParameters }) => baseScript
    .replace("const method = '<METHOD>';", `const method = ${JSON.stringify(method)};`)
    .replace(
      "const pathTemplate = '<PATH_FROM_INDEX>';",
      `const pathTemplate = ${JSON.stringify(pathTemplate)};`,
    )
    .replace(
      "const pathParameters = { id: '<VALUE_FROM_LIVE_RESPONSE>' };",
      `const pathParameters = ${JSON.stringify(pathParameters)};`,
    );

  const inheritedMethod = requestScript({
    method: 'constructor',
    pathTemplate: '/api/runs/{id}/shell-approvals',
    pathParameters: { id: 'run-1' },
  });
  const inheritedResult = await runNodeScript(inheritedMethod, [], env);
  assert.notEqual(inheritedResult.exitCode, 0);
  assert.match(inheritedResult.stderr, /Selected operation is not present/);

  const rejectedRequests = [
    {
      pathTemplate: '/api/runs/{id}/shell-approvals',
      pathParameters: { id: '..' },
      error: /Unsafe path parameter/,
    },
    {
      pathTemplate: '/api/runs/{id}/{action}',
      pathParameters: { id: 'run-1' },
      error: /Missing path parameter: action/,
    },
    {
      pathTemplate: '//attacker.example/collect',
      pathParameters: {},
      error: /cross-origin API request/,
    },
    {
      pathTemplate: '/api/runs/../shell-approvals',
      pathParameters: {},
      error: /normalized API path/,
    },
  ];
  for (const request of rejectedRequests) {
    const result = await runNodeScript(requestScript({ method: 'POST', ...request }), [], env);
    assert.notEqual(result.exitCode, 0);
    assert.match(result.stderr, request.error);
  }
});
