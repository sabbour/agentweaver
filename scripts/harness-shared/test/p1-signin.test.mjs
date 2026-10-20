import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import {
  P1_RUN_SCOPES,
  P1_SETUP_SCOPES,
  P1_SIGNIN_PROFILE_ROOT,
  authorizeP1InBrowser,
  createP1AuthorizationRequest,
  isAuthorizeUrl,
  validateP1BrokerToken,
  withFreshP1BrowserContext,
} from '../p1-signin.mjs';
import {
  createP1SigninIdentityProvider,
  parseP1SigninArguments,
  runP1SigninCli,
} from '../p1-signin-run.mjs';

const ISSUER = 'https://agentweaver.cluster.aksapp.io/';
const AUDIENCE = 'https://agentweaver.cluster.aksapp.io/';
const ENVIRONMENT = {
  baseUrl: 'https://environment.controlled.test/',
  issuer: ISSUER,
  audience: AUDIENCE,
  actorId: 'acceptance-actor',
  tenantId: 'acceptance-tenant',
};
const PROJECT_ID = 'fixture-project';
const RUN_ID = '00000000-0000-4000-8000-000000000001';
const DENIED_ACTOR = { actorId: 'denied-actor', tenantId: 'denied-tenant' };
const NOW = 2_000_000_000_000;

function jwt({
  actorId = ENVIRONMENT.actorId,
  projectId,
  runId,
  scopes = P1_SETUP_SCOPES,
  exp = NOW / 1000 + 600,
} = {}) {
  return [
    'header',
    Buffer.from(JSON.stringify({
      iss: ISSUER,
      sub: actorId,
      aud: AUDIENCE,
      exp,
      scope: scopes.join(' '),
      ...(projectId ? { project_id: projectId } : {}),
      ...(runId ? { run_id: runId } : {}),
    })).toString('base64url'),
    'signature',
  ].join('.');
}

function validInput() {
  const sourceSha = 'a'.repeat(40);
  const registryDigest = `sha256:${'b'.repeat(64)}`;
  return {
    executionIntent: 'execute',
    journey: 'journal',
    environment: ENVIRONMENT,
    deploymentEvidence: {
      publicationPath: 'artifacts/p1/publication.json',
      manifestPath: 'releases/foundation.json',
      agentHostArchivePath: 'artifacts/p1/Agentweaver.AgentHost.oci.tar',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver',
      providerReceipt: {
        configMapName: 'provider-catalog-v1',
        sourceCommit: 'c'.repeat(40),
        uid: 'provider-uid',
        resourceVersion: '12',
      },
      runtimeReceipt: { generation: 1 },
      storageReceipt: { pvUid: 'controlled-pv' },
      sourceReceipt: { sourceSha, sourceTree: 'd'.repeat(40) },
      imageReceipt: {
        registryImage: `ghcr.io/sabbour/agentweaver/agenthost@${registryDigest}`,
        registryDigest,
        archiveSha256: 'e'.repeat(64),
        manifestDigest: `sha256:${'f'.repeat(64)}`,
        configDigest: `sha256:${'1'.repeat(64)}`,
        compressedPullBytes: 1024,
      },
    },
    fixturePlan: {
      expectedPlatformRuntimeRevision: 7,
      selectionContext: {
        revision: 'provider-catalog-v1',
        availableModelSelectionReferences: ['platform-model'],
        providerRequirements: [],
        requiredEgress: [],
      },
      gate: null,
      message: null,
    },
  };
}

test('authorization request uses the registered public client, S256, and exact optional run selectors', () => {
  let randomCall = 0;
  const request = createP1AuthorizationRequest({
    issuer: ISSUER,
    scopes: P1_RUN_SCOPES,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    random: () => Buffer.alloc(32, ++randomCall),
  });
  const url = new URL(request.authorizeUrl);
  const verifier = Buffer.alloc(32, 1).toString('base64url');

  assert.equal(url.origin, new URL(ISSUER).origin);
  assert.equal(url.pathname, '/connect/authorize');
  assert.equal(url.searchParams.get('client_id'), 'agentweaver-web');
  assert.equal(url.searchParams.get('redirect_uri'), 'https://portal.cluster.aksapp.io/auth/callback');
  assert.equal(url.searchParams.get('response_type'), 'code');
  assert.equal(url.searchParams.get('code_challenge_method'), 'S256');
  assert.equal(url.searchParams.get('code_challenge'), createHash('sha256').update(verifier).digest('base64url'));
  assert.equal(url.searchParams.get('project_id'), PROJECT_ID);
  assert.equal(url.searchParams.get('run_id'), RUN_ID);
  assert.deepEqual(url.searchParams.get('scope').split(' '), P1_RUN_SCOPES);
  assert.equal(P1_RUN_SCOPES.includes('projects.admin'), false);

  const unbound = createP1AuthorizationRequest({
    issuer: ISSUER,
    scopes: P1_SETUP_SCOPES,
    random: () => Buffer.alloc(32, 2),
  });
  assert.equal(new URL(unbound.authorizeUrl).searchParams.has('project_id'), false);
  assert.equal(new URL(unbound.authorizeUrl).searchParams.has('run_id'), false);
});

test('authorization interception accepts the Broker continuation query without weakening bindings', () => {
  const request = createP1AuthorizationRequest({
    issuer: ISSUER,
    scopes: P1_RUN_SCOPES,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    random: () => Buffer.alloc(32, 3),
  });
  const original = new URL(request.authorizeUrl);
  const orderedKeys = [
    'client_id', 'redirect_uri', 'response_type', 'scope', 'state',
    'code_challenge', 'code_challenge_method', 'nonce', 'project_id', 'run_id',
  ];
  const query = orderedKeys.flatMap(name => {
    const value = name === 'nonce' ? 'broker-generated-nonce'
      : original.searchParams.get(name);
    return value === null ? [] : [`${encodeURIComponent(name)}=${encodeURIComponent(value)}`];
  }).join('&');
  const brokerResumeUrl = `${original.origin}${original.pathname}?${query}`;

  assert.notEqual(new URL(brokerResumeUrl).search, original.search);
  assert.equal(isAuthorizeUrl(brokerResumeUrl, request), true);

  const changedState = new URL(brokerResumeUrl);
  changedState.searchParams.set('state', 'different-state');
  assert.equal(isAuthorizeUrl(changedState.toString(), request), false);

  const duplicateState = new URL(brokerResumeUrl);
  duplicateState.searchParams.append('state', request.state);
  assert.equal(isAuthorizeUrl(duplicateState.toString(), request), false);

  const unexpected = new URL(brokerResumeUrl);
  unexpected.searchParams.set('access_token', 'unexpected');
  assert.equal(isAuthorizeUrl(unexpected.toString(), request), false);
});

test('Broker tokens must match actor, issuer, audience, expiry, required scopes, and run binding', () => {
  assert.equal(validateP1BrokerToken(jwt(), {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: ENVIRONMENT.actorId,
    requiredScopes: ['api.read', 'projects.admin', 'projects.orchestrator'],
    now: () => NOW,
  }), jwt());

  assert.throws(() => validateP1BrokerToken(jwt({ exp: NOW / 1000 }), {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: ENVIRONMENT.actorId,
    now: () => NOW,
  }), /does not match/);
  assert.throws(() => validateP1BrokerToken(jwt({ scopes: ['api.read'] }), {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: ENVIRONMENT.actorId,
    requiredScopes: ['projects.admin'],
    now: () => NOW,
  }), /does not match/);
  assert.throws(() => validateP1BrokerToken(jwt({ projectId: PROJECT_ID, runId: RUN_ID }), {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: ENVIRONMENT.actorId,
    now: () => NOW,
  }), /does not match/);
  assert.throws(() => validateP1BrokerToken(jwt({ projectId: PROJECT_ID, runId: 'other-run' }), {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: ENVIRONMENT.actorId,
    projectId: PROJECT_ID,
    runId: RUN_ID,
    now: () => NOW,
  }), /does not match/);
});

test('sign-in completes consent, intercepts the registered callback, and exchanges the code using fakes', async () => {
  const accessToken = jwt();
  const routes = [];
  const exposedFunctions = new Map();
  const browserFetches = [];
  const exchangeRequests = [];
  const log = [];
  const abortedUrls = [];
  let authorizationRequests = 0;
  let redirectPromise;
  let notifyRedirect;
  const redirectStarted = new Promise(resolve => { notifyRedirect = resolve; });

  async function dispatch(url) {
    for (const { matcher, handler } of routes) {
      if (!matcher(url)) continue;
      const route = {
        request: () => ({ url: () => url }),
        async fetch() {
          authorizationRequests += 1;
          if (authorizationRequests === 1) {
            return {
              status: () => 200,
              ok: () => true,
              headers: () => ({ 'content-type': 'application/json' }),
              async json() {
                return {
                  consent_required: true,
                  consent_handle: 'controlled-consent-handle',
                  client_id: 'agentweaver-web',
                  requested_scopes: P1_SETUP_SCOPES,
                  csrf_token: 'controlled-csrf-token',
                };
              },
            };
          }
          const authorizeUrl = new URL(url);
          const callbackUrl = new URL(authorizeUrl.searchParams.get('redirect_uri'));
          callbackUrl.searchParams.set('code', 'controlled-authorization-code');
          callbackUrl.searchParams.set('state', authorizeUrl.searchParams.get('state'));
          callbackUrl.searchParams.set('iss', `${new URL(ISSUER).origin}/`);
          return {
            status: () => 302,
            ok: () => false,
            headers: () => ({ location: callbackUrl.toString() }),
          };
        },
        async fulfill({ response } = {}) {
          if (response?.status() >= 300 && response.status() < 400) {
            await dispatch(response.headers().location);
          }
        },
        async abort() {
          abortedUrls.push(url);
        },
      };
      await handler(route);
      return;
    }
    throw new Error('The fake browser received an unhandled route.');
  }

  const page = {
    setDefaultTimeout() {},
    setDefaultNavigationTimeout() {},
    route(matcher, handler) {
      routes.push({ matcher, handler });
    },
    async goto(url) {
      await dispatch(url);
    },
    async exposeFunction(name, callback) {
      exposedFunctions.set(name, callback);
    },
    async evaluate(callback, input) {
      const elements = [];
      const body = {
        style: {},
        replaceChildren() {},
        append(...children) { elements.push(...children); },
      };
      const document = {
        title: '',
        body,
        createElement(tagName) {
          const listeners = new Map();
          const element = {
            tagName,
            style: {},
            listeners,
            append() {},
            setAttribute() {},
            addEventListener(name, listener) { listeners.set(name, listener); },
          };
          elements.push(element);
          return element;
        },
      };
      const window = {
        location: {
          assign(url) {
            redirectPromise = dispatch(url);
            notifyRedirect();
          },
        },
      };
      for (const [name, exposed] of exposedFunctions) window[name] = exposed;
      const fetch = async (url, options) => {
        browserFetches.push({ url, options });
        return { ok: false, status: 0, type: 'opaqueredirect' };
      };
      runInNewContext(`(${callback.toString()})(__input)`, {
        __input: input,
        document,
        window,
        fetch,
      });
      const approveButton = elements.find(element => element.textContent === 'Approve');
      assert.ok(approveButton, 'the consent prompt renders an approve button');
      approveButton.listeners.get('click')();
      await redirectStarted;
      await redirectPromise;
    },
    pages: () => [page],
    async close() {},
  };
  const context = {
    pages: () => [page],
    async newPage() { return page; },
  };
  const codeExchange = async (url, options) => {
    exchangeRequests.push({ url: String(url), options });
    return {
      ok: true,
      async json() {
        return {
          token_type: 'Bearer',
          access_token: accessToken,
          expires_in: 600,
        };
      },
    };
  };

  const token = await authorizeP1InBrowser({
    context,
    environment: ENVIRONMENT,
    actorId: ENVIRONMENT.actorId,
    scopes: P1_SETUP_SCOPES,
    io: { log: message => log.push(message) },
    fetchImpl: codeExchange,
  });

  assert.equal(token, accessToken);
  assert.equal(authorizationRequests, 2);
  assert.equal(browserFetches.length, 1);
  assert.equal(browserFetches[0].url, '/connect/consent');
  assert.equal(browserFetches[0].options.method, 'POST');
  assert.equal(browserFetches[0].options.headers['X-CSRF-TOKEN'], 'controlled-csrf-token');
  assert.equal(browserFetches[0].options.credentials, 'include');
  assert.deepEqual(JSON.parse(browserFetches[0].options.body), {
    consent_handle: 'controlled-consent-handle',
    approve: true,
    scopes: P1_SETUP_SCOPES,
  });
  assert.equal(abortedUrls.length, 1);
  assert.match(abortedUrls[0], /\/auth\/callback\?/);
  assert.equal(exchangeRequests.length, 1);
  assert.equal(exchangeRequests[0].url, `${new URL(ISSUER).origin}/connect/token`);
  assert.equal(new URLSearchParams(exchangeRequests[0].options.body).get('code'), 'controlled-authorization-code');
  assert.doesNotMatch(log.join('\n'), /eyJ|controlled-authorization-code|controlled-csrf-token/);
});

test('cancelling while the consent prompt is open settles sign-in and deletes the profile', async () => {
  const controller = new AbortController();
  const routes = [];
  const listeners = new Map();
  let profilePath;
  let profileClosed = false;
  let showPrompt;
  const promptShown = new Promise(resolve => { showPrompt = resolve; });
  const page = {
    setDefaultTimeout() {},
    setDefaultNavigationTimeout() {},
    route(matcher, handler) { routes.push({ matcher, handler }); },
    once(event, listener) { listeners.set(event, listener); },
    off(event, listener) {
      if (listeners.get(event) === listener) listeners.delete(event);
    },
    async goto(url) {
      const route = routes.find(entry => entry.matcher(url));
      assert.ok(route, 'the authorization request was intercepted');
      await route.handler({
        request: () => ({ url: () => url }),
        async fetch() {
          return {
            status: () => 200,
            ok: () => true,
            headers: () => ({ 'content-type': 'application/json' }),
            async json() {
              return {
                consent_required: true,
                consent_handle: 'controlled-consent-handle',
                client_id: 'agentweaver-web',
                requested_scopes: P1_SETUP_SCOPES,
                csrf_token: 'controlled-csrf-token',
              };
            },
          };
        },
        async fulfill() {},
        async abort() {},
      });
    },
    async exposeFunction() {},
    async evaluate() { showPrompt(); },
    pages() { return [page]; },
    async close() {
      if (profileClosed) return;
      profileClosed = true;
      listeners.get('close')?.();
    },
  };
  const context = {
    pages: () => [page],
    async close() { await page.close(); },
  };
  const chromium = {
    async launchPersistentContext(userDataDir) {
      profilePath = userDataDir;
      return context;
    },
  };
  const signing = withFreshP1BrowserContext({
    chromium,
    authRoot: P1_SIGNIN_PROFILE_ROOT,
    executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    signal: controller.signal,
  }, browserContext => authorizeP1InBrowser({
    context: browserContext,
    environment: ENVIRONMENT,
    scopes: P1_SETUP_SCOPES,
    signal: controller.signal,
    io: { log() {} },
  }));

  await promptShown;
  controller.abort();
  await assert.rejects(signing, /P1 sign-in could not be completed/);
  assert.equal(profileClosed, true);
  await assert.rejects(access(profilePath), { code: 'ENOENT' });
});

test('fresh browser user-data directory is empty and deleted after success, failure, and cancellation', async () => {
  const paths = [];
  let closeCount = 0;
  let delayClose = false;
  let releaseClose;
  let closeStartedResolve;
  const closeStarted = new Promise(resolve => { closeStartedResolve = resolve; });
  const chromium = {
    async launchPersistentContext(userDataDir, options) {
      paths.push(userDataDir);
      assert.deepEqual(await readdir(userDataDir), []);
      assert.equal(options.headless, false);
      assert.equal(options.userDataDir, undefined);
      return {
        pages: () => [],
        async newPage() { return {}; },
        async close() {
          closeCount += 1;
          if (delayClose) {
            closeStartedResolve();
            await new Promise(resolve => { releaseClose = resolve; });
          }
        },
      };
    },
  };

  await withFreshP1BrowserContext({
    chromium,
    authRoot: P1_SIGNIN_PROFILE_ROOT,
    executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  }, async (_context, userDataDir) => {
    await writeFile(path.join(userDataDir, 'browser-state'), 'disposable');
    return 'completed';
  }).then(result => assert.equal(result, 'completed'));

  await assert.rejects(withFreshP1BrowserContext({
    chromium,
    authRoot: P1_SIGNIN_PROFILE_ROOT,
    executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  }, async (_context, userDataDir) => {
    await writeFile(path.join(userDataDir, 'browser-state'), 'disposable');
    throw new Error('controlled sign-in failure');
  }), /controlled sign-in failure/);

  const controller = new AbortController();
  let signalHandlerReady;
  const cancellationReady = new Promise(resolve => { signalHandlerReady = resolve; });
  const cancelled = withFreshP1BrowserContext({
    chromium,
    authRoot: P1_SIGNIN_PROFILE_ROOT,
    executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    signal: controller.signal,
  }, async (_context, userDataDir) => {
    await writeFile(path.join(userDataDir, 'browser-state'), 'disposable');
    return new Promise((_resolve, reject) => {
      controller.signal.addEventListener('abort', () => reject(new Error('controlled cancellation')), { once: true });
      signalHandlerReady();
    });
  });
  await cancellationReady;
  delayClose = true;
  let cancellationSettled = false;
  cancelled.then(
    () => { cancellationSettled = true; },
    () => { cancellationSettled = true; },
  );
  controller.abort();
  await closeStarted;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(cancellationSettled, false);
  releaseClose();
  await assert.rejects(cancelled, /controlled cancellation/);

  assert.equal(closeCount, 3);
  for (const userDataDir of paths) {
    await assert.rejects(access(userDataDir), { code: 'ENOENT' });
  }
});

test('launcher excludes external module inputs and reuses one signed-in account for setup and run scope', async () => {
  assert.throws(() => parseP1SigninArguments([
    '--surface', 'api', '--input', 'input.json', '--output', 'evidence.jsonl',
    '--identity-module', 'provider.mjs',
  ]), /built-in identity provider/);
  assert.throws(() => parseP1SigninArguments([
    '--surface', 'api', '--input', 'input.json', '--output', 'evidence.jsonl',
    '--page-module', 'page.mjs',
  ]), /Use --surface/);

  const input = validInput();
  const manifestPath = path.resolve('scripts/harness-shared/test/p1-signin-input.fixture.json');
  const { writeFile: writeInput, rm } = await import('node:fs/promises');
  await writeInput(manifestPath, JSON.stringify(input), 'utf8');
  const calls = [];
  const output = [];
  const signIn = async request => {
    calls.push(request);
    return jwt({
      actorId: request.actorId,
      projectId: request.projectId,
      runId: request.runId,
      scopes: request.scopes,
    });
  };
  let browserSessions = 0;
  const browserContext = {};
  try {
    const code = await runP1SigninCli([
      '--surface', 'api',
      '--input', manifestPath,
      '--output', 'scripts/harness-shared/test/p1-signin-evidence.fixture.jsonl',
    ], { AGENTWEAVER_P1_SETUP_TOKEN: 'ignored-existing-value' }, {
      log: message => output.push(message),
      error: message => output.push(message),
    }, {
      signIn,
      chromium: {},
      withBrowserContext: async (_options, useContext) => {
        browserSessions += 1;
        return useContext(browserContext);
      },
      async runP1(argv, env, _io, { identityProvider }) {
        assert.equal(argv.includes('--identity-module'), false);
        assert.notEqual(env.AGENTWEAVER_P1_SETUP_TOKEN, 'ignored-existing-value');
        const identity = await identityProvider({
          ...ENVIRONMENT,
          projectId: PROJECT_ID,
          runId: RUN_ID,
        });
        assert.deepEqual(Object.keys(identity), ['token']);
        return 0;
      },
    });
    assert.equal(code, 0, output.join('\n'));
    assert.equal(browserSessions, 1);
    assert.equal(calls.length, 2);
    assert.equal(calls[0].context, browserContext);
    assert.equal(calls[1].context, browserContext);
    assert.deepEqual(calls[0].scopes, P1_SETUP_SCOPES);
    assert.deepEqual(calls[1].scopes, P1_RUN_SCOPES);
    assert.equal(calls[1].projectId, PROJECT_ID);
    assert.equal(calls[1].runId, RUN_ID);
    assert.equal(calls[1].actorId, ENVIRONMENT.actorId);
    assert.doesNotMatch(output.join('\n'), /eyJ|accessToken|Bearer/);
  } finally {
    await rm(manifestPath, { force: true });
  }
});

test('UI P1 launcher blocks on #1859 without starting a browser sign-in', async () => {
  let signIns = 0;
  let received = false;
  const code = await runP1SigninCli([
    '--surface', 'ui',
    '--input', 'manifest.json',
    '--output', 'evidence.jsonl',
  ], {}, { log() {}, error() {} }, {
    signIn: async () => { signIns += 1; },
    async runP1() {
      received = true;
      return 2;
    },
  });
  assert.equal(code, 2);
  assert.equal(received, true);
  assert.equal(signIns, 0);
});

test('launcher refuses a fixture that asks it to sign in a second actor', async () => {
  const input = validInput();
  input.fixturePlan.deniedActor = DENIED_ACTOR;
  const manifestPath = path.resolve('scripts/harness-shared/test/p1-signin-second-actor.fixture.json');
  const { writeFile: writeInput, rm } = await import('node:fs/promises');
  await writeInput(manifestPath, JSON.stringify(input), 'utf8');
  let signIns = 0;
  try {
    const errors = [];
    const code = await runP1SigninCli([
      '--surface', 'api',
      '--input', manifestPath,
      '--output', 'evidence.jsonl',
    ], {}, { log() {}, error: message => errors.push(message) }, {
      signIn: async () => { signIns += 1; },
      chromium: {},
    });
    assert.equal(code, 2);
    assert.equal(signIns, 0);
    assert.match(errors.join('\n'), /one Broker account only/);
  } finally {
    await rm(manifestPath, { force: true });
  }
});

test('built-in identity provider returns only the selected run token', async () => {
  const requests = [];
  const provider = createP1SigninIdentityProvider({
    environment: ENVIRONMENT,
    signIn: async request => {
      requests.push(request);
      return jwt({
        actorId: request.actorId,
        projectId: request.projectId,
        runId: request.runId,
        scopes: request.scopes,
      });
    },
  });
  const result = await provider({
    ...ENVIRONMENT,
    projectId: PROJECT_ID,
    runId: RUN_ID,
  });
  assert.deepEqual(Object.keys(result), ['token']);
  assert.equal(requests.length, 1);
  assert.deepEqual(requests[0].scopes, P1_RUN_SCOPES);
  assert.equal(requests[0].projectId, PROJECT_ID);
  assert.equal(requests[0].runId, RUN_ID);
});
