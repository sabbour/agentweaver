import { existsSync } from 'node:fs';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const AUTH_DIR = path.join(HERE, '..', '.auth');
export const DEFAULT_STORAGE_STATE = path.join(AUTH_DIR, 'staging.storageState.json');

export function isAuthExpired({ url = '', status = null } = {}) {
  return status === 401 || status === 403 || /\/(login|signin|oauth)(?:[/?#]|$)/i.test(String(url));
}

export async function loadStorageState(statePath = DEFAULT_STORAGE_STATE) {
  if (!existsSync(statePath)) {
    const error = new Error(`AUTH_EXPIRED: no stored browser session at ${statePath}; run the login command`);
    error.code = 'AUTH_EXPIRED';
    throw error;
  }
  const parsed = JSON.parse(await readFile(statePath, 'utf8'));
  if (!Array.isArray(parsed.cookies) || !Array.isArray(parsed.origins)) {
    throw new Error('AUTH_EXPIRED: stored browser session has an invalid Playwright storageState shape');
  }
  return statePath;
}

export async function ensureAuthDirectory() {
  await mkdir(AUTH_DIR, { recursive: true });
  return AUTH_DIR;
}
