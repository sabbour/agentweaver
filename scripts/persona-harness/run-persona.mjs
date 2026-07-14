#!/usr/bin/env node
// Persona-driven API E2E harness — CLI entry point.
//
// Runs ONE persona scenario end-to-end against a running Agentweaver instance
// using only REST API calls (no browser), and reports pass/fail with evidence.
//
// Usage:
//   node run-persona.mjs --scenario priya-ticket-triage \
//     --base-url https://agentweaver.<zone>.westus2.staging.aksapp.io [--insecure]
//
//   Token resolution order: --token <t>  >  $AGENTWEAVER_TOKEN  >  `gh auth token`.
//   Base URL: --base-url  >  $AGENTWEAVER_BASE_URL.
//
// Exit code 0 = driver drove + captured evidence cleanly (P0 platform-correctness
// held); 1 = a deterministic check FAILED (P0 mechanics or seam structural); 2 =
// harness/setup error; 3 = inconclusive (e.g. generator model provider down).
//
// NOTE (driver/judge separation): a 0 exit means the RUN was driven successfully
// and full evidence was captured — it is NOT a subjective "the output is good"
// verdict. That P1 quality verdict is rendered by a separate LLM judge reading the
// emitted finding JSON (judgeInputs + evidence). The driver never judges quality.

import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { readdir } from 'node:fs/promises';

import { AgentweaverClient } from './lib/client.mjs';
import { loadPersona } from './lib/persona.mjs';
import { driveScenario } from './lib/runner.mjs';
import { runGenerationSeams } from './lib/seams.mjs';
import { summarizeProjectMetrics } from './lib/metrics.mjs';
import { writeFinding, printReport } from './lib/reporter.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const PERSONAS_DIR = join(HERE, '..', '..', 'specs', 'personas');

function parseArgs(argv) {
  const out = { insecure: false, keep: false, allowInsecureProd: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--insecure') out.insecure = true;
    else if (a === '--allow-insecure-prod') out.allowInsecureProd = true;
    else if (a === '--keep') out.keep = true;
    else if (a === '--scenario') out.scenario = argv[++i];
    else if (a === '--base-url') out.baseUrl = argv[++i];
    else if (a === '--token') out.token = argv[++i];
    else if (a === '--timeout') out.timeoutMs = Number(argv[++i]) * 1000;
    else if (a === '--list') out.list = true;
  }
  return out;
}

/**
 * `--insecure` disables TLS verification, which is fine for localhost or the
 * staging zone but dangerous against production. Only allow it for hosts that are
 * clearly non-prod unless the caller explicitly opts in with --allow-insecure-prod.
 * @returns {string|null} an error message when the combination is disallowed, else null
 */
export function checkInsecureAllowed(baseUrl, insecure, allowInsecureProd) {
  if (!insecure) return null;
  let host;
  try {
    host = new URL(baseUrl).hostname.toLowerCase();
  } catch {
    return `--base-url "${baseUrl}" is not a valid URL`;
  }
  const isLocal = host === 'localhost' || host === '127.0.0.1' || host === '::1' || host.endsWith('.localhost');
  const isStaging = host.includes('.staging.') || host.endsWith('.staging');
  if (isLocal || isStaging || allowInsecureProd) return null;
  return (
    `refusing to disable TLS verification (--insecure) against non-staging host "${host}". ` +
    `Use a trusted certificate, target a *.staging.* / localhost host, or pass --allow-insecure-prod to override.`
  );
}

function resolveToken(explicit) {
  if (explicit) return explicit;
  if (process.env.AGENTWEAVER_TOKEN) return process.env.AGENTWEAVER_TOKEN;
  try {
    return execFileSync('gh', ['auth', 'token'], { encoding: 'utf8' }).trim();
  } catch {
    return null;
  }
}

async function listScenarios() {
  const files = (await readdir(join(HERE, 'scenarios'))).filter((f) => f.endsWith('.mjs'));
  console.log('Available scenarios:');
  for (const f of files) console.log(`  - ${f.replace(/\.mjs$/, '')}`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.list) {
    await listScenarios();
    return 0;
  }

  if (!args.scenario) {
    console.error('error: --scenario is required (use --list to see options)');
    return 2;
  }

  const baseUrl = args.baseUrl ?? process.env.AGENTWEAVER_BASE_URL;
  if (!baseUrl) {
    console.error('error: --base-url or $AGENTWEAVER_BASE_URL is required');
    return 2;
  }

  const insecureError = checkInsecureAllowed(baseUrl, args.insecure, args.allowInsecureProd);
  if (insecureError) {
    console.error(`error: ${insecureError}`);
    return 2;
  }

  const token = resolveToken(args.token);
  if (!token) {
    console.error('error: no token (pass --token, set $AGENTWEAVER_TOKEN, or run `gh auth login`)');
    return 2;
  }

  let scenario;
  try {
    scenario = (await import(`./scenarios/${args.scenario}.mjs`)).default;
  } catch (err) {
    console.error(`error: cannot load scenario "${args.scenario}": ${err.message}`);
    return 2;
  }

  const persona = await loadPersona(join(PERSONAS_DIR, scenario.personaFile));
  const kind = scenario.kind ?? 'persona-scoping';

  console.log(`Driving "${scenario.title}"`);
  console.log(`  persona : ${persona.title}`);
  console.log(`  target  : ${baseUrl}`);
  console.log(
    `  mode    : ${kind === 'generation-seam'
      ? 'API-only (no browser), generated-artifact seam validation'
      : 'API-only (no browser), start_mode=defineOutcome'}`,
  );

  const client = new AgentweaverClient({ baseUrl, token, insecure: args.insecure });

  let result;
  try {
    result =
      kind === 'generation-seam'
        ? await runGenerationSeams(client, scenario, { keep: args.keep })
        : await driveScenario(client, scenario, persona, {
            timeoutMs: args.timeoutMs,
            keep: args.keep,
          });
  } catch (err) {
    console.error(`error: scenario driver threw: ${err.stack ?? err}`);
    return 2;
  }

  const evidence =
    kind === 'generation-seam'
      ? result.evidence
      : {
          projectId: result.evidence.projectId,
          runId: result.evidence.runId,
          runStatus: result.evidence.runStatus,
          outcomeSpecSettled: result.evidence.outcomeSpecSettled ?? false,
          submittedGoal: result.evidence.submittedGoal ?? null,
          // FULL team object verbatim (a judge may inspect roles/instructions).
          team: result.evidence.team,
          teamMembers: Array.isArray(result.evidence.team?.members)
            ? result.evidence.team.members.map((m) => m.name ?? m.role ?? m.id)
            : [],
          // FULL outcome spec verbatim — the primary artifact the judge assesses.
          outcomeSpec: result.evidence.outcomeSpec,
          // FULL event stream verbatim (not a {sequence,type} projection) so the
          // judge can see everything that happened, plus a convenience count.
          events: result.evidence.events,
          eventTypeCounts: countBy(result.evidence.eventTypes ?? [], (e) => e.type),
          // Audit trail of any judged approval gate driven during the run (empty on
          // scoping-rung runs, which suspend before any tool/shell gate is raised).
          approvalDecisions: result.evidence.approvalDecisions ?? [],
        };

  // Performance/cost metrics (requirement 4) — reuse the dashboard's own endpoint.
  // Fetch BEFORE cleanup deletes the project. Never fails the scenario.
  let performance = { available: false, reason: 'no project' };
  const metricsProjectId = result.evidence.projectId;
  if (metricsProjectId) {
    try {
      performance = await summarizeProjectMetrics(client, metricsProjectId);
    } catch (err) {
      performance = { available: false, reason: String(err?.message ?? err) };
    }
  }

  // Objective, deterministic driver checks (P0 platform-correctness for scoping
  // runs; structural validation for seams). These are the ONLY things the driver
  // decides. Subjective output quality is deferred to a separate LLM judge.
  const platformChecks = kind === 'generation-seam' ? result.checks : result.platformChecks;
  const platformPass = kind === 'generation-seam' ? result.pass : result.platformPass;
  const inconclusive = result.inconclusive ?? false;

  // The matching persona scenario's authored acceptance criteria + failure signals,
  // surfaced verbatim so a judge can render the P1 verdict from this file alone.
  const matchedScenario = persona.scenarios.find((s) => s.name === scenario.personaScenario) ?? null;

  const finding = {
    schema: 'agentweaver.persona-finding/v2',
    generatedAt: new Date().toISOString(),
    target: baseUrl,
    kind,
    persona: {
      title: persona.title,
      file: `specs/personas/${scenario.personaFile}`,
    },
    scenario: {
      id: scenario.id,
      title: scenario.title,
      personaScenario: scenario.personaScenario,
    },
    durationMs: result.durationMs,

    // DRIVER verdict — objective/deterministic ONLY. No subjective quality call.
    driver: {
      platformPass,
      inconclusive,
      platformChecks,
    },

    // JUDGMENT — intentionally null. A separate LLM (or human) judge reads
    // `judgeInputs` + `evidence` below and fills this in later with a P0/P1/
    // CANNOT_DETERMINE verdict. The harness does NOT self-certify output quality.
    judgment: null,

    // Everything a downstream judge needs to render the verdict WITHOUT re-running.
    judgeInputs: {
      personaTitle: persona.title,
      personaFile: `specs/personas/${scenario.personaFile}`,
      scenarioName: scenario.personaScenario,
      submittedGoal: result.evidence.submittedGoal ?? null,
      successCriteria: matchedScenario?.fields?.['success looks like'] ?? null,
      scenarioSpec: matchedScenario?.raw ?? null,
      failureSignals: persona.failureSignals,
      judgeContext: result.judgeContext ?? null,
      taxonomy: {
        P0: 'Platform-correctness (orchestration mechanics). Already computed in driver.platformChecks — objective/deterministic.',
        P1: 'Output-quality: judge the drafted outcomeSpec (in evidence) against successCriteria. Subjective — the judge decides, not the driver.',
        CANNOT_DETERMINE: 'Evidence genuinely does not show it either way — do not guess pass/fail; mark unknown.',
      },
    },

    phaseTimings: result.timings ?? null,
    performance,
    evidence,

    // FULL API call trail WITH request + response bodies — the complete raw record.
    apiCalls: client.calls,
  };

  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const outPath = join(HERE, 'findings', `${scenario.id}-${stamp}.json`);
  await writeFinding(finding, outPath);

  printReport(finding);
  console.log(`Finding written: ${outPath.replace(join(HERE, '..', '..') + '\\', '')}`);

  await result.cleanup();
  if (!args.keep) {
    console.log(
      kind === 'generation-seam'
        ? 'Cleaned up throwaway project (pass --keep to retain).'
        : 'Cleaned up project + run (pass --keep to retain).',
    );
  }

  // Exit 3 = inconclusive (e.g. the generator's model provider was unavailable, so the
  // seam couldn't be assessed) — distinct from a real structural FAIL (exit 1).
  if (inconclusive && platformPass) return 3;
  return platformPass ? 0 : 1;
}

/** Count occurrences keyed by a selector, returned as a plain object. */
function countBy(arr, keyFn) {
  const out = {};
  for (const item of arr) {
    const k = keyFn(item);
    out[k] = (out[k] ?? 0) + 1;
  }
  return out;
}
// Only run the CLI when executed directly — importing this module (e.g. from
// tests, to reuse checkInsecureAllowed) must not trigger main() or process.exit.
if (import.meta.url === `file://${process.argv[1]}` || import.meta.url === pathToFileURL(process.argv[1]).href) {
  main()
    .then((code) => process.exit(code))
    .catch((err) => {
      console.error(err);
      process.exit(2);
    });
}