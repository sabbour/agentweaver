import fs from 'node:fs/promises';

export const DEFAULT_STORAGE_STATE_PATH = 'scripts/ui-harness/.auth/staging.storageState.json';
export const DEFAULT_SESSION_STORAGE_PATH = 'scripts/ui-harness/.auth/staging.storageState.json.sessionStorage.json';

export async function readJson(path) {
  return JSON.parse(await fs.readFile(path, 'utf8'));
}

export async function loadSessionSeed(sessionStoragePath = DEFAULT_SESSION_STORAGE_PATH) {
  return readJson(sessionStoragePath);
}

export async function loadStorageState(storageStatePath = DEFAULT_STORAGE_STATE_PATH) {
  return readJson(storageStatePath);
}

export async function getSessionToken(sessionStoragePath = DEFAULT_SESSION_STORAGE_PATH, { allowExpired = false } = {}) {
  const seed = await loadSessionSeed(sessionStoragePath);
  const token = seed?.entries?.['agentweaver.sessionToken'];
  if (!token) throw new Error(`Missing agentweaver.sessionToken in ${sessionStoragePath}`);
  if (!allowExpired) {
    const expiry = decodeJwtExpiry(token);
    // Only refuse when expiry is *provably* past. An undecodable or
    // exp-less token is left alone so this cannot break non-JWT fixtures.
    if (expiry && expiry.getTime() <= Date.now()) {
      const minutes = Math.round((Date.now() - expiry.getTime()) / 60_000);
      throw new SessionTokenExpiredError(
        `The cached recording session token expired ${minutes} minute(s) ago at ${expiry.toISOString()}. ` +
        'Every authenticated request would fail with 401. Refresh it with: npm run demo:record -- signin ' +
        '(this requires a human to close all Google Chrome windows first, so it cannot be completed unattended).',
        { expiresAt: expiry },
      );
    }
  }
  return token;
}

export class SessionTokenExpiredError extends Error {
  constructor(message, { expiresAt } = {}) {
    super(message);
    this.name = 'SessionTokenExpiredError';
    this.expiresAt = expiresAt ?? null;
  }
}

/**
 * Returns the `exp` claim of a JWT as a Date, or null when the token is not a
 * decodable JWT or carries no expiry. Never throws: callers use this to make a
 * status line honest, not to validate a signature.
 */
export function decodeJwtExpiry(token) {
  if (typeof token !== 'string') return null;
  const segments = token.split('.');
  if (segments.length < 2) return null;
  try {
    const payload = Buffer.from(segments[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8');
    const exp = JSON.parse(payload)?.exp;
    if (typeof exp !== 'number' || !Number.isFinite(exp)) return null;
    return new Date(exp * 1000);
  } catch {
    return null;
  }
}

/**
 * Describes the cached session token without throwing, so `status` can report
 * the truth instead of only whether a file exists.
 */
export async function getSessionTokenStatus(sessionStoragePath = DEFAULT_SESSION_STORAGE_PATH, { now = () => Date.now() } = {}) {
  let token;
  try {
    token = await getSessionToken(sessionStoragePath, { allowExpired: true });
  } catch {
    return { present: false, expiresAt: null, expired: false, minutesRemaining: null };
  }
  const expiresAt = decodeJwtExpiry(token);
  if (!expiresAt) {
    return { present: true, expiresAt: null, expired: false, minutesRemaining: null };
  }
  const minutesRemaining = Math.round((expiresAt.getTime() - now()) / 60_000);
  return {
    present: true,
    expiresAt,
    expired: expiresAt.getTime() <= now(),
    minutesRemaining,
  };
}

export function makeSeedScriptSource(seed, targetOrigin) {
  const origin = targetOrigin ?? seed.origin;
  const entriesJson = JSON.stringify(seed.entries);
  return [
    'async page => {',
    `  await page.goto(${JSON.stringify(origin)}, { waitUntil: 'domcontentloaded' });`,
    '  await page.evaluate((entries) => {',
    '    for (const [key, value] of Object.entries(entries)) {',
    '      window.sessionStorage.setItem(key, value);',
    '    }',
    `  }, ${entriesJson});`,
    '  return {',
    `    origin: ${JSON.stringify(origin)},`,
    `    keysSeeded: Object.keys(${entriesJson})`,
    '  };',
    '}',
  ].join('\n');
}

export async function writeSeedScript(outPath, options = {}) {
  const seed = await loadSessionSeed(options.sessionStoragePath);
  const source = makeSeedScriptSource(seed, options.targetOrigin);
  await fs.writeFile(outPath, source, 'utf8');
  return { outPath, origin: options.targetOrigin ?? seed.origin };
}
