import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import {
  P1_RUN_SCOPES,
  P1_SETUP_SCOPES,
  P1_SIGNIN_PROFILE_ROOT,
  authorizeP1InBrowser,
  createP1AuthorizationRequest,
  getValidatedP1BrokerTokenActorId,
  isAuthorizeUrl,
  openP1AuthorizedUiPage,
  validateP1BrokerToken,
  withFreshP1BrowserContext,
} from '../p1-signin.mjs';
import {
  createP1SigninIdentityProvider,
  parseP1SigninArguments,
  p1SigninSurfaceArguments,
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

function callbackSession(onFail = () => {}) {
  const listeners = new Map();
  return {
    on(event, listener) { listeners.set(event, listener); },
    async send(method, params) {
      if (method === 'Fetch.enable') {
        assert.equal(params.patterns[0].requestStage, 'Request');
        assert.equal(params.patterns[0].urlPattern, 'https://portal.cluster.aksapp.io/auth/callback*');
      } else if (method === 'Fetch.failRequest') {
        assert.equal(params.errorReason, 'Aborted');
        await onFail();
      } else {
        assert.equal(method, 'Fetch.continueRequest');
      }
    },
    async pause(url) {
      await listeners.get('Fetch.requestPaused')({
        requestId: 'callback-request',
        request: { url },
      });
    },
    async detach() {},
  };
}

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

test('a run token subject can be derived only after its issuer, audience, expiry, and scopes are validated', () => {
  const token = jwt({ actorId: 'signed-in-actor', scopes: P1_RUN_SCOPES });
  assert.equal(getValidatedP1BrokerTokenActorId(token, {
    issuer: ISSUER,
    audience: AUDIENCE,
    requiredScopes: ['api.read', 'projects.orchestrator'],
    now: () => NOW,
  }), 'signed-in-actor');
  assert.throws(() => getValidatedP1BrokerTokenActorId(token, {
    issuer: ISSUER,
    audience: AUDIENCE,
    actorId: 'different-actor',
    requiredScopes: ['api.read', 'projects.orchestrator'],
    now: () => NOW,
  }), /does not match/);
  assert.throws(() => getValidatedP1BrokerTokenActorId(jwt({
    actorId: 'signed-in-actor', projectId: PROJECT_ID, runId: RUN_ID, scopes: P1_RUN_SCOPES,
  }), {
    issuer: ISSUER,
    audience: AUDIENCE,
    requiredScopes: ['api.read', 'projects.orchestrator'],
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
  let pausedUrl;
  const session = callbackSession(() => { abortedUrls.push(pausedUrl); });
  let authorizationRequests = 0;
  let redirectPromise;
  let notifyRedirect;
  const redirectStarted = new Promise(resolve => { notifyRedirect = resolve; });

  async function dispatch(url) {
    if (new URL(url).pathname === '/auth/callback') {
      pausedUrl = url;
      await session.pause(url);
      return;
    }
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
    on() {},
    off() {},
    async newPage() { return page; },
    async newCDPSession() { return session; },
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

test('invalid callbacks are aborted before rejection and page close precedes CDP detach', async t => {
  for (const query of [
    'code=code&state=invalid',
    'code=code&state=expected&state=duplicate',
    'state=expected',
    'code=&state=expected',
    'code=code&state=expected&error=access_denied',
    'code=code&state=expected&iss=https://unexpected.test/',
  ]) {
    await t.test(query, async () => {
      const events = [];
      let releaseAbort;
      const abortGate = new Promise(resolve => { releaseAbort = resolve; });
      let notifyPaused;
      const paused = new Promise(resolve => { notifyPaused = resolve; });
      const session = callbackSession(async () => {
        events.push('abort sent');
        notifyPaused();
        await abortGate;
        events.push('abort acknowledged');
      });
      session.detach = async () => { events.push('detach'); };
      const page = {
        route() {},
        async goto() {
          void session.pause(`https://portal.cluster.aksapp.io/auth/callback?${query}`);
          // Navigation must not decide whether the callback was safely blocked.
          return new Promise(() => {});
        },
        async close() { events.push('close'); },
      };
      const signing = authorizeP1InBrowser({
        context: { pages: () => [page], on() {}, off() {}, async newCDPSession() { return session; } },
        environment: ENVIRONMENT,
        scopes: P1_SETUP_SCOPES,
        makeRequest: options => ({ ...createP1AuthorizationRequest(options), state: 'expected' }),
        timeoutMs: 1000,
        io: { log() {} },
        fetchImpl: async () => { assert.fail('invalid callbacks must not exchange a code'); },
      });
      let settled = false;
      signing.finally(() => { settled = true; }).catch(() => {});
      try {
        await Promise.race([
          paused,
          signing.then(() => assert.fail('invalid callback succeeded')),
        ]);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(settled, false, 'wait for Fetch.failRequest before rejecting');
        assert.deepEqual(events, ['abort sent']);
      } finally {
        releaseAbort();
        await assert.rejects(signing, /P1 sign-in could not be completed/);
      }
      assert.deepEqual(events, ['abort sent', 'abort acknowledged', 'close', 'detach']);
    });
  }
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
    on() {},
    off() {},
    async newCDPSession() { return callbackSession(); },
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

test('built-in UI page adapter waits for a same-origin authorized Gateway response without reading browser tokens', async () => {
  let location = 'about:blank';
  let initialized;
  let checkedResponse;
  const navigationSteps = [];
  const page = {
    url: () => location,
    async addInitScript(script, argument) {
      initialized = { script, argument };
      navigationSteps.push('init');
    },
    async goto(url) {
      location = url;
      navigationSteps.push('goto');
    },
    async evaluate(_callback, key) {
      assert.equal(key, 'agentweaver.auth.signed-in-before');
      return true;
    },
    async waitForResponse(predicate, options) {
      assert.equal(options.timeout, 1000);
      checkedResponse = {
        url: () => `${new URL(ENVIRONMENT.baseUrl).origin}/api/v1/projects`,
        status: () => 200,
      };
      assert.equal(predicate(checkedResponse), true);
      return checkedResponse;
    },
  };
  const context = { pages: () => [page], async newPage() { return page; } };
  const output = [];
  const result = await openP1AuthorizedUiPage({
    context,
    baseUrl: ENVIRONMENT.baseUrl,
    timeoutMs: 1000,
    io: { log: message => output.push(message) },
  });

  assert.equal(result, page);
  assert.equal(location, ENVIRONMENT.baseUrl);
  assert.deepEqual(navigationSteps, ['init', 'goto']);
  assert.deepEqual(initialized.argument, {
    origin: new URL(ENVIRONMENT.baseUrl).origin,
    key: 'agentweaver.auth.signed-in-before',
  });
  assert.match(initialized.script.toString(), /sessionStorage\.setItem/);
  assert.ok(checkedResponse);
  assert.match(output.join('\n'), /does not read browser token storage/);
});

test('built-in UI page adapter rejects a foreign or unauthenticated Gateway response', async () => {
  for (const response of [
    { url: () => 'https://foreign.test/api/v1/projects', status: () => 200 },
    { url: () => `${new URL(ENVIRONMENT.baseUrl).origin}/api/v1/projects`, status: () => 401 },
  ]) {
    const page = {
      url: () => ENVIRONMENT.baseUrl,
      async addInitScript() {},
      async goto() {},
      async evaluate() { return true; },
      async waitForResponse(predicate) {
        assert.equal(predicate(response), false);
        throw new Error('controlled response did not match');
      },
    };
    await assert.rejects(openP1AuthorizedUiPage({
      context: { pages: () => [page], async newPage() { return page; } },
      baseUrl: ENVIRONMENT.baseUrl,
      timeoutMs: 1000,
      io: { log() {} },
    }), /did not reach an authorized Gateway response/);
  }
});

test('built-in UI page adapter fails clearly when it cannot start silent authorization', async () => {
  const page = {
    url: () => ENVIRONMENT.baseUrl,
    async addInitScript() {},
    async goto() {},
    async evaluate() { return false; },
    async waitForResponse() {
      throw new Error('must not wait when silent authorization could not start');
    },
  };
  await assert.rejects(openP1AuthorizedUiPage({
    context: { pages: () => [page], async newPage() { return page; } },
    baseUrl: ENVIRONMENT.baseUrl,
    timeoutMs: 1000,
    io: { log() {} },
  }), /could not start silent authorization/);
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

test('UI launcher reuses one browser context and supplies its built-in authorized page', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'agentweaver-p1-ui-'));
  const manifestPath = path.join(directory, 'input.json');
  await writeFile(manifestPath, JSON.stringify(validInput()), 'utf8');
  let location = 'about:blank';
  const page = {
    url: () => location,
    async addInitScript() {},
    async goto(url) { location = url; },
    async evaluate() { return true; },
    async waitForResponse(predicate) {
      const response = {
        url: () => `${new URL(ENVIRONMENT.baseUrl).origin}/api/v1/projects`,
        status: () => 200,
      };
      assert.equal(predicate(response), true);
      return response;
    },
  };
  const context = { pages: () => [page], async newPage() { return page; } };
  const signIns = [];
  const output = [];
  let browserContexts = 0;
  try {
    const code = await runP1SigninCli([
      '--surface', 'ui',
      '--input', manifestPath,
      '--output', path.join(directory, 'evidence.jsonl'),
    ], { AGENTWEAVER_P1_SETUP_TOKEN: 'ignored-existing-value' }, {
      log: message => output.push(message),
      error: message => output.push(message),
    }, {
      signIn: async request => {
        assert.equal(request.context, context);
        signIns.push(request);
        return jwt(request);
      },
      chromium: {},
      withBrowserContext: async (_options, useContext) => {
        browserContexts += 1;
        return useContext(context);
      },
      async runP1(argv, env, _io, { identityProvider, pageProvider }) {
        assert.equal(argv[argv.indexOf('--surface') + 1], 'ui');
        assert.notEqual(env.AGENTWEAVER_P1_SETUP_TOKEN, 'ignored-existing-value');
        assert.equal(typeof pageProvider, 'function');
        const identity = await identityProvider({
          ...ENVIRONMENT,
          projectId: PROJECT_ID,
          runId: RUN_ID,
        });
        assert.deepEqual(Object.keys(identity), ['token']);
        assert.equal(signIns.length, 2);
        assert.equal(await pageProvider({ baseUrl: ENVIRONMENT.baseUrl }), page);
        return 0;
      },
    });

    assert.equal(code, 0, output.join('\n'));
    assert.equal(browserContexts, 1);
    assert.equal(signIns.length, 2);
    assert.ok(signIns.every(request => request.context === context));
    assert.equal(signIns[0].projectId, undefined);
    assert.equal(signIns[1].projectId, PROJECT_ID);
    assert.equal(location, ENVIRONMENT.baseUrl);
    assert.doesNotMatch(output.join('\n'), /eyJ|accessToken|Bearer/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('launcher dry-run validates the manifest without opening a browser or writing output', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'agentweaver-p1-dry-run-'));
  const manifestPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'evidence.jsonl');
  await writeFile(manifestPath, JSON.stringify(validInput()), 'utf8');
  const output = [];
  let browserLoads = 0;
  let signIns = 0;
  let runs = 0;
  try {
    const code = await runP1SigninCli([
      '--surface', 'api',
      '--input', manifestPath,
      '--output', outputPath,
      '--dry-run',
    ], {}, {
      log: message => output.push(message),
      error: message => output.push(message),
    }, {
      loadChromium: async () => { browserLoads += 1; throw new Error('browser must not load'); },
      signIn: async () => { signIns += 1; },
      async runP1() { runs += 1; },
    });

    assert.equal(code, 0);
    assert.equal(browserLoads, 0);
    assert.equal(signIns, 0);
    assert.equal(runs, 0);
    assert.match(output.join('\n'), /Owner and Orchestrator/);
    assert.match(output.join('\n'), /No browser, network request, evidence file, project, or assignment is created/);
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('launcher reports missing input before loading the browser', async () => {
  const errors = [];
  let browserLoads = 0;
  const code = await runP1SigninCli([
    '--surface', 'api',
    '--output', 'evidence.jsonl',
  ], {}, { log() {}, error: message => errors.push(message) }, {
    loadChromium: async () => { browserLoads += 1; },
  });

  assert.equal(code, 2);
  assert.equal(browserLoads, 0);
  assert.match(errors.join('\n'), /input manifest/i);
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

test('redirect-hop callbacks are paused without page routing and rejected before code exchange when invalid', async t => {
  for (const scenario of ['valid', 'pending-navigation', 'wrong-state', 'duplicate-code', 'error', 'wrong-issuer', 'path-mismatch', 'abort-failure', 'close-failure']) {
    await t.test(scenario, async () => {
      let failed = false;
      let exchanged = false;
      let detached = false;
      let closed = false;
      let routeCount = 0;
      let releaseNavigation;
      const valid = ['valid', 'pending-navigation'].includes(scenario);
      const session = callbackSession(() => {
        if (scenario === 'abort-failure') throw new Error('controlled CDP failure');
        failed = true;
      });
      session.detach = async () => { detached = true; };
      const page = {
        route() { routeCount += 1; },
        async goto(url) {
          const authorize = new URL(url);
          const callback = new URL(authorize.searchParams.get('redirect_uri'));
          callback.searchParams.set('state', scenario === 'wrong-state' ? 'wrong' : authorize.searchParams.get('state'));
          callback.searchParams.set('code', 'redirect-code');
          if (scenario === 'duplicate-code') callback.searchParams.append('code', 'second');
          if (scenario === 'error') callback.searchParams.set('error', 'access_denied');
          if (scenario === 'wrong-issuer') callback.searchParams.set('iss', 'https://wrong.test/');
          if (scenario === 'path-mismatch') callback.pathname += '-unexpected';
          if (scenario === 'close-failure') callback.searchParams.set('state', 'wrong');
          // Redirect hops bypass Playwright routes. Only request-stage CDP sees this callback.
          await session.pause(callback.toString());
          if (scenario === 'pending-navigation') {
            await new Promise(resolve => { releaseNavigation = resolve; });
          }
          throw new Error('navigation aborted');
        },
        async close() {
          closed = true;
          if (scenario === 'close-failure') throw new Error('controlled page close failure');
          releaseNavigation?.();
        },
      };
      const signing = authorizeP1InBrowser({
        context: {
          pages: () => [page],
          on() {},
          off() {},
          async newCDPSession(target) { assert.equal(target, page); return session; },
        },
        environment: ENVIRONMENT,
        scopes: P1_SETUP_SCOPES,
        io: { log() {} },
        timeoutMs: 1000,
        fetchImpl: async (_url, options) => {
          assert.equal(failed, true, 'the callback is blocked before exchanging its code');
          assert.equal(options.body.get('code'), 'redirect-code');
          exchanged = true;
          return { ok: true, async json() { return { token_type: 'Bearer', access_token: jwt() }; } };
        },
      });
      if (valid) assert.equal(await signing, jwt());
      else await assert.rejects(signing, /P1 sign-in could not be completed/);
      assert.equal(exchanged, valid);
      assert.equal(failed, scenario !== 'abort-failure', 'invalid callbacks are also aborted');
      assert.equal(routeCount, 1, 'page routing is used only for the initial authorization/consent request');
      assert.equal(closed, true);
      assert.equal(detached, scenario !== 'close-failure', 'never detach from a page that failed to close');
    });
  }
});

test('multi-surface launcher shares one setup sign-in and runs both surfaces despite a blocked result', async () => {
  const { rm } = await import('node:fs/promises');
  const manifestPath = path.resolve('scripts/harness-shared/test/p1-signin-multiple.fixture.json');
  await writeFile(manifestPath, JSON.stringify(validInput()));
  let browserSessions = 0;
  const context = {};
  const requests = [];
  const runs = [];
  try {
    const result = await runP1SigninCli([
      '--surface', 'api,mcp', '--input', manifestPath, '--output', 'evidence.jsonl',
      '--native-probe-envelope', 'native.json',
    ], {}, { log() {}, error() {} }, {
      chromium: {},
      withBrowserContext: async (_options, useContext) => {
        browserSessions += 1;
        return useContext(context);
      },
      signIn: async request => {
        assert.equal(request.context, context);
        requests.push(request);
        return jwt(request);
      },
      async runP1(argv, env, _io, { identityProvider }) {
        const surface = argv[argv.indexOf('--surface') + 1];
        runs.push(surface);
        assert.equal(argv[argv.indexOf('--output') + 1], `evidence.${surface}.jsonl`);
        assert.equal(argv[argv.indexOf('--native-probe-envelope') + 1], 'native.json');
        assert.equal(env.AGENTWEAVER_P1_SETUP_TOKEN, jwt());
        await identityProvider({
          ...ENVIRONMENT, projectId: `${PROJECT_ID}-${surface}`, runId: RUN_ID,
        });
        return surface === 'api' ? 2 : 0;
      },
    });
    assert.equal(result, 2);
    assert.deepEqual(runs, ['api', 'mcp']);
    assert.equal(browserSessions, 1);
    assert.equal(requests.length, 3);
    assert.deepEqual(requests[0].scopes, P1_SETUP_SCOPES);
    assert.equal(requests[1].projectId, `${PROJECT_ID}-api`);
    assert.equal(requests[2].projectId, `${PROJECT_ID}-mcp`);
  } finally {
    await rm(manifestPath, { force: true });
  }
});

test('surface output names are deterministic and invalid surface lists never open a browser', async () => {
  for (const [output, expected] of [
    ['evidence.jsonl', 'evidence.mcp.jsonl'],
    ['evidence', 'evidence.mcp'],
    ['dir.with.dot\\evidence.jsonl', 'dir.with.dot\\evidence.mcp.jsonl'],
  ]) {
    const argv = ['--surface', 'mcp,api', '--output', output];
    assert.deepEqual(p1SigninSurfaceArguments(argv, 'mcp', output, true),
      ['--surface', 'mcp', '--output', expected]);
    assert.deepEqual(argv, ['--surface', 'mcp,api', '--output', output]);
    assert.deepEqual(p1SigninSurfaceArguments(argv, 'mcp', output, false),
      ['--surface', 'mcp', '--output', output]);
  }
  for (const surface of ['api,api', 'api,ui', 'api,', 'api, mcp', 'unknown', '']) {
    assert.equal(await runP1SigninCli([
      '--surface', surface, '--input', 'manifest.json', '--output', 'evidence.jsonl',
    ], {}, { error() {} }, {
      loadChromium: async () => { assert.fail('invalid input must not launch a browser'); },
    }), 2);
  }
});
