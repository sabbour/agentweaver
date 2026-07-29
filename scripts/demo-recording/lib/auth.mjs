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

export async function getSessionToken(sessionStoragePath = DEFAULT_SESSION_STORAGE_PATH) {
  const seed = await loadSessionSeed(sessionStoragePath);
  const token = seed?.entries?.['agentweaver.sessionToken'];
  if (!token) throw new Error(`Missing agentweaver.sessionToken in ${sessionStoragePath}`);
  return token;
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
