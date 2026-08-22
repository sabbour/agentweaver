// Deletes only explicitly declared demo fixtures at an inactive recording boundary.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApiFromSession } from './lib/api.mjs';
import { loadCaptureConfig } from './lib/capture-config.mjs';
import { cleanScenarioFixtures, validateScenarioFixture } from './lib/preflight.mjs';
import {
  DEFAULT_RECORDING_AUTH_ROOT,
  DEFAULT_RECORDING_BASE_URL,
  DEFAULT_RECORDING_SESSION,
  listPlaywrightSessions,
  recordingAuthPaths,
} from './lib/recording-session.mjs';

const CONFIRMATION_FLAG = '--confirm-demo-cleanup';

function requireOptionValue(argv, index, flag) {
  const value = argv[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`Expected a value after ${flag}.`);
  return value;
}

export function parseCleanupOptions(argv) {
  const options = {
    authRoot: DEFAULT_RECORDING_AUTH_ROOT,
    baseUrl: DEFAULT_RECORDING_BASE_URL,
    session: DEFAULT_RECORDING_SESSION,
    confirmed: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (flag === CONFIRMATION_FLAG) {
      options.confirmed = true;
      continue;
    }
    if (flag === '--plan' || flag === '--auth-root' || flag === '--base-url' || flag === '--session') {
      options[flag.slice(2).replace(/-([a-z])/g, (_match, letter) => letter.toUpperCase())] = requireOptionValue(argv, index, flag);
      index += 1;
      continue;
    }
    throw new Error(`Unknown cleanup option: ${flag}`);
  }

  if (!options.confirmed) {
    throw new Error(`Cleanup is destructive. Re-run with ${CONFIRMATION_FLAG} after verifying the inactive recording boundary.`);
  }
  if (!options.plan) throw new Error('Cleanup requires --plan so it can restrict deletion to that plan’s declared demo fixture.');
  if (!URL.canParse(options.baseUrl) || new URL(options.baseUrl).protocol !== 'https:') {
    throw new Error('--base-url must be an absolute HTTPS URL.');
  }
  if (!/^[a-zA-Z0-9_-]+$/.test(options.session)) {
    throw new Error('--session may contain only letters, numbers, underscores, and hyphens.');
  }
  return options;
}

export function assertPlanTargetsBaseUrl(captureConfig, baseUrl) {
  // Continuation beats (cross-beat URL continuity) legitimately omit startUrl.
  // Only beats that declare a startUrl are checked; at least one must exist.
  // Template placeholders (e.g. "{{AGENTWEAVER_DEMO_PROJECT_URL}}/board") are runtime-resolved
  // and cannot be validated statically — skip them, but require at least one absolute URL.
  const allStartUrls = captureConfig.beats
    .map((beat) => beat.startUrl)
    .filter((startUrl) => startUrl != null);
  const startUrls = allStartUrls.filter(
    (startUrl) => typeof startUrl === 'string' && !startUrl.startsWith('{{'),
  );
  if (startUrls.length === 0 || startUrls.some((startUrl) => !URL.canParse(startUrl))) {
    throw new Error('Cleanup refused: at least one beat must declare an absolute staging startUrl, and all declared startUrls must be valid.');
  }
  const origins = new Set(
    startUrls
      .map((startUrl) => new URL(startUrl).origin),
  );
  if (origins.size !== 1 || !origins.has(new URL(baseUrl).origin)) {
    throw new Error('Cleanup refused: the active plan must target exactly the configured staging base URL.');
  }
}

export async function cleanStaging(options, dependencies = {}) {
  if (!options.confirmed) {
    throw new Error(`Cleanup is destructive. Re-run with ${CONFIRMATION_FLAG} after verifying the inactive recording boundary.`);
  }
  if (!options.plan) throw new Error('Cleanup requires --plan so it can restrict deletion to that plan’s declared demo fixture.');

  const captureConfig = await (dependencies.loadCaptureConfig ?? loadCaptureConfig)(path.resolve(options.plan));
  assertPlanTargetsBaseUrl(captureConfig, options.baseUrl);
  const fixture = validateScenarioFixture(captureConfig.fixture);

  const sessions = (dependencies.listSessions ?? listPlaywrightSessions)();
  // playwright-cli does not expose session ownership, so every open session is unsafe.
  const openSessions = [...sessions.entries()]
    .filter(([, session]) => session.status === 'open')
    .map(([name]) => name);
  if (openSessions.length > 0) {
    throw new Error(`Cleanup refused: recorder session(s) ${openSessions.map((name) => `"${name}"`).join(', ')} are open. Close all sessions and verify no capture is active first.`);
  }

  const authPaths = recordingAuthPaths(options.authRoot);
  const api = dependencies.api ?? await (dependencies.createApiFromSession ?? createApiFromSession)({
    baseUrl: options.baseUrl,
    sessionStoragePath: authPaths.sessionStoragePath,
  });
  return (dependencies.cleanFixtures ?? cleanScenarioFixtures)({
    fixture,
    baseUrl: options.baseUrl,
    sessionStoragePath: authPaths.sessionStoragePath,
  }, { ...dependencies, api });
}

async function main() {
  const result = await cleanStaging(parseCleanupOptions(process.argv.slice(2)));
  process.stdout.write(
    `Removed ${result.discoveredProjectCount} declared demo fixture project(s) and ${result.discoveredSessionCount} session(s); verified none remain.\n`,
  );
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
