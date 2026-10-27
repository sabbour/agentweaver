import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  findImmutableRuntimeConfigMapReplacements,
  replaceImmutableRuntimeConfigMaps,
} from '../immutable-runtime-config-maps.mjs';

const namespace = 'agentweaver';
const name = 'projects-config-runtime-config';

function desiredMap(name, value) {
  return {
    apiVersion: 'v1',
    kind: 'ConfigMap',
    metadata: { name, namespace },
    immutable: true,
    data: { Setting: value },
  };
}

// A minimal API server records what kubectl actually sends, so an invalid CLI flag or a
// dropped request body fails here instead of only on a live cluster.
async function startApiServer(statusFor) {
  const requests = [];
  const server = http.createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({ method: request.method, url: request.url, body });
      const status = statusFor(request);
      response.statusCode = status;
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(status < 300
        ? { kind: 'Status', apiVersion: 'v1', status: 'Success' }
        : { kind: 'Status', apiVersion: 'v1', status: 'Failure', reason: 'Conflict', code: status,
          message: 'Precondition failed: UID in precondition does not match' }));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aw-immutable-map-'));
  const kubeconfig = path.join(directory, 'config');
  fs.writeFileSync(kubeconfig, [
    'apiVersion: v1', 'kind: Config',
    'clusters:', '- name: test', '  cluster:', `    server: http://127.0.0.1:${server.address().port}`,
    'contexts:', '- name: test', '  context:', '    cluster: test', '    user: test',
    'current-context: test', 'users:', '- name: test', '  user: {}', '',
  ].join('\n'));
  return {
    requests,
    kubeconfig,
    async close() {
      await new Promise(resolve => server.close(resolve));
      fs.rmSync(directory, { recursive: true, force: true });
    },
  };
}

function runKubectl(kubeconfig, args, input) {
  return new Promise((resolve, reject) => {
    const child = spawn('kubectl', ['--kubeconfig', kubeconfig, '--namespace', namespace, ...args]);
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', status => resolve({ status, stdout, stderr }));
    child.stdin.end(input ?? '');
  });
}

// Plans one replacement, then runs the guarded delete through real kubectl while the
// live reads and the create stay scripted.
async function replaceWithRealDelete(api) {
  const desired = desiredMap(name, 'new');
  const live = {
    ...desiredMap(name, 'old'),
    metadata: { name, namespace, uid: 'live-uid', resourceVersion: '42' },
  };
  const calls = [];
  let created;
  const run = (command, args, settings = {}) => {
    calls.push({ args, input: settings.input });
    if (args[0] === 'get' && args.includes('--output=name')) return `configmap/${args[2]}`;
    if (args[0] === 'get') {
      const current = args[2] === name ? (created ?? live) : desiredMap(args[2], 'same');
      return JSON.stringify(settings.projectJson(current));
    }
    if (args[0] === 'create') {
      created = JSON.parse(settings.input);
      return '';
    }
    throw new Error(`unexpected kubectl ${args.join(' ')}`);
  };
  const replacements = findImmutableRuntimeConfigMapReplacements({
    items: [
      desired,
      desiredMap('agenthost-runtime-v1', 'same'),
      desiredMap('gateway-runtime-config', 'same'),
      desiredMap('orchestrator-runtime-config', 'same'),
    ],
  }, namespace, { root: '.', run });
  assert.deepEqual(replacements.map(resource => resource.metadata.name), [name]);

  const pending = [];
  const runWithRealDelete = (command, args, settings = {}) => {
    if (args[0] !== 'delete') return run(command, args, settings);
    calls.push({ args, input: settings.input });
    pending.push(runKubectl(api.kubeconfig, args, settings.input));
    return '';
  };
  replaceImmutableRuntimeConfigMaps(replacements, namespace, { root: '.', run: runWithRealDelete });
  return { calls, created, results: await Promise.all(pending) };
}

test('guarded ConfigMap delete sends uid and resourceVersion preconditions through real kubectl', async () => {
  const api = await startApiServer(() => 200);
  try {
    const { calls, created, results } = await replaceWithRealDelete(api);
    assert.deepEqual(results.map(result => result.status), [0], results.map(result => result.stderr).join('\n'));
    assert.deepEqual(api.requests.map(({ method, url }) => [method, url]), [
      ['DELETE', `/api/v1/namespaces/${namespace}/configmaps/${name}`],
    ]);
    assert.deepEqual(JSON.parse(api.requests[0].body), {
      kind: 'DeleteOptions',
      apiVersion: 'v1',
      preconditions: { uid: 'live-uid', resourceVersion: '42' },
    });
    const order = calls.map(({ args }) => args[0]).filter(verb => verb === 'delete' || verb === 'create');
    assert.deepEqual(order, ['delete', 'create']);
    assert.equal(created.data.Setting, 'new');
  } finally {
    await api.close();
  }
});

test('kubectl reports a failed guarded delete when the live ConfigMap changed', async () => {
  const api = await startApiServer(() => 409);
  try {
    const { results } = await replaceWithRealDelete(api);
    assert.notEqual(results[0].status, 0);
    assert.match(results[0].stderr, /Precondition failed|Conflict|409/);
  } finally {
    await api.close();
  }
});

test('run-binding replacement scope allows only its two runtime ConfigMaps', () => {
  const replaceableNames = new Set([
    'identity-broker-runtime-config-v2',
    'projects-config-runtime-config',
  ]);
  const desired = [
    ...[...replaceableNames].map(name => desiredMap(name, 'new')),
    desiredMap('agenthost-runtime-v1', 'same'),
  ];
  const run = (_command, args, settings = {}) => {
    const name = args[2];
    if (args[0] !== 'get') throw new Error(`unexpected kubectl ${args.join(' ')}`);
    if (args.includes('--output=name')) return `configmap/${name}`;
    const live = desiredMap(name, replaceableNames.has(name) ? 'old' : 'same');
    live.metadata.uid = `uid-${name}`;
    live.metadata.resourceVersion = '42';
    return JSON.stringify(settings.projectJson(live));
  };
  const replacements = findImmutableRuntimeConfigMapReplacements(
    { items: desired }, namespace, { root: '.', run, replaceableNames },
  );
  assert.deepEqual(replacements.map(resource => resource.metadata.name), [...replaceableNames]);

  const driftOutsideScope = [
    ...desired,
    desiredMap('gateway-runtime-config', 'new'),
  ];
  assert.throws(() => findImmutableRuntimeConfigMapReplacements(
    { items: driftOutsideScope }, namespace, {
      root: '.',
      run: (_command, args, settings = {}) => {
        const name = args[2];
        if (args.includes('--output=name')) return `configmap/${name}`;
        const live = desiredMap(name, name === 'gateway-runtime-config' ? 'old' :
          replaceableNames.has(name) ? 'old' : 'same');
        live.metadata.uid = `uid-${name}`;
        live.metadata.resourceVersion = '42';
        return JSON.stringify(settings.projectJson(live));
      },
      replaceableNames,
    }),
  /Not eligible for guarded replacement: gateway-runtime-config/);
});
