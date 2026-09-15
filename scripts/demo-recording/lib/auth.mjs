import fs from 'node:fs/promises';

export const DEFAULT_STORAGE_STATE_PATH = 'scripts/demo-recording/.auth/recording.storageState.json';
export const DEFAULT_SESSION_STORAGE_PATH = 'scripts/demo-recording/.auth/recording.storageState.json.sessionStorage.json';
export const DEFAULT_ACCESS_TOKEN_REFRESH_MARGIN_MS = 15 * 60_000;

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

export function formatTokenRemaining(milliseconds) {
  if (!Number.isFinite(milliseconds)) return 'unknown';
  const sign = milliseconds < 0 ? '-' : '';
  let remainingSeconds = Math.max(0, Math.ceil(Math.abs(milliseconds) / 1_000));
  const days = Math.floor(remainingSeconds / 86_400);
  remainingSeconds -= days * 86_400;
  const hours = Math.floor(remainingSeconds / 3_600);
  remainingSeconds -= hours * 3_600;
  const minutes = Math.floor(remainingSeconds / 60);
  const seconds = remainingSeconds - minutes * 60;
  const parts = [];
  if (days) parts.push(`${days}d`);
  if (hours || days) parts.push(`${hours}h`);
  if (minutes || hours || days) parts.push(`${minutes}m`);
  if (parts.length === 0) parts.push(`${seconds}s`);
  return `${sign}${parts.slice(0, 3).join(' ')}`;
}

export function decodeJwtPayload(token) {
  if (typeof token !== 'string') throw new Error('Session token is not a string.');
  const parts = token.split('.');
  if (parts.length < 2 || !parts[1]) throw new Error('Session token is not a JWT with a decodable payload.');
  try {
    return JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
  } catch {
    throw new Error('Session token payload could not be decoded.');
  }
}

export function inspectToken(token, {
  now = () => Date.now(),
  minRemainingMs = DEFAULT_ACCESS_TOKEN_REFRESH_MARGIN_MS,
  operationBudgetMs = 0,
} = {}) {
  let payload;
  try {
    payload = decodeJwtPayload(token);
  } catch (error) {
    return {
      present: typeof token === 'string' && token.length > 0,
      validJwt: false,
      ready: false,
      expired: false,
      refreshRecommended: true,
      reason: error.message,
      remainingMs: null,
      remainingText: 'unknown',
      expiresAtMs: null,
      expiresAtIso: null,
      minRemainingMs,
      operationBudgetMs,
    };
  }

  const exp = payload?.exp;
  if (!Number.isFinite(exp)) {
    return {
      present: true,
      validJwt: true,
      ready: false,
      expired: false,
      refreshRecommended: true,
      reason: 'Session token does not contain a numeric exp claim.',
      remainingMs: null,
      remainingText: 'unknown',
      expiresAtMs: null,
      expiresAtIso: null,
      minRemainingMs,
      operationBudgetMs,
    };
  }

  const expiresAtMs = Math.trunc(exp * 1_000);
  const remainingMs = expiresAtMs - now();
  const requiredRemainingMs = minRemainingMs + Math.max(0, operationBudgetMs);
  const expired = remainingMs <= 0;
  const refreshRecommended = remainingMs <= requiredRemainingMs;
  return {
    present: true,
    validJwt: true,
    ready: !refreshRecommended,
    expired,
    refreshRecommended,
    reason: expired
      ? 'Access token has expired.'
      : (refreshRecommended ? 'Access token remaining lifetime is below the refresh safety margin.' : 'Access token has enough remaining lifetime.'),
    remainingMs,
    remainingText: formatTokenRemaining(remainingMs),
    expiresAtMs,
    expiresAtIso: new Date(expiresAtMs).toISOString(),
    minRemainingMs,
    operationBudgetMs,
  };
}

export async function inspectSessionToken(sessionStoragePath = DEFAULT_SESSION_STORAGE_PATH, options = {}) {
  try {
    const token = await getSessionToken(sessionStoragePath, { allowExpired: true });
    return inspectToken(token, options);
  } catch (error) {
    return {
      present: false,
      validJwt: false,
      ready: false,
      expired: false,
      refreshRecommended: true,
      reason: error.message,
      remainingMs: null,
      remainingText: 'unknown',
      expiresAtMs: null,
      expiresAtIso: null,
      minRemainingMs: options.minRemainingMs ?? DEFAULT_ACCESS_TOKEN_REFRESH_MARGIN_MS,
      operationBudgetMs: options.operationBudgetMs ?? 0,
    };
  }
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
