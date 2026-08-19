import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApiFromSession } from './lib/api.mjs';
import { loadCaptureConfig } from './lib/capture-config.mjs';
import { preflightFinalTake } from './lib/preflight.mjs';
import {
  DEFAULT_RECORDING_AUTH_ROOT,
  DEFAULT_RECORDING_BASE_URL,
  listPlaywrightSessions,
  recordingAuthPaths,
} from './lib/recording-session.mjs';

function optionValue(argv, index, flag) {
  const value = argv[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`Expected a value after ${flag}.`);
  return value;
}

export function parseFinalTakePreflightOptions(argv) {
  const options = {
    authRoot: DEFAULT_RECORDING_AUTH_ROOT,
    baseUrl: DEFAULT_RECORDING_BASE_URL,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (!['--plan', '--auth-root', '--base-url'].includes(flag)) {
      throw new Error(`Unknown final-take preflight option: ${flag}`);
    }
    options[flag.slice(2).replace(/-([a-z])/gu, (_match, letter) => letter.toUpperCase())] = optionValue(argv, index, flag);
    index += 1;
  }
  if (!options.plan) throw new Error('Final-take preflight requires --plan.');
  if (!URL.canParse(options.baseUrl) || new URL(options.baseUrl).protocol !== 'https:') {
    throw new Error('--base-url must be an absolute HTTPS URL.');
  }
  return options;
}

export async function runFinalTakePreflight(options, dependencies = {}) {
  const openSessions = [...(dependencies.listSessions ?? listPlaywrightSessions)().entries()]
    .filter(([, session]) => session.status === 'open')
    .map(([name]) => name);
  if (openSessions.length > 0) {
    throw new Error(`Final-take preflight refused: recorder session(s) ${openSessions.map((name) => `"${name}"`).join(', ')} are open.`);
  }

  const captureConfig = await (dependencies.loadCaptureConfig ?? loadCaptureConfig)(path.resolve(options.plan));
  const authPaths = recordingAuthPaths(options.authRoot);
  const api = dependencies.api ?? await (dependencies.createApiFromSession ?? createApiFromSession)({
    baseUrl: options.baseUrl,
    sessionStoragePath: authPaths.sessionStoragePath,
  });
  return preflightFinalTake(captureConfig, { ...dependencies, api });
}

async function main() {
  const result = await runFinalTakePreflight(parseFinalTakePreflightOptions(process.argv.slice(2)));
  process.stdout.write(`Final take "${result.finalTakeId}" is isolated and ready for ${result.plannedVideoCount} new capture(s).\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
