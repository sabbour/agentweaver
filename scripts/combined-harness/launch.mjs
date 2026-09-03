#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { collectVerdictPaths, loadVerdicts } from '../harness-judge/meta-aggregate.mjs';
import { redact } from '../harness-shared/redaction.mjs';
import { networkTargetEvidence } from '../harness-shared/target-guard.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SURFACES = ['api', 'ui', 'mcp'];

export function createBatchId() {
  return `combined-${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}`;
}

export function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 1) {
    const value = argv[index];
    if (!value.startsWith('--')) throw new Error(`unexpected argument: ${value}`);
    const key = value.slice(2);
    if (!argv[index + 1] || argv[index + 1].startsWith('--')) throw new Error(`missing value for --${key}`);
    args[key] = argv[++index];
  }
  return args;
}

function commandFromJson(value, option) {
  if (!value) return null;
  let command;
  try {
    command = JSON.parse(value);
  } catch {
    throw new Error(`--${option} must be a JSON argv array`);
  }
  if (!Array.isArray(command) || command.length === 0 || !command.every((part) => typeof part === 'string')) {
    throw new Error(`--${option} must be a non-empty JSON argv array of strings`);
  }
  return command;
}

function replaceTokens(part, tokens) {
  return part.replace(/\{(batchId|scenarioId|verdictDir)\}/g, (_, key) => tokens[key]);
}

export function buildCommands(args, tokens) {
  const configured = {
    // Default targets the one remaining fixed, one-shot API check: the structural
    // generation-seam conformance test (no persona/pushback dimension, so it fits
    // this launcher's single-child-process-per-surface model). A dynamically-
    // driven persona scenario is a multi-turn, dispatched-sub-agent (PersonaActor)
    // session driven by its own curl calls against the live API/OpenAPI spec, not
    // a one-shot command — pass an explicit `--api-command` that wraps your own
    // driving session if you need a persona scenario in a cross-surface sweep.
    api: commandFromJson(args['api-command'], 'api-command') ?? [
      'node', 'scripts/api-harness/run-persona.mjs', '--scenario', 'generated-artifacts-seam',
      '--batch-id', '{batchId}', '--out', '{verdictDir}/api.json',
    ],
    ui: commandFromJson(args['ui-command'], 'ui-command'),
    mcp: commandFromJson(args['mcp-command'], 'mcp-command'),
  };
  const selected = (args.surfaces ?? SURFACES.join(',')).split(',').filter(Boolean);
  if (!selected.length || selected.some((surface) => !SURFACES.includes(surface))) {
    throw new Error(`--surfaces must be a comma-separated subset of ${SURFACES.join(', ')}`);
  }
  return selected.map((surface) => {
    const command = configured[surface];
    if (!command) throw new Error(`--${surface}-command is required when ${surface} is selected`);
    return { surface, command: command.map((part) => replaceTokens(part, tokens)) };
  });
}

const SENSITIVE_ARG = /^--(?:token|authorization|api[-_]?key|secret|password|storage-state)$/i;

export function sanitizeCommand(command) {
  return command.map((part, index) => {
    if (index > 0 && SENSITIVE_ARG.test(command[index - 1])) return '[REDACTED]';
    if (/^[A-Za-z_][A-Za-z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY)=/i.test(part)) {
      return `${part.slice(0, part.indexOf('=') + 1)}[REDACTED]`;
    }
    if (/^--(?:token|authorization|api[-_]?key|secret|password|storage-state)=/i.test(part)) {
      return `${part.slice(0, part.indexOf('=') + 1)}[REDACTED]`;
    }
    return redact(part);
  });
}

export function sanitizeEnvironment(environment) {
  return redact(environment);
}

function option(command, names) {
  const index = command.findIndex((part) => names.includes(part));
  return index >= 0 ? command[index + 1] : null;
}

function commandPreflight(surface, command, environment) {
  const explicitTarget = option(command, ['--target', '--base-url']);
  const target = explicitTarget ?? (surface === 'mcp' && environment.AGENTWEAVER_BASE_URL
    ? `${environment.AGENTWEAVER_BASE_URL.replace(/\/+$/, '')}/mcp`
    : environment.AGENTWEAVER_BASE_URL ?? null);
  let evidence;
  try {
    evidence = target
      ? networkTargetEvidence(target, {
        surface,
        authSource: option(command, ['--token'])
          ? 'cli-token'
          : environment.AGENTWEAVER_TOKEN ? 'environment' : surface === 'ui' ? 'playwright-storage-state' : 'none',
        exactPath: surface === 'mcp' && target !== 'stdio' ? '/mcp' : undefined,
      })
      : {
        surface, transport: 'unspecified', targetOrigin: null, targetPath: null,
        authSource: 'none', tlsMode: 'system-default',
      };
  } catch (error) {
    evidence = {
      surface, transport: 'invalid', targetOrigin: null, targetPath: null,
      authSource: 'none', tlsMode: 'system-default', validationError: error.message,
    };
  }
  return {
    ...evidence,
    projectId: option(command, ['--project-id']),
    runId: null,
    cleanupIntent: surface === 'mcp' ? 'surface-owned cleanup policy' : 'surface runner cleanup policy',
    cleanupResult: 'delegated-to-surface',
  };
}

export function spawnCommand(command, options = {}) {
  return new Promise((resolve) => {
    const child = (options.spawn ?? spawn)(command[0], command.slice(1), {
      cwd: options.cwd ?? ROOT,
      env: options.env ?? process.env,
      stdio: options.stdio ?? 'inherit',
    });
    child.once('error', (error) => resolve({ code: null, signal: null, error: error.message }));
    child.once('close', (code, signal) => resolve({ code, signal, error: null }));
  });
}

function reportPathFor(args, verdictDir) {
  return path.resolve(args.report ?? path.join(verdictDir, '..', 'launcher-report.json'));
}

export async function runCombined(args, dependencies = {}) {
  if (!args['scenario-id']) throw new Error('--scenario-id is required');
  const batchId = args['batch-id'] ?? (dependencies.createBatchId ?? createBatchId)();
  const scenarioId = args['scenario-id'];
  const verdictDir = path.resolve(args['verdict-dir'] ?? path.join(ROOT, 'artifacts', 'combined-harness', batchId));
  const aggregateReport = path.resolve(args['aggregate-report'] ?? path.join(verdictDir, '..', 'cross-surface-report.json'));
  const tokens = { batchId, scenarioId, verdictDir: verdictDir.replaceAll('\\', '/') };
  const commands = buildCommands(args, tokens);
  const makeDir = dependencies.mkdir ?? mkdir;
  const run = dependencies.runCommand ?? ((command, options) => spawnCommand(command, options));
  const save = dependencies.writeFile ?? writeFile;
  const readVerdicts = dependencies.readVerdicts ?? (() => loadVerdicts(collectVerdictPaths([verdictDir])));

  await makeDir(verdictDir, { recursive: true });
  const childEnvironment = {
    ...process.env,
    AGENTWEAVER_BATCH_ID: batchId,
    AGENTWEAVER_SCENARIO_ID: scenarioId,
    AGENTWEAVER_VERDICT_DIR: verdictDir,
  };
  const results = await Promise.all(commands.map(async ({ surface, command }) => {
    const outcome = await run(command, { cwd: ROOT, env: childEnvironment, stdio: 'inherit' });
    return {
      surface,
      command: sanitizeCommand(command),
      code: outcome.code ?? null,
      signal: outcome.signal ?? null,
      error: redact(outcome.error ?? null),
    };
  }));

  const verdicts = readVerdicts().filter((verdict) => verdict.batchId === batchId && verdict.scenarioId === scenarioId);
  const missingSurfaces = commands
    .filter(({ surface }) => !verdicts.some((verdict) => verdict.surface === surface))
    .map(({ surface }) => surface);
  const aggregateCommand = ['node', 'scripts/harness-judge/meta-aggregate.mjs', verdictDir, '--json', aggregateReport];
  const aggregationOutcome = await run(aggregateCommand, { cwd: ROOT, env: childEnvironment, stdio: 'inherit' });
  const aggregation = {
    code: aggregationOutcome.code ?? null,
    signal: aggregationOutcome.signal ?? null,
    error: redact(aggregationOutcome.error ?? null),
  };
  const report = {
    batchId, scenarioId, verdictDir, aggregateReport, processes: results, aggregation,
    verdictCount: verdicts.length, missingSurfaces,
    preflight: commands.map(({ surface, command }) => commandPreflight(surface, command, childEnvironment)),
  };
  await makeDir(path.dirname(reportPathFor(args, verdictDir)), { recursive: true });
  await save(reportPathFor(args, verdictDir), `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  return report;
}

function usage() {
  return 'usage: node scripts/combined-harness/launch.mjs --scenario-id <id> [--batch-id <id>] [--surfaces api,ui,mcp] [--api-command <json-argv>] --ui-command <json-argv> --mcp-command <json-argv>';
}

async function main() {
  try {
    const report = await runCombined(parseArgs(process.argv.slice(2)));
    process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
    if (report.processes.some((result) => result.code !== 0) || report.missingSurfaces.length || report.aggregation.code !== 0) process.exitCode = 1;
  } catch (error) {
    console.error(`${error.message}\n${usage()}`);
    process.exitCode = 2;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
