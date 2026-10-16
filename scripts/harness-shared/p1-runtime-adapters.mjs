import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { createBrokerAuthProvider } from './broker-auth.mjs';
import { validateNetworkTarget } from './target-guard.mjs';
import { AgentweaverClient } from '../api-harness/lib/client.mjs';
import { McpHarnessClient } from '../mcp-harness/mcp-client/client.mjs';
import { UiHarnessClient } from '../ui-harness/lib/client.mjs';

function decodeClaims(token) {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) throw new Error('Invalid JWT shape.');
    return JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
  } catch {
    throw new Error('The authorized identity provider returned an invalid broker token.');
  }
}

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

export async function acquireP1RunIdentity({
  modulePath,
  environment,
  projectId,
  runId,
  importModule = specifier => import(specifier),
  fetchImpl = globalThis.fetch,
}) {
  const provideIdentity = await loadModule(modulePath, 'already-authorized identity provider', importModule);
  const identity = {
    actorId: environment.actorId,
    tenantId: environment.tenantId,
    issuer: environment.issuer,
    audience: environment.audience,
  };
  let credentials;
  try {
    credentials = await provideIdentity({
      baseUrl: environment.baseUrl,
      ...identity,
      projectId,
      runId,
    });
  } catch {
    throw new Error('The already-authorized identity provider did not return run credentials.');
  }
  if (!credentials || typeof credentials !== 'object' || Array.isArray(credentials)
    || Object.keys(credentials).sort().join(',') !== 'deniedToken,token') {
    throw new Error('The identity provider must return only the run token and denied-actor token.');
  }
  const token = requireToken(credentials.token, identity, { projectId, runId });
  const deniedActor = credentials.deniedActor;
  if (deniedActor !== undefined) {
    throw new Error('The denied actor identity must come from the runner plan, not the identity provider.');
  }
  return {
    token,
    deniedToken: credentials.deniedToken,
    projectId,
    runId,
    createClients({ deniedActor }) {
      const base = validateNetworkTarget(environment.baseUrl, { exactPath: '/' });
      const authProvider = createBrokerAuthProvider({ target: base, token, ...identity });
      const deniedIdentity = {
        issuer: environment.issuer,
        audience: environment.audience,
        actorId: deniedActor.actorId,
      };
      const deniedToken = requireToken(credentials.deniedToken, deniedIdentity, null);
      const deniedAuthProvider = createBrokerAuthProvider({
        target: base, token: deniedToken, ...deniedIdentity,
      });
      const api = new AgentweaverClient({
        baseUrl: environment.baseUrl, authProvider, tenantId: environment.tenantId, fetchImpl,
      });
      const deniedApi = new AgentweaverClient({
        baseUrl: environment.baseUrl, authProvider: deniedAuthProvider,
        tenantId: deniedActor.tenantId, fetchImpl,
      });
      return { api, deniedApi };
    },
  };
}

export async function createP1SurfaceAdapters({
  surface,
  environment,
  identity,
  deniedActor,
  pageModulePath,
  importModule = specifier => import(specifier),
  fetchImpl = globalThis.fetch,
}) {
  const { api, deniedApi } = identity.api && identity.deniedApi
    ? identity
    : identity.createClients({ deniedActor });
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
    const providePage = await loadModule(pageModulePath, 'already-authorized page provider', importModule);
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
  return { api, deniedApi, mcp, ui };
}
