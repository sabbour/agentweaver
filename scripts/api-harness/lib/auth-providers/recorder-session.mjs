import path from 'node:path';
import {
  DEFAULT_STORAGE_STATE,
  loadSessionStorageSeed,
  loadStorageState,
} from '../../../ui-harness/lib/auth.mjs';

export const RECORDER_SESSION_AUTH_PROVIDER = 'recorder-session';

export function uiHarnessAuthPaths(authRoot) {
  const storageStatePath = authRoot
    ? path.join(path.resolve(authRoot), 'staging.storageState.json')
    : DEFAULT_STORAGE_STATE;
  return {
    storageStatePath,
    sessionStoragePath: `${storageStatePath}.sessionStorage.json`,
  };
}

export function createRecorderSessionAuthProvider({
  authRoot,
  baseUrl,
  uiHarnessAuthPathsFn = uiHarnessAuthPaths,
  loadStorageStateFn = loadStorageState,
  loadSessionStorageSeedFn = loadSessionStorageSeed,
} = {}) {
  const { storageStatePath } = uiHarnessAuthPathsFn(authRoot);
  let authorization;

  return {
    name: RECORDER_SESSION_AUTH_PROVIDER,
    async getAuthorization() {
      if (authorization) return authorization;
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
        if (seed?.origin !== expectedOrigin) {
          throw new Error('the cached UI-harness session belongs to a different target origin');
        }
        const token = seed.entries?.['agentweaver.sessionToken'];
        if (typeof token !== 'string' || token.length === 0) {
          throw new Error('the cached UI-harness session does not contain an Agentweaver session token');
        }
        authorization = `Bearer ${token}`;
        return authorization;
      } catch (error) {
        throw new Error(
          `Cached UI-harness authentication is unavailable or expired (${error.message}). `
          + `Close Chrome and run node scripts/ui-harness/login-chrome-default.mjs --base-url ${expectedOrigin}, then retry.`,
        );
      }
    },
  };
}
