import { existsSync } from 'node:fs';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
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
  const href = String(url);
  let pathname = href;
  try {
    pathname = new URL(href).pathname;
  } catch {
    // Treat non-URL inputs as plain paths and fall through to the regex checks.
  }
  return status === 401
    || status === 403
    || /\/(login|signin|oauth)(?:[/?#]|$)/i.test(pathname)
    || /^\/auth\/[^/]+\/(?:authorize|callback)(?:[/?#]|$)/i.test(pathname);
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
  return parsed;
}

export async function loadStorageStateForOrigin(statePath, origin) {
  const parsed = await loadStorageState(statePath);
  const target = new URL(origin);
  const hostname = target.hostname.toLowerCase();
  return {
    cookies: parsed.cookies.filter((cookie) =>
      String(cookie.domain ?? '').toLowerCase().replace(/^\./, '') === hostname),
    origins: parsed.origins.filter((item) => item.origin === target.origin),
  };
}

export async function ensureAuthDirectory() {
  await mkdir(AUTH_DIR, { recursive: true });
  return AUTH_DIR;
}

// Agentweaver's session token lives in `sessionStorage`, not cookies or `localStorage`
// (see apps/web/src/config.ts getSessionToken/setSessionAuth). Playwright's
// `context.storageState()` only persists cookies + localStorage — sessionStorage is
// intentionally excluded from that API (it's per-tab, not per-context). A companion
// seed file captures it separately so a headless replay can restore the real session.
function sessionStorageSeedPath(statePath) {
  return `${statePath}.sessionStorage.json`;
}

/** Capture the page's live `sessionStorage` (all origins currently loaded) alongside
 * the Playwright storageState, since storageState() cannot see it. Call this right
 * after a successful login, while the authenticated page is still open. */
export async function saveSessionStorageSeed(page, statePath = DEFAULT_STORAGE_STATE) {
  const origin = await page.evaluate(() => window.location.origin);
  const entries = await page.evaluate(() => ({ ...window.sessionStorage }));
  await writeFile(sessionStorageSeedPath(statePath), JSON.stringify({ origin, entries }, null, 2), 'utf8');
}

/** Load a previously captured sessionStorage seed, if one exists for this storageState.
 * Returns null (not an error) when absent — sessionStorage seeding is best-effort;
 * a missing seed just means cookie/localStorage-only auth (or an older capture). */
export async function loadSessionStorageSeed(statePath = DEFAULT_STORAGE_STATE) {
  const seedPath = sessionStorageSeedPath(statePath);
  if (!existsSync(seedPath)) return null;
  const parsed = JSON.parse(await readFile(seedPath, 'utf8'));
  if (!parsed.origin || typeof parsed.entries !== 'object' || parsed.entries === null) return null;
  if (Object.keys(parsed.entries).length === 0) return null;
  return parsed;
}
