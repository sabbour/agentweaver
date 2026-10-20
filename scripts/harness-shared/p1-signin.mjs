import { createHash, randomBytes } from 'node:crypto';
import { readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateP1RunnerPlanInput } from './p1-journey-input.mjs';
import { createFreshChromeUserDataDir, buildFreshChromeLaunchOptions } from '../ui-harness/lib/fresh-chrome-profile.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const P1_SIGNIN_PROFILE_ROOT = path.resolve(HERE, '..', 'ui-harness', '.auth');
export const P1_WEB_CLIENT_ID = 'agentweaver-web';
export const P1_SETUP_SCOPES = [
  'openid', 'profile', 'email', 'api.read', 'offline_access',
  'projects.admin', 'projects.orchestrator',
];
export const P1_RUN_SCOPES = P1_SETUP_SCOPES.filter(scope => scope !== 'projects.admin');
const DEFAULT_TIMEOUT_MS = 600_000;
function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  promise.catch(() => {});
  return { promise, resolve, reject };
}

function base64Url(bytes) {
  return Buffer.from(bytes).toString('base64url');
}

/** Derive the registered Portal callback from the deployed Broker issuer convention. */
export function registeredPortalRedirectUri(issuer) {
  let url;
  try {
    url = new URL(issuer);
  } catch {
    throw new Error('The configured Identity Broker issuer is invalid.');
  }
  const hostname = url.hostname.toLowerCase();
  const portalSuffix = hostname.startsWith('agentweaver.')
    ? hostname.slice('agentweaver.'.length)
    : '';
  if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/'
    || url.search || url.hash || !/^[-a-z0-9.]+\.aksapp\.io$/.test(portalSuffix)) {
    throw new Error('The configured Identity Broker issuer does not identify a registered Agentweaver Portal callback.');
  }
  return `https://portal.${portalSuffix}/auth/callback`;
}

/** Build an S256 authorization request using only the already-registered web client. */
export function createP1AuthorizationRequest({
  issuer,
  redirectUri = registeredPortalRedirectUri(issuer),
  scopes,
  projectId,
  runId,
  random = randomBytes,
}) {
  if (!Array.isArray(scopes) || scopes.length === 0
    || scopes.some(scope => typeof scope !== 'string' || !scope.trim())
    || new Set(scopes).size !== scopes.length) {
    throw new Error('The P1 Broker authorization scope set is invalid.');
  }
  if ((projectId === undefined) !== (runId === undefined)) {
    throw new Error('A run-bound Broker sign-in requires both project and run selectors.');
  }
  const verifier = base64Url(random(32));
  const state = base64Url(random(32));
  const challenge = createHash('sha256').update(verifier).digest('base64url');
  const url = new URL('/connect/authorize', issuer);
  url.searchParams.set('client_id', P1_WEB_CLIENT_ID);
  url.searchParams.set('redirect_uri', redirectUri);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('scope', scopes.join(' '));
  url.searchParams.set('code_challenge', challenge);
  url.searchParams.set('code_challenge_method', 'S256');
  url.searchParams.set('state', state);
  if (projectId !== undefined) {
    url.searchParams.set('project_id', projectId);
    url.searchParams.set('run_id', runId);
  }
  return {
    authorizeUrl: url.toString(),
    brokerUrl: new URL(issuer).origin,
    clientId: P1_WEB_CLIENT_ID,
    redirectUri,
    state,
    verifier,
    projectId,
    runId,
    scopes: [...scopes],
  };
}

function tokenScopes(claim) {
  if (Array.isArray(claim)) return claim.filter(value => typeof value === 'string');
  if (typeof claim === 'string') return claim.split(/\s+/).filter(Boolean);
  return [];
}

/** Validate only the Broker claims required to bind this token to its intended sign-in. */
export function validateP1BrokerToken(token, {
  issuer,
  audience,
  actorId,
  projectId,
  runId,
  requiredScopes = [],
  now = Date.now,
} = {}) {
  let claims;
  try {
    const segments = token.split('.');
    if (typeof token !== 'string' || segments.length !== 3) throw new Error();
    claims = JSON.parse(Buffer.from(segments[1], 'base64url').toString('utf8'));
  } catch {
    throw new Error('The Identity Broker returned an invalid access token.');
  }
  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  const scopes = tokenScopes(claims.scope);
  if (claims.iss !== issuer || claims.sub !== actorId || !audiences.includes(audience)
    || !Number.isFinite(claims.exp) || claims.exp * 1000 <= now()
    || requiredScopes.some(scope => !scopes.includes(scope))
    || (projectId !== undefined && claims.project_id !== projectId)
    || (runId !== undefined && claims.run_id !== runId)
    || (projectId === undefined && (claims.project_id !== undefined || claims.run_id !== undefined))) {
    throw new Error('The Identity Broker token does not match the selected actor, authority, and run.');
  }
  return token;
}

/** Own and always remove one empty Chrome user-data directory around a browser session. */
export async function withFreshP1BrowserContext({
  chromium,
  authRoot = P1_SIGNIN_PROFILE_ROOT,
  signal,
  timeoutMs = DEFAULT_TIMEOUT_MS,
  executablePath,
  makeUserDataDir = createFreshChromeUserDataDir,
}, useContext) {
  if (!chromium || typeof chromium.launchPersistentContext !== 'function') {
    throw new Error('Playwright is required for the headed P1 sign-in flow.');
  }
  if (typeof useContext !== 'function') throw new Error('A disposable browser action is required.');
  const userDataDir = await makeUserDataDir({ authRoot });
  let context;
  let contextClosePromise;
  let failure;
  let result;
  let cleanupFailure = false;
  const closeContext = async () => {
    if (!context) return;
    contextClosePromise ??= Promise.resolve().then(() => context.close()).catch(() => {});
    await contextClosePromise;
  };
  try {
    const launch = buildFreshChromeLaunchOptions(userDataDir, executablePath);
    const { userDataDir: ignoredUserDataDir, ...launchOptions } = launch;
    void ignoredUserDataDir;
    context = await chromium.launchPersistentContext(userDataDir, launchOptions);
    const onAbort = () => { void closeContext(); };
    signal?.addEventListener('abort', onAbort, { once: true });
    try {
      result = await useContext(context, userDataDir);
    } catch (error) {
      failure = error;
    } finally {
      signal?.removeEventListener('abort', onAbort);
    }
  } catch (error) {
    failure = error;
  } finally {
    await closeContext();
    try {
      await rm(userDataDir, { recursive: true, force: true });
    } catch {
      cleanupFailure = true;
    }
  }
  if (cleanupFailure) throw new Error('The disposable P1 browser profile could not be removed.');
  if (failure) throw failure;
  return result;
}

function validateConsentPrompt(value, request) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || value.consent_required !== true
    || typeof value.consent_handle !== 'string' || !value.consent_handle
    || value.client_id !== P1_WEB_CLIENT_ID
    || !Array.isArray(value.requested_scopes)
    || value.requested_scopes.some(scope => typeof scope !== 'string'
      || !request.scopes.includes(scope))
    || typeof value.csrf_token !== 'string' || !value.csrf_token) {
    throw new Error('The Identity Broker returned an invalid consent request.');
  }
  return value;
}

function callbackCode(urlValue, request) {
  const url = new URL(urlValue);
  if (url.origin !== new URL(request.redirectUri).origin
    || url.pathname !== new URL(request.redirectUri).pathname) return null;
  const states = url.searchParams.getAll('state');
  const codes = url.searchParams.getAll('code');
  const errors = url.searchParams.getAll('error');
  const issuers = url.searchParams.getAll('iss');
  if (states.length !== 1 || states[0] !== request.state
    || codes.length !== 1 || errors.length !== 0 || issuers.length > 1
    || (issuers.length === 1 && issuers[0] !== new URL(request.brokerUrl).origin + '/')) {
    throw new Error('The Identity Broker returned an invalid authorization callback.');
  }
  if (!codes[0] || codes[0].length > 4096) {
    throw new Error('The Identity Broker returned an invalid authorization callback.');
  }
  return codes[0];
}

function isAuthorizeUrl(candidate, request) {
  try {
    const url = new URL(candidate);
    const expected = new URL(request.authorizeUrl);
    return url.origin === expected.origin && url.pathname === expected.pathname
      && url.search === expected.search;
  } catch {
    return false;
  }
}

function raceWithAbort(promise, signal, timeoutMs) {
  if (signal?.aborted) return Promise.reject(new Error('P1 sign-in was cancelled.'));
  return new Promise((resolve, reject) => {
    const finish = (callback, value) => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', cancel);
      callback(value);
    };
    const timer = setTimeout(() => finish(reject, new Error('P1 sign-in timed out.')), timeoutMs);
    const cancel = () => finish(reject, new Error('P1 sign-in was cancelled.'));
    signal?.addEventListener('abort', cancel, { once: true });
    promise.then(
      value => finish(resolve, value),
      error => finish(reject, error),
    );
  });
}

async function renderConsentPrompt(page, prompt, request) {
  const decision = deferred();
  await page.exposeFunction('__agentweaverP1ConsentDecision', approved => {
    decision.resolve(approved === true);
  });
  await page.evaluate(({ scopes, authorizeUrl, consent }) => {
    document.title = 'Agentweaver P1 sign-in consent';
    document.body.replaceChildren();
    document.body.style.cssText = 'font:16px system-ui,sans-serif;max-width:44rem;margin:4rem auto;padding:0 1rem';
    const heading = document.createElement('h1');
    heading.textContent = 'Approve Agentweaver access';
    const intro = document.createElement('p');
    intro.textContent = 'Review the permissions requested for this P1 sign-in.';
    const list = document.createElement('ul');
    for (const scope of scopes) {
      const item = document.createElement('li');
      item.textContent = scope;
      list.append(item);
    }
    const status = document.createElement('p');
    status.setAttribute('role', 'status');
    const approve = document.createElement('button');
    approve.textContent = 'Approve';
    const decline = document.createElement('button');
    decline.textContent = 'Decline';
    decline.style.marginLeft = '0.75rem';
    const decide = async (approved) => {
      approve.disabled = true;
      decline.disabled = true;
      status.textContent = 'Submitting your decision…';
      try {
        const response = await fetch('/connect/consent', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Accept: 'application/json',
            'X-CSRF-TOKEN': consent.csrf_token,
          },
          credentials: 'include',
          cache: 'no-store',
          redirect: 'manual',
          body: JSON.stringify({
            consent_handle: consent.consent_handle,
            approve: approved,
            scopes: approved ? scopes : [],
          }),
        });
        if (!(response.ok || (response.status >= 300 && response.status < 400))) {
          status.textContent = 'The consent decision could not be completed. Close this window and rerun the command.';
          await window.__agentweaverP1ConsentDecision(false);
          return;
        }
        await window.__agentweaverP1ConsentDecision(approved);
        if (approved) window.location.assign(authorizeUrl);
        else status.textContent = 'Consent declined. No P1 sign-in token was issued.';
      } catch {
        status.textContent = 'The consent decision could not be completed. Close this window and rerun the command.';
        await window.__agentweaverP1ConsentDecision(false);
      }
    };
    approve.addEventListener('click', () => { void decide(true); });
    decline.addEventListener('click', () => { void decide(false); });
    document.body.append(heading, intro, list, approve, decline, status);
  }, {
    scopes: prompt.requested_scopes,
    authorizeUrl: request.authorizeUrl,
    consent: { consent_handle: prompt.consent_handle, csrf_token: prompt.csrf_token },
  });
  return decision.promise;
}

async function exchangeCode(request, code, fetchImpl) {
  let response;
  try {
    response = await fetchImpl(new URL('/connect/token', request.brokerUrl), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
        Accept: 'application/json',
      },
      cache: 'no-store',
      redirect: 'manual',
      body: new URLSearchParams({
        grant_type: 'authorization_code',
        client_id: request.clientId,
        code,
        redirect_uri: request.redirectUri,
        code_verifier: request.verifier,
      }),
    });
  } catch {
    throw new Error('The Identity Broker authorization-code exchange failed.');
  }
  let body;
  try {
    body = await response.json();
  } catch {
    throw new Error('The Identity Broker authorization-code exchange failed.');
  }
  if (!response.ok || body?.token_type !== 'Bearer'
    || typeof body.access_token !== 'string' || !body.access_token
    || (body.expires_in !== undefined && !Number.isFinite(body.expires_in))) {
    throw new Error('The Identity Broker authorization-code exchange failed.');
  }
  return validateP1BrokerToken(body.access_token, {
    issuer: request.issuer,
    audience: request.audience,
    actorId: request.actorId,
    projectId: request.projectId,
    runId: request.runId,
    requiredScopes: request.scopes.filter(scope => !['openid', 'profile', 'email', 'offline_access'].includes(scope)),
  });
}

async function authorizeP1InContext({
  context,
  environment,
  actorId,
  projectId,
  runId,
  request,
  signal,
  io,
  timeoutMs,
  fetchImpl,
}) {
  const page = context.pages()[0] ?? await context.newPage();
  try {
      page.setDefaultTimeout?.(timeoutMs);
      page.setDefaultNavigationTimeout?.(timeoutMs);
      const callback = deferred();
      const prompt = deferred();
      let callbackObserved = false;

      await page.route(candidate => {
        try {
          const target = new URL(request.redirectUri);
          const url = new URL(candidate);
          return url.origin === target.origin && url.pathname === target.pathname;
        } catch {
          return false;
        }
      }, async route => {
        try {
          const code = callbackCode(route.request().url(), request);
          callbackObserved = true;
          callback.resolve(code);
        } catch (error) {
          callbackObserved = true;
          callback.reject(error);
        }
        await route.abort().catch(() => {});
      });

      await page.route(candidate => isAuthorizeUrl(candidate, request), async route => {
        try {
          const response = await route.fetch({ maxRedirects: 0 });
          const contentType = response.headers()['content-type'] ?? '';
          if (response.status() === 200 && contentType.includes('application/json')) {
            let payload;
            try {
              payload = await response.json();
            } catch {
              throw new Error('The Identity Broker returned an invalid authorization response.');
            }
            if (payload?.consent_required === true) {
              prompt.resolve(validateConsentPrompt(payload, request));
              await route.fulfill({
                status: 200,
                contentType: 'text/html; charset=utf-8',
                body: '<!doctype html><title>Agentweaver consent</title><p>Waiting for the consent prompt.</p>',
              });
              return;
            }
            if (payload?.error) {
              throw new Error('The Identity Broker authorization was not completed.');
            }
          } else if (!response.ok() && !(response.status() >= 300 && response.status() < 400)) {
            throw new Error('The Identity Broker authorization was not completed.');
          }
          await route.fulfill({ response });
        } catch {
          prompt.reject(new Error('The Identity Broker authorization was not completed.'));
          callback.reject(new Error('The Identity Broker authorization was not completed.'));
          await route.fulfill({
            status: 500,
            contentType: 'text/html; charset=utf-8',
            body: '<!doctype html><title>Sign-in unavailable</title><p>Sign-in could not be completed.</p>',
          }).catch(() => {});
        }
      });

      io.log?.('Complete the requested Broker authorization in the headed Chrome window. Review the scopes before approving consent.');
      await page.goto(request.authorizeUrl, {
        waitUntil: 'domcontentloaded',
        timeout: timeoutMs,
      }).catch(() => {
        if (!callbackObserved) throw new Error('The Identity Broker sign-in could not be started.');
      });

      let code;
      const initial = await raceWithAbort(Promise.race([
        callback.promise.then(value => ({ kind: 'code', value })),
        prompt.promise.then(value => ({ kind: 'consent', value })),
      ]), signal, timeoutMs);
      if (initial.kind === 'code') {
        code = initial.value;
      } else {
        const decision = await renderConsentPrompt(page, initial.value, request);
        if (!decision) throw new Error('Broker consent was declined.');
        code = await raceWithAbort(callback.promise, signal, timeoutMs);
      }
      if (signal?.aborted) throw new Error('P1 sign-in was cancelled.');
      return await exchangeCode({ ...request, issuer: environment.issuer, audience: environment.audience, actorId }, code, fetchImpl);
  } finally {
    await page.close().catch(() => {});
  }
}

/**
 * Complete a public-client flow in the caller's fresh Chrome context. Callers may
 * reuse that context for setup and run-bound tokens so one user login supplies both.
 * The registered callback route is aborted before the page can consume its code.
 */
export async function authorizeP1InBrowser({
  chromium,
  context: existingContext,
  environment,
  actorId = environment.actorId,
  projectId,
  runId,
  scopes,
  signal,
  io = console,
  authRoot = P1_SIGNIN_PROFILE_ROOT,
  timeoutMs = DEFAULT_TIMEOUT_MS,
  executablePath,
  fetchImpl = globalThis.fetch,
  makeUserDataDir = createFreshChromeUserDataDir,
  makeRequest = createP1AuthorizationRequest,
} = {}) {
  const request = makeRequest({
    issuer: environment.issuer,
    scopes,
    projectId,
    runId,
  });
  const signIn = context => authorizeP1InContext({
    context,
    environment,
    actorId,
    projectId,
    runId,
    request,
    signal,
    io,
    timeoutMs,
    fetchImpl,
  });
  try {
    if (existingContext) return await signIn(existingContext);
    return await withFreshP1BrowserContext({
      chromium,
      authRoot,
      signal,
      timeoutMs,
      executablePath,
      makeUserDataDir,
    }, signIn);
  } catch {
    throw new Error('P1 sign-in could not be completed. No token values were written or displayed.');
  }
}

/** Read only the P1 manifest identity needed to request the setup and fixture tokens. */
export async function readP1SignInInput(argv) {
  const inputIndex = argv.indexOf('--input');
  if (inputIndex < 0 || !argv[inputIndex + 1] || argv[inputIndex + 1].startsWith('--')) {
    throw new Error('A P1 input manifest is required before sign-in.');
  }
  let input;
  try {
    input = JSON.parse(await readFile(path.resolve(argv[inputIndex + 1]), 'utf8'));
    return validateP1RunnerPlanInput(input);
  } catch {
    throw new Error('P1 sign-in input is invalid; check the manifest before retrying.');
  }
}
