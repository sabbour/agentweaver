import { existsSync } from 'node:fs';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const AUTH_DIR = path.join(HERE, '..', '.auth');
export const DEFAULT_STORAGE_STATE = path.join(AUTH_DIR, 'staging.storageState.json');

function authExpired(message) {
  const error = new Error(`AUTH_EXPIRED: ${message}`);
  error.code = 'AUTH_EXPIRED';
  return error;
}

export function isAuthExpired({ url = '', status = null } = {}) {
  return status === 401 || status === 403 || /\/(login|signin|oauth)(?:[/?#]|$)/i.test(String(url));
}

export async function loadStorageState(statePath = DEFAULT_STORAGE_STATE) {
  if (!existsSync(statePath)) {
    throw authExpired(`no stored browser session at ${statePath}; run the login command`);
  }
  const parsed = JSON.parse(await readFile(statePath, 'utf8'));
  if (!Array.isArray(parsed.cookies) || !Array.isArray(parsed.origins)) {
    throw authExpired('stored browser session has an invalid Playwright storageState shape');
  }
  if (parsed.cookies.length === 0 && parsed.origins.length === 0) {
    throw authExpired('stored browser session is empty; run the login command');
  }
  return statePath;
}

export async function ensureAuthDirectory() {
  await mkdir(AUTH_DIR, { recursive: true });
  return AUTH_DIR;
}
