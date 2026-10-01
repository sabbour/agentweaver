import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  loadSessionStorageSeed,
} from '../../../ui-harness/lib/auth.mjs';
import { decodeJwtExpiry, getSessionToken, SessionTokenExpiredError } from '../../../demo-recording/lib/auth.mjs';

export const RECORDER_SESSION_AUTH_PROVIDER = 'recorder-session';
const HERE = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_RECORDER_AUTH_ROOT = path.resolve(HERE, '../../../demo-recording/.auth');

async function loadRecorderStorageState(storageStatePath) {
  const parsed = JSON.parse(await readFile(storageStatePath, 'utf8'));
  if (!Array.isArray(parsed.cookies) || !Array.isArray(parsed.origins)) {
    throw new Error('stored recorder session has an invalid Playwright storageState shape');
  }
  return parsed;
}

export function uiHarnessAuthPaths(authRoot) {
  const root = authRoot ? path.resolve(authRoot) : DEFAULT_RECORDER_AUTH_ROOT;
  const recorderPath = path.join(root, 'recording.storageState.json');
  const uiPath = path.join(root, 'staging.storageState.json');
  const useUiCache = Boolean(authRoot) && !existsSync(recorderPath) && existsSync(uiPath);
  const storageStatePath = useUiCache ? uiPath : recorderPath;
  return {
    storageStatePath,
    sessionStoragePath: `${storageStatePath}.sessionStorage.json`,
    refreshCommand: useUiCache
      ? 'node scripts/ui-harness/login-chrome-default.mjs'
      : 'npm run demo:record -- open',
  };
}

export function createRecorderSessionAuthProvider({
  authRoot,
  baseUrl,
  uiHarnessAuthPathsFn = uiHarnessAuthPaths,
  loadStorageStateFn = loadRecorderStorageState,
  loadSessionStorageSeedFn = loadSessionStorageSeed,
  getSessionTokenFn = getSessionToken,
  now = Date.now,
} = {}) {
  const { storageStatePath, sessionStoragePath, refreshCommand } = uiHarnessAuthPathsFn(authRoot);
  let authorization;
  let expiresAt = null;

  return {
    name: RECORDER_SESSION_AUTH_PROVIDER,
    async getAuthorization() {
      if (authorization && (expiresAt === null || now() < expiresAt)) return authorization;
      authorization = undefined;
      if (typeof baseUrl !== 'string' || !baseUrl.trim()) {
        throw new Error('A target base URL is required to use cached UI-harness authentication.');
      }
      let expectedOrigin;
      try {
        expectedOrigin = new URL(baseUrl).origin;
      } catch {
        throw new Error('The target base URL is invalid; cached UI-harness authentication cannot be selected.');
      }

      try {
        await loadStorageStateFn(storageStatePath);
        const seed = await loadSessionStorageSeedFn(storageStatePath);
        if (!seed) {
          throw new Error('the selected cached session is missing its sessionStorage seed');
        }
        if (seed?.origin !== expectedOrigin) {
          throw new Error('the cached UI-harness session belongs to a different target origin');
        }
        const token = await getSessionTokenFn(sessionStoragePath);
        if (typeof token !== 'string' || token.length === 0) {
          throw new Error('the cached UI-harness session does not contain an Agentweaver session token');
        }
        const expiry = decodeJwtExpiry(token);
        expiresAt = expiry?.getTime() ?? null;
        if (expiresAt !== null && now() >= expiresAt) {
          throw new SessionTokenExpiredError('the selected cached session token has expired', { expiresAt: expiry });
        }
        authorization = `Bearer ${token}`;
        return authorization;
      } catch (error) {
        const reason = error instanceof SessionTokenExpiredError
          ? 'the selected cached session token has expired'
          : error.message;
        throw new Error(
          `Cached UI-harness authentication is unavailable or expired (${reason}). `
          + `Refresh the selected session with ${refreshCommand ?? 'npm run demo:record -- open'} --base-url ${expectedOrigin}, then retry.`,
        );
      }
    },
  };
}
