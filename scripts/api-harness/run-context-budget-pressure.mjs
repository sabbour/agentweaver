#!/usr/bin/env node

import { pathToFileURL } from 'node:url';

import { AgentweaverClient } from './lib/client.mjs';
import { createRecorderSessionAuthProvider } from './lib/auth-providers/recorder-session.mjs';
import {
  contextBudgetPressureProfile,
  runContextBudgetPressure,
} from './lib/context-budget-pressure.mjs';
import { withContextBudgetProfile } from '../azure/lib/context-budget-profile.mjs';
import { redact } from '../harness-shared/redaction.mjs';

function args(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 1) {
    const key = argv[index];
    if (!key.startsWith('--')) throw new Error(`Unexpected argument ${key}.`);
    result[key.slice(2)] = argv[++index];
  }
  return result;
}

export async function main(argv = process.argv.slice(2), processImpl = process) {
  const parsed = args(argv);
  const target = parsed.target;
  if (!target) throw new Error('--target is required.');
  const abort = new AbortController();
  const requestCancellation = (signal) => {
    if (!abort.signal.aborted) abort.abort(new Error(`Context-budget acceptance cancelled by ${signal}.`));
  };
  const onSigint = () => requestCancellation('SIGINT');
  const onSigterm = () => requestCancellation('SIGTERM');
  processImpl.once('SIGINT', onSigint);
  processImpl.once('SIGTERM', onSigterm);
  try {
    const authProvider = createRecorderSessionAuthProvider({
      baseUrl: target,
      authRoot: parsed['recorder-auth-root'],
    });
    const client = new AgentweaverClient({ baseUrl: target, authProvider });
    const version = await client.get('/api/version', { authenticated: false, signal: abort.signal });
    if (!version.ok || version.responseBody?.isRelease !== false) {
      throw new Error('Context-budget acceptance requires a reachable non-release target reporting isRelease=false.');
    }
    const result = await withContextBudgetProfile({
      target,
      namespace: parsed.namespace,
      kubeContext: parsed['kube-context'],
      confirmNonProduction: parsed['confirm-non-production'],
      nonProductionVerified: true,
      ...contextBudgetPressureProfile,
      signal: abort.signal,
    }, async () => runContextBudgetPressure(client, {
      signal: abort.signal,
      timeoutMs: Number(parsed['timeout-seconds'] ?? 120) * 1000,
    }));
    processImpl.stdout.write(`${JSON.stringify(redact(result), null, 2)}\n`);
    return 0;
  } finally {
    processImpl.removeListener('SIGINT', onSigint);
    processImpl.removeListener('SIGTERM', onSigterm);
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(`CONTEXT-BUDGET ACCEPTANCE FAIL: ${redact(error.message)}`);
    for (const cleanupError of error.cleanupErrors ?? []) {
      console.error(`CLEANUP FAIL: ${redact(cleanupError)}`);
    }
    process.exitCode = 1;
  });
}
