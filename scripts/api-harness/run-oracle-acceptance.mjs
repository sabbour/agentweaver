#!/usr/bin/env node
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { AgentweaverClient } from './lib/client.mjs';
import { createRecorderSessionAuthProvider } from './lib/auth-providers/recorder-session.mjs';
import { createLocalTestAuthProvider } from './lib/auth-providers/local-test.mjs';
import { DEFAULT_BUDGETS, createAcceptanceTransport, runOracleAcceptance } from './lib/oracle-acceptance.mjs';
import { validateNetworkTarget } from '../harness-shared/target-guard.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export function parseOracleArgs(argv) {
  const values = {};
  const keys = new Map([
    ['--target', 'target'], ['--project-id', 'projectId'], ['--run-id', 'runId'], ['--goal', 'goal'],
    ['--workflow-id', 'workflowId'], ['--expected-text', 'expectedText'],
    ['--corrected-text', 'correctedText'], ['--feedback', 'feedback'], ['--target-files', 'targetFiles'],
    ['--port', 'port'], ['--poll-ms', 'pollMs'], ['--budget', 'budget'],
    ['--transcript', 'transcriptPath'], ['--result', 'resultPath'],
    ['--recorder-auth-root', 'authRoot'], ['--auth-provider', 'authProvider'],
  ]);
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--help') { values.help = true; continue; }
    if (argv[i] === '--approve-shell') { values.approveShell = true; continue; }
    const [option, inline] = argv[i].split('=', 2);
    const key = keys.get(option);
    if (!key) throw new Error(`Unknown option ${option}`);
    const value = inline ?? argv[++i];
    if (!value || value.startsWith('--')) throw new Error(`Missing ${option} value.`);
    if (key === 'budget') (values.budgets ??= []).push(value);
    else values[key] = value;
  }
  const budgets = { ...DEFAULT_BUDGETS };
  for (const pair of values.budgets ?? []) {
    const [phase, minutes] = pair.split('=');
    if (!(phase in budgets) || !Number.isFinite(Number(minutes)) || Number(minutes) <= 0) {
      throw new Error(`Invalid --budget ${pair}. Valid phases: ${Object.keys(budgets).join(', ')}`);
    }
    budgets[phase] = Number(minutes);
  }
  values.budgets = budgets;
  for (const key of ['port', 'pollMs']) {
    if (values[key] === undefined) continue;
    values[key] = Number(values[key]);
    if (!Number.isInteger(values[key]) || values[key] < 1 || (key === 'port' && values[key] > 65535)) {
      throw new Error(`Invalid ${key}.`);
    }
  }
  if (values.targetFiles) values.targetFiles = values.targetFiles.split(',').map((p) => p.trim()).filter(Boolean);
  return values;
}

export async function main(argv = process.argv.slice(2)) {
  const args = parseOracleArgs(argv);
  if (args.help) {
    console.log('Usage: node scripts/api-harness/run-oracle-acceptance.mjs --target <url> --project-id <uuid> (--run-id <uuid> | --goal <text>) --expected-text <text> --corrected-text <text> --feedback <grounded feedback> --target-files <comma-separated paths> [--workflow-id <id>] [--budget phase=minutes] [--approve-shell] [--poll-ms 5000] [--port 3000] [--transcript path] [--result path]');
    return 0;
  }
  if (!args.target || (!args.runId && (!args.projectId || !args.goal)) || !args.expectedText || !args.correctedText || !args.feedback || !args.targetFiles?.length) {
    throw new Error('Target, project/run inputs, both expected texts, feedback and target files are required. Use --help.');
  }
  validateNetworkTarget(args.target);
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const transcriptPath = path.resolve(args.transcriptPath ?? path.join(here, 'transcripts', `oracle-acceptance-${stamp}.jsonl`));
  const resultPath = path.resolve(args.resultPath ?? path.join(here, 'verdicts', `oracle-acceptance-${stamp}.json`));
  if (transcriptPath === resultPath) throw new Error('Transcript and result paths must differ.');
  if (args.authProvider && !['recorder-session', 'local-test'].includes(args.authProvider)) throw new Error('Unsupported auth provider.');
  const authProvider = args.authProvider === 'local-test'
    ? createLocalTestAuthProvider()
    : createRecorderSessionAuthProvider({ baseUrl: args.target, authRoot: args.authRoot });
  const client = new AgentweaverClient({ baseUrl: args.target, authProvider });
  const result = await runOracleAcceptance({
    ...args, transcriptPath, resultPath, request: createAcceptanceTransport(client, transcriptPath),
  });
  console.log(JSON.stringify({ verdict: result.verdict, phase: result.phase, transcriptPath, resultPath, error: result.error ?? null }));
  return result.verdict === 'pass' ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then((code) => { process.exitCode = code; }, (error) => {
    console.error(String(error.message));
    process.exitCode = 2;
  });
}
