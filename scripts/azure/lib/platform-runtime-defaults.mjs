import { readFileSync } from 'node:fs';
import path from 'node:path';
import {
  authorizeP1InBrowser,
  P1_SETUP_SCOPES,
  validateP1BrokerToken,
} from '../../harness-shared/p1-signin.mjs';

const DEFAULT_INPUT = 'deploy/k8s/runtime-configs/platform-runtime-defaults.json';
const REQUIRED_SCOPES = P1_SETUP_SCOPES.filter(scope =>
  !['openid', 'profile', 'email', 'offline_access'].includes(scope));
export const PLATFORM_RUNTIME_DEFAULTS_USAGE = `Usage: npm run platform:runtime-defaults -- --base-url <gateway-url> --issuer <https-issuer> --audience <token-audience> [options]

Without --execute the script signs in, reads current defaults, and compares them with the reviewed input.

  --base-url <url>      Gateway origin for the deployed API. Required.
  --issuer <url>        Identity Broker issuer. Required.
  --audience <value>    Public API token audience. Required.
  --input <file>        Reviewed defaults JSON. Default: ${DEFAULT_INPUT}
  --execute             Append the reviewed defaults using the current revision, then verify exact readback.
  --help                Show this help.

The script uses the P1 browser sign-in flow with P1_SETUP_SCOPES. Tokens are never written or displayed.`;

function fail(message) {
  throw new Error(`Platform runtime defaults: ${message}`);
}

function validateHttpsUrl(value, name, { allowPath = true } = {}) {
  if (typeof value !== 'string' || value.trim() !== value || !value) {
    fail(`${name} must be an absolute HTTPS URL.`);
  }
  let url;
  try {
    url = new URL(value);
  } catch {
    fail(`${name} must be an absolute HTTPS URL.`);
  }
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash ||
      (!allowPath && url.pathname !== '/')) {
    fail(`${name} must be an absolute HTTPS URL without credentials, query, or fragment.`);
  }
  return url.toString();
}

export function parsePlatformRuntimeDefaultsArgs(argv = []) {
  const options = {
    baseUrl: undefined,
    issuer: undefined,
    audience: undefined,
    inputPath: DEFAULT_INPUT,
    execute: false,
    help: false,
  };
  const valued = new Map([
    ['--base-url', 'baseUrl'],
    ['--issuer', 'issuer'],
    ['--audience', 'audience'],
    ['--input', 'inputPath'],
  ]);
  const seen = new Set();
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') {
      options.help = true;
      continue;
    }
    if (arg === '--execute') {
      if (options.execute) fail('the --execute option can only be used once.');
      options.execute = true;
      continue;
    }
    const separator = arg.indexOf('=');
    const name = separator < 0 ? arg : arg.slice(0, separator);
    if (!valued.has(name)) fail(`unknown argument "${arg}".`);
    if (seen.has(name)) fail(`${name} can only be used once.`);
    seen.add(name);
    const value = separator < 0 ? argv[++index] : arg.slice(separator + 1);
    if (typeof value !== 'string' || !value.trim() || value.startsWith('--') || value.trim() !== value) {
      fail(`${name} requires a value without surrounding whitespace.`);
    }
    options[valued.get(name)] = value;
  }
  if (options.help) return options;
  options.baseUrl = validateHttpsUrl(options.baseUrl, '--base-url');
  options.issuer = validateHttpsUrl(options.issuer, '--issuer', { allowPath: false });
  if (typeof options.audience !== 'string' || !options.audience.trim() ||
      options.audience.trim() !== options.audience) {
    fail('--audience is required and must not contain surrounding whitespace.');
  }
  return options;
}

function canonicalJson(value) {
  if (Array.isArray(value)) return value.map(canonicalJson);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalJson(value[key])]));
  }
  return value;
}

function sameJson(left, right) {
  return JSON.stringify(canonicalJson(left)) === JSON.stringify(canonicalJson(right));
}

function parseDefaultsJson(text) {
  let defaults;
  try {
    defaults = JSON.parse(text);
  } catch {
    fail('the reviewed input file is not valid JSON.');
  }
  if (!defaults || typeof defaults !== 'object' || Array.isArray(defaults)) {
    fail('the reviewed input file must contain a platform defaults object.');
  }
  return defaults;
}

function requireVersionedDefaults(response, description) {
  if (!response || !Number.isSafeInteger(response.revision) || response.revision < 0 ||
      !(response.defaults === null || (response.defaults && typeof response.defaults === 'object' &&
        !Array.isArray(response.defaults)))) {
    fail(`${description} returned an invalid platform defaults contract.`);
  }
  return response;
}

async function readJsonResponse(fetchImpl, url, token, description, options = {}) {
  let response;
  try {
    response = await fetchImpl(url, {
      ...options,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...options.headers,
      },
    });
  } catch {
    fail(`${description} request could not reach the Gateway.`);
  }
  if (!response.ok) {
    fail(`${description} request returned HTTP ${response.status}.`);
  }
  try {
    return await response.json();
  } catch {
    fail(`${description} returned invalid JSON.`);
  }
}

function defaultDependencies() {
  return {
    readFile: readFileSync,
    fetch: globalThis.fetch,
    signIn: async ({ issuer, audience, io }) => {
      const { chromium } = await import('playwright');
      const token = await authorizeP1InBrowser({
        chromium,
        environment: { issuer, audience },
        scopes: P1_SETUP_SCOPES,
        io,
      });
      // Require the admin API scopes and the exact authority, without exposing the token value.
      validateP1BrokerToken(token, { issuer, audience, requiredScopes: REQUIRED_SCOPES });
      return token;
    },
    log: message => console.log(message),
    io: console,
  };
}

export async function runPlatformRuntimeDefaults(options, overrides = {}) {
  const deps = { ...defaultDependencies(), ...overrides };
  const root = path.resolve(deps.root ?? process.cwd());
  const inputPath = path.resolve(root, options.inputPath ?? DEFAULT_INPUT);
  const expected = parseDefaultsJson(deps.readFile(inputPath, 'utf8'));
  const token = await deps.signIn({
    baseUrl: options.baseUrl,
    issuer: options.issuer,
    audience: options.audience,
    io: deps.io,
  });
  // Validate even injected sign-in adapters so no request can use an unbound or under-scoped token.
  validateP1BrokerToken(token, { issuer: options.issuer, audience: options.audience, requiredScopes: REQUIRED_SCOPES });

  const endpoint = new URL('/api/v1/platform/runtime-defaults', options.baseUrl).toString();
  const current = requireVersionedDefaults(
    await readJsonResponse(deps.fetch, endpoint, token, 'Platform runtime defaults GET'),
    'Platform runtime defaults GET');
  const currentMatches = current.defaults !== null && sameJson(current.defaults, expected);
  if (!options.execute) {
    if (currentMatches) {
      deps.log(`Platform runtime defaults match the reviewed input at revision ${current.revision}.`);
      return { result: 'exact', revision: current.revision, exitCode: 0 };
    }
    deps.log(`Platform runtime defaults differ from the reviewed input at revision ${current.revision}.`);
    return { result: 'differs', revision: current.revision, exitCode: 2 };
  }

  const update = requireVersionedDefaults(
    await readJsonResponse(deps.fetch, endpoint, token, 'Platform runtime defaults PUT', {
      method: 'PUT',
      body: JSON.stringify({ expectedRevision: current.revision, defaults: expected }),
    }),
    'Platform runtime defaults PUT');
  if (update.revision !== current.revision + 1) {
    fail('the PUT response did not append exactly one platform revision.');
  }
  const readback = requireVersionedDefaults(
    await readJsonResponse(deps.fetch, endpoint, token, 'Platform runtime defaults readback'),
    'Platform runtime defaults readback');
  // The revision fence detects races; the canonical comparison checks the complete server-normalized payload.
  if (readback.revision !== update.revision || !sameJson(readback.defaults, expected)) {
    fail('the platform defaults readback does not exactly match the reviewed input and appended revision.');
  }
  deps.log(`Platform runtime defaults were written and verified at revision ${readback.revision}.`);
  return { result: 'exact', revision: readback.revision, exitCode: 0 };
}
