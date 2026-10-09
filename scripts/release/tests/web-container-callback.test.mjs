import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, cpSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const callbackPath = '/auth/github/copilot-app/callback';
const hostProject = path.join(root, 'apps', 'web', 'host', 'Agentweaver.Web.Host.csproj');
const pinnedRuntimeImage = 'mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4';
const canRunPinnedImage = process.platform === 'linux' &&
  process.arch === 'x64' &&
  spawnSync('docker', ['version'], {
    stdio: 'ignore',
    timeout: 5_000,
    windowsHide: true,
  }).status === 0 &&
  spawnSync('docker', ['image', 'inspect', pinnedRuntimeImage], {
    stdio: 'ignore',
    timeout: 5_000,
    windowsHide: true,
  }).status === 0;
const isGitHubActions = process.env.GITHUB_ACTIONS === 'true';

function run(command, args, { cwd = root, timeout = 30_000 } = {}) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: 'utf8',
    timeout,
    windowsHide: true,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} failed (${result.status}): ${result.stderr.trim()}`);
  }
  return result.stdout.trim();
}

async function waitForCallback(url) {
  const deadline = Date.now() + 20_000;
  let lastError;
  while (Date.now() < deadline) {
    try {
      return await fetch(url, { signal: AbortSignal.timeout(2_000) });
    } catch (error) {
      lastError = error;
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }
  throw new Error(`Web callback container did not become ready: ${lastError?.message ?? 'request timed out'}`);
}

function executeInlineBootstrap(source, search) {
  const location = { pathname: callbackPath, search, hash: '#fragment' };
  let replacedUrl;
  const window = {
    location,
    history: {
      replaceState(_state, _title, url) {
        replacedUrl = url;
        location.pathname = url;
        location.search = '';
        location.hash = '';
      },
    },
  };
  vm.runInNewContext(source, { URLSearchParams, window });
  return { window, location, replacedUrl };
}

test('pinned amd64 Web image serves and sanitizes the Copilot callback', {
  skip: !canRunPinnedImage && !isGitHubActions
    ? 'requires Linux amd64 Docker with the pinned ASP.NET runtime image already cached'
    : false,
}, async t => {
  assert.equal(process.platform, 'linux', 'the pinned production image check runs on Linux');
  assert.equal(process.arch, 'x64', 'the pinned production image check runs on amd64');
  assert.ok(existsSync(path.join(root, 'apps', 'web', 'dist', 'index.html')),
    'build the Web client before running the production container check');

  const suffix = `${process.pid}-${Date.now()}`;
  const image = `agentweaver-web-callback-test:${suffix}`;
  const container = `agentweaver-web-callback-test-${suffix}`;
  const webDirectory = path.join(root, 'apps', 'web');
  const context = mkdtempSync(path.join(os.tmpdir(), 'agentweaver-web-callback-'));
  const publishDirectory = path.join(context, 'publish');
  let imageBuilt = false;
  let containerCreated = false;
  t.after(() => {
    const cleanupErrors = [];
    for (const [created, args] of [
      [containerCreated, ['rm', '--force', container]],
      [imageBuilt, ['image', 'rm', '--force', image]],
    ]) {
      if (!created) continue;
      try {
        run('docker', args);
      } catch (error) {
        cleanupErrors.push(error);
      }
    }
    rmSync(context, { recursive: true, force: true });
    if (cleanupErrors.length > 0) throw new AggregateError(cleanupErrors, 'Web callback Docker cleanup failed');
  });
  copyFileSync(path.join(webDirectory, 'Dockerfile'), path.join(context, 'Dockerfile'));
  run('dotnet', [
    'publish', hostProject,
    '--configuration', 'Release', '--no-restore', '--output', publishDirectory,
    '-p:UseAppHost=false', '-p:ContinuousIntegrationBuild=true',
  ], { timeout: 600_000 });
  cpSync(path.join(webDirectory, 'dist'), path.join(context, 'wwwroot'), { recursive: true });
  const sourceSha = run('git', ['rev-parse', 'HEAD']);
  run('docker', [
    'build', '--pull=false', '--network=none', '--no-cache', '--platform', 'linux/amd64',
    '--build-arg', 'IMAGE_TAG=callback-test',
    '--build-arg', `GIT_SHA=${sourceSha}`,
    '--file', 'Dockerfile',
    '--tag', image,
    '.',
  ], { cwd: context, timeout: 600_000 });
  imageBuilt = true;

  const baseEnvironment = run('docker', [
    'image', 'inspect', '--format', '{{range .Config.Env}}{{println .}}{{end}}', pinnedRuntimeImage,
  ]);
  const appUid = baseEnvironment.split(/\r?\n/).find(entry => entry.startsWith('APP_UID='))?.slice('APP_UID='.length);
  assert.match(appUid ?? '', /^\d+$/, 'the pinned ASP.NET runtime image defines a numeric APP_UID');
  const imageUser = run('docker', ['image', 'inspect', '--format', '{{.Config.User}}', image]);
  assert.equal(imageUser, appUid, 'the Web image runs as the ASP.NET runtime app UID');

  const clientId = 'client-π";window.__injected=true;//';
  run('docker', [
    'create', '--rm', '--platform', 'linux/amd64',
    '--publish', '127.0.0.1::8080',
    '--env', 'VITE_GATEWAY_URL=',
    '--env', `VITE_OAUTH_CLIENT_ID=${clientId}`,
    '--env', 'VITE_OAUTH_REDIRECT_URI=',
    '--env', 'VITE_OAUTH_SCOPES=read:copilot write:repo',
    '--name', container,
    image,
  ]);
  containerCreated = true;
  run('docker', ['start', container]);
  const runtimeUid = run('docker', ['exec', container, 'id', '-u']);
  assert.equal(runtimeUid, appUid, 'the running Web container uses the ASP.NET runtime app UID');
  assert.notEqual(runtimeUid, '0', 'the running Web container does not use root');

  const publishedAddress = run('docker', ['port', container, '8080/tcp']);
  const port = publishedAddress.match(/^127\.0\.0\.1:(\d+)$/)?.[1];
  assert.ok(port, `Docker published an unexpected callback address: ${publishedAddress}`);
  const origin = `http://127.0.0.1:${port}`;
  const state = 'callback-log-state-a8cd';
  const code = 'callback-log-code-f08b';
  const response = await waitForCallback(`${origin}${callbackPath}?state=${state}&code=${code}`);
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type') ?? '', /^text\/html\b/i);
  assert.match(response.headers.get('cache-control') ?? '', /\bno-store\b/i);
  assert.equal(response.headers.get('referrer-policy'), 'no-referrer');
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(response.headers.get('x-frame-options'), 'DENY');

  const configResponse = await fetch(`${origin}/env-config.js`);
  assert.equal(configResponse.status, 200);
  assert.match(configResponse.headers.get('content-type') ?? '', /javascript/i);
  assert.match(configResponse.headers.get('cache-control') ?? '', /\bno-store\b/i);
  assert.equal(configResponse.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(configResponse.headers.get('x-frame-options'), 'DENY');
  const configScript = await configResponse.text();
  const serializedConfig = configScript.match(
    /^window\.__AGENTWEAVER_CONFIG_BASE64__ = (\{[\s\S]*\});\s*$/,
  )?.[1];
  assert.ok(serializedConfig, 'runtime configuration is a serialized base64 object');
  const encodedConfig = JSON.parse(serializedConfig);
  assert.deepEqual(Object.keys(encodedConfig).sort(), [
    'GATEWAY_URL',
    'IDENTITY_BROKER_ISSUER',
    'IDENTITY_BROKER_URL',
    'OAUTH_CLIENT_ID',
    'OAUTH_REDIRECT_URI',
    'OAUTH_SCOPES',
  ]);
  const decodedConfig = Object.fromEntries(Object.entries(encodedConfig).map(([key, value]) =>
    [key, Buffer.from(value, 'base64').toString('utf8')]));
  assert.deepEqual(decodedConfig, {
    GATEWAY_URL: '/api/v1',
    IDENTITY_BROKER_URL: '',
    IDENTITY_BROKER_ISSUER: '',
    OAUTH_CLIENT_ID: clientId,
    OAUTH_REDIRECT_URI: '',
    OAUTH_SCOPES: 'read:copilot write:repo',
  });
  assert.ok(!configScript.includes(clientId), 'runtime values are not interpolated into JavaScript');

  const html = await response.text();
  const inlineScript = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)]
    .find(([, attributes, source]) => !/\bsrc\s*=/i.test(attributes) && source.trim())?.[2];
  assert.ok(inlineScript, 'callback HTML contains the inline callback bootstrap');
  assert.ok(
    html.indexOf(inlineScript) < html.indexOf('src="/env-config.js"'),
    'the callback bootstrap runs before the runtime config script is requested',
  );

  const assetPath = [...html.matchAll(/<script\b[^>]*\bsrc=["']([^"']+)["'][^>]*>\s*<\/script>/gi)]
    .map(([, src]) => src).find(src => /^\/assets\/[^/]+\.js$/.test(src));
  assert.match(assetPath ?? '', /^\/assets\/[^/]+\.js$/);
  const assetResponse = await fetch(new URL(assetPath, origin));
  assert.equal(assetResponse.status, 200);
  assert.match(assetResponse.headers.get('content-type') ?? '', /javascript/i);
  assert.match(assetResponse.headers.get('cache-control') ?? '', /max-age=31536000.*immutable/i);
  assert.equal(assetResponse.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(assetResponse.headers.get('x-frame-options'), 'DENY');
  const asset = await assetResponse.text();
  assert.ok(asset.includes('agentweaver.copilot-user.callback'),
    'built Web bundle contains the callback message handler');

  const missingExtensionlessAsset = await fetch(`${origin}/assets/missing`);
  assert.equal(missingExtensionlessAsset.status, 404);
  const missingJavaScriptAsset = await fetch(`${origin}/assets/missing.js`);
  assert.equal(missingJavaScriptAsset.status, 404);

  const spaResponse = await fetch(`${origin}/projects`);
  assert.equal(spaResponse.status, 200);
  assert.match(await spaResponse.text(), /id="root"/);

  const valid = executeInlineBootstrap(inlineScript, `?state=${state}&code=${code}`);
  assert.deepEqual(JSON.parse(JSON.stringify(valid.window.__AGENTWEAVER_COPILOT_CALLBACK__)), {
    type: 'agentweaver.copilot-user.callback',
    state,
    code,
  });
  assert.equal(valid.replacedUrl, callbackPath);
  assert.equal(valid.location.search, '');
  assert.equal(valid.location.hash, '');

  const rejected = executeInlineBootstrap(inlineScript,
    '?state=valid-state&code=valid-code&redirect_uri=https%3A%2F%2Fevil.example');
  assert.equal(rejected.window.__AGENTWEAVER_COPILOT_CALLBACK__, undefined);
  assert.equal(rejected.replacedUrl, callbackPath);
  assert.equal(rejected.location.search, '');
  assert.equal(rejected.location.hash, '');

  const logs = run('docker', ['logs', container]);
  assert.ok(!logs.includes(state));
  assert.ok(!logs.includes(code));
  assert.ok(!logs.includes(callbackPath), 'the ASP.NET host does not write request access logs');
});
