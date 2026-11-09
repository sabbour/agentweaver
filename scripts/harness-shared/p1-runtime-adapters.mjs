import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { createBrokerAuthProvider } from './broker-auth.mjs';
import { validateNetworkTarget } from './target-guard.mjs';
import { AgentweaverClient } from '../api-harness/lib/client.mjs';
import { McpHarnessClient } from '../mcp-harness/mcp-client/client.mjs';
import { UiHarnessClient } from '../ui-harness/lib/client.mjs';

/** Adapt injected, already-authorized credentials and browser pages to the three P1 harness surfaces. */
/** Decode only the payload claims needed to bind a broker token to its authorized identity. */
function decodeClaims(token) {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) throw new Error('Invalid JWT shape.');
    return JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
  } catch {
    throw new Error('The authorized identity provider returned an invalid broker token.');
  }
}

/** Reject tokens that are missing, expired by shape, or bound to a different actor or selected run. */
function requireToken(token, identity, binding) {
  if (typeof token !== 'string' || !token) {
    throw new Error('The authorized identity provider did not return a broker token.');
  }
  const claims = decodeClaims(token);
  const audiences = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
  if (claims.iss !== identity.issuer || claims.sub !== identity.actorId
    || !audiences.includes(identity.audience) || !Number.isFinite(claims.exp)
    || binding && (claims.project_id !== binding.projectId || claims.run_id !== binding.runId)) {
    throw new Error('The broker token does not match the exact authorized actor and selected run.');
  }
  return token;
}

/** Load an injected provider module and require its default async provider function. */
async function loadModule(path, label, importModule) {
  if (typeof path !== 'string' || !path.trim()) throw new Error(`A ${label} module path is required.`);
  let loaded;
  try {
    loaded = await importModule(pathToFileURL(resolve(path)).href);
  } catch {
    throw new Error(`The ${label} module could not be loaded.`);
  }
  if (typeof loaded.default !== 'function') {
    throw new Error(`The ${label} module must export a default async function.`);
  }
  return loaded.default;
}

/** Create the setup-scoped Gateway client from the selected environment and setup token. */
export function createP1SetupApi({ environment, setupToken, fetchImpl = globalThis.fetch }) {
  const target = validateNetworkTarget(environment.baseUrl, { exactPath: '/' });
  const authProvider = createBrokerAuthProvider({
    target,
    token: setupToken,
    issuer: environment.issuer,
    audience: environment.audience,
    actorId: environment.actorId,
  });
  return new AgentweaverClient({
    baseUrl: environment.baseUrl,
    authProvider,
    tenantId: environment.tenantId,
    fetchImpl,
  });
}

/**
 * Load an already-authorized identity provider and acquire credentials pinned to one project and run.
 * The returned factory creates the run-bound API client without handling login.
 */
export async function acquireP1RunIdentity({
  modulePath,
  environment,
  projectId,
  runId,
  provideIdentity,
  importModule = specifier => import(specifier),
  fetchImpl = globalThis.fetch,
}) {
  const identityFunction = provideIdentity
    ?? await loadModule(modulePath, 'already-authorized identity provider', importModule);
  const identity = {
    actorId: environment.actorId,
    tenantId: environment.tenantId,
    issuer: environment.issuer,
    audience: environment.audience,
  };
  let credentials;
  try {
    credentials = await identityFunction({
      baseUrl: environment.baseUrl,
      ...identity,
      projectId,
      runId,
    });
  } catch {
    throw new Error('The already-authorized identity provider did not return run credentials.');
  }
  if (!credentials || typeof credentials !== 'object' || Array.isArray(credentials)
    || Object.keys(credentials).sort().join(',') !== 'token') {
    throw new Error('The identity provider must return only the run token.');
  }
  const token = requireToken(credentials.token, identity, { projectId, runId });
  return {
    token,
    projectId,
    runId,
    createClients() {
      const base = validateNetworkTarget(environment.baseUrl, { exactPath: '/' });
      const authProvider = createBrokerAuthProvider({ target: base, token, ...identity });
      return { api: new AgentweaverClient({
        baseUrl: environment.baseUrl, authProvider, tenantId: environment.tenantId, fetchImpl,
      }) };
    },
  };
}

/**
 * Build only the adapters required by the selected P1 surface from injected, already-authorized inputs.
 * MCP connections are opened here; UI page creation is delegated to the supplied page provider.
 */
export async function createP1SurfaceAdapters({
  surface,
  environment,
  identity,
  pageModulePath,
  pageProvider,
  importModule = specifier => import(specifier),
  fetchImpl = globalThis.fetch,
}) {
  const { api } = identity.api
    ? identity
    : identity.createClients();
  let mcp;
  let ui;
  if (surface === 'mcp') {
    const target = new URL('/mcp', environment.baseUrl);
    const authProvider = createBrokerAuthProvider({
      target,
      token: identity.token,
      issuer: environment.issuer,
      audience: environment.audience,
      actorId: environment.actorId,
    });
    mcp = await McpHarnessClient.connect({
      target,
      authProvider,
      tenantId: environment.tenantId,
      fetchImpl,
    });
  }
  if (surface === 'ui') {
    // Prefer the operator launcher's in-memory provider; module loading is only for advanced runs.
    if (pageProvider !== undefined && typeof pageProvider !== 'function') {
      throw new Error('The authorized UI page provider must be a function.');
    }
    const providePage = pageProvider
      ?? await loadModule(pageModulePath, 'already-authorized page provider', importModule);
    let page;
    try {
      page = await providePage({
        baseUrl: environment.baseUrl,
        actorId: environment.actorId,
        tenantId: environment.tenantId,
        issuer: environment.issuer,
        audience: environment.audience,
        projectId: identity.projectId,
        runId: identity.runId,
      });
    } catch {
      throw new Error('The already-authorized UI page provider did not return a page.');
    }
    const gatewayUrl = new URL('/api/v1', environment.baseUrl);
    ui = new UiHarnessClient({
      page,
      baseUrl: environment.baseUrl,
      gatewayUrl,
      tenantId: environment.tenantId,
      issuer: environment.issuer,
      audience: environment.audience,
      actorId: environment.actorId,
    });
  }
  return { api, mcp, ui };
}
