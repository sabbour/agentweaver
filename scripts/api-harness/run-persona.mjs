#!/usr/bin/env node
// Generated-artifact SEAM checker — CLI entry point.
//
// NOTE: this file NO LONGER drives persona scenarios. Persona-behavior scenarios
// (Priya, Jordan, ...) are driven dynamically by a dispatched PersonaActor
// sub-agent that curls the live API directly, guided by a persona-brief + the
// live OpenAPI/Swagger spec it fetches itself — deciding every next action
// (including pushback/objections) live from real responses, with no scripted
// HTTP-calling layer in between. See .github/agents/persona-actor.agent.md and
// .github/agents/harness.agent.md.
//
// This file remains the entry point ONLY for `scenarios/generated-artifacts-seam.mjs`
// (kind: 'generation-seam') — a deterministic STRUCTURAL conformance check of the
// blueprint/workflow GENERATORS themselves (reserved-role leaks, dangling edges,
// backend-guard round-trips). That is not a persona-behavior simulation and has no
// pushback/adaptive dimension, so it is intentionally NOT part of the fixed-script
// rigidity this pivot removes — it stays a fixed, deterministic regression check.
//
// Usage:
//   node run-persona.mjs --scenario generated-artifacts-seam \
//     --base-url https://agentweaver.example.com
//
//   Token resolution order: --token <t>  >  $AGENTWEAVER_TOKEN.
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

import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { readdir, writeFile } from 'node:fs/promises';

import { AgentweaverClient } from './lib/client.mjs';
import {
  createRecorderSessionAuthProvider,
  RECORDER_SESSION_AUTH_PROVIDER,
} from './lib/auth-providers/recorder-session.mjs';
import { runGenerationSeams } from './lib/seams.mjs';
import { summarizeProjectMetrics } from './lib/metrics.mjs';
import { writeFinding, printReport } from './lib/reporter.mjs';
import { loadPersona } from '../persona-briefs/index.mjs';
import { adaptApiEvidence } from '../harness-judge/adapters/api.mjs';
import { judgeEvidence } from '../harness-judge/core.mjs';
import { networkTargetEvidence, validateNetworkTarget } from '../harness-shared/target-guard.mjs';
import { redact } from '../harness-shared/redaction.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));

// generated-artifacts-seam (and any other kind: 'generation-seam' scenario) is a
// deterministic STRUCTURAL check with no persona behind it at all — loadPersona()
// genuinely returns null because there is no persona brief for it. The shared
// judge evidence schema (scripts/harness-judge/verdict-schema.mjs
// REQUIRED_JOIN_KEY_FIELDS) still requires adapterVersion/personaCoreVersion to be
// non-empty strings for every surface, so a real persona scenario's evidence can be
// join-keyed to the persona-brief version that produced it. For this fixed,
// non-persona scenario there is no such version to report, so we fall back to the
// same 'unknown' sentinel already used for this exact case in
// scripts/mcp-harness/run-persona.mjs (adapterVersion/personaCoreVersion) and
// scripts/ui-harness/agent-driver-ui/tools.mjs (targetRevision) — it satisfies the
// shared schema without fabricating a fake version number or weakening validation
// for scenarios that do have a real persona.
const NO_PERSONA_VERSION_SENTINEL = 'unknown';

function parseArgs(argv) {
  const out = { keep: false, rung: 'scoping' };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--keep') out.keep = true;
    else if (a === '--scenario') out.scenario = argv[++i];
    else if (a === '--base-url' || a === '--target') out.baseUrl = argv[++i];
    else if (a === '--persona') out.persona = argv[++i];
    else if (a === '--seed') out.seed = argv[++i];
    else if (a === '--batch-id') out.batchId = argv[++i];
    else if (a === '--target-revision') out.targetRevision = argv[++i];
    else if (a === '--rung') out.rung = argv[++i];
    else if (a === '--out') out.out = argv[++i];
    else if (a === '--token') out.token = argv[++i];
    else if (a === '--auth-provider') out.authProvider = argv[++i];
    else if (a === '--recorder-auth-root') out.recorderAuthRoot = argv[++i];
    else if (a === '--timeout') out.timeoutMs = Number(argv[++i]) * 1000;
    else if (a === '--list') out.list = true;
  }
  return out;
}

export function resolveToken(explicit, env = process.env) {
  if (explicit) return explicit;
  return env.AGENTWEAVER_TOKEN || null;
}

function resolveAuthProvider(args) {
  if (!args.authProvider) return null;
  if (args.authProvider !== RECORDER_SESSION_AUTH_PROVIDER) {
    throw new Error(`Unsupported auth provider "${args.authProvider}".`);
  }
  return createRecorderSessionAuthProvider({ authRoot: args.recorderAuthRoot });
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

  let validatedTarget;
  try {
    validatedTarget = validateNetworkTarget(baseUrl);
  } catch (error) {
    console.error(`error: ${error.message}`);
    return 2;
  }
  const persistedTarget = `${validatedTarget.origin}${validatedTarget.pathname}`;

  let authProvider;
  try {
    authProvider = resolveAuthProvider(args);
  } catch (err) {
    console.error(`error: ${err.message}`);
    return 2;
  }
  const token = authProvider ? null : resolveToken(args.token);
  if (!authProvider && !token) {
    console.error('error: no token (pass --token or set $AGENTWEAVER_TOKEN)');
    return 2;
  }

  let scenario;
  try {
    scenario = (await import(`./scenarios/${args.scenario}.mjs`)).default;
  } catch (err) {
    console.error(`error: cannot load scenario "${args.scenario}": ${err.message}`);
    return 2;
  }

  const kind = scenario.kind ?? null;
  if (kind !== 'generation-seam') {
    console.error(
      `error: run-persona.mjs only drives kind: 'generation-seam' scenarios (structural generator ` +
      `checks). Persona-behavior scenarios (Priya, Jordan, ...) are no longer fixed scripts and are ` +
      `not run through this file at all — dispatch a PersonaActor sub-agent that curls the live API ` +
      `directly against the fetched OpenAPI/Swagger spec, guided by the persona brief. See ` +
      `.github/agents/persona-actor.agent.md and .github/agents/harness.agent.md.`,
    );
    return 2;
  }

  const personaId = args.persona ?? scenario.personaFile?.replace(/\.md$/, '') ?? scenario.id.split('-')[0];
  const sharedPersona = await loadPersona(personaId, 'api').catch(() => null);
  const personaTitle = sharedPersona?.name ?? scenario.personaScenario ?? scenario.title;

  console.log(`Driving "${scenario.title}"`);
  console.log(`  persona : ${personaTitle}`);
  console.log(`  target  : ${persistedTarget}`);
  console.log('  mode    : API-only (no browser), generated-artifact seam validation');

  const client = new AgentweaverClient({
    baseUrl, token, authProvider,
  });

  let result;
  try {
    result = await runGenerationSeams(client, scenario, { keep: args.keep });
  } catch (err) {
    console.error(`error: scenario driver threw: ${err.stack ?? err}`);
    return 2;
  }

  const evidence = result.evidence;

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

  // Objective, deterministic driver checks (structural validation for seams).
  // These are the ONLY things the driver decides. Subjective output quality is
  // deferred to a separate LLM judge.
  const platformChecks = result.checks;
  const platformPass = result.pass;
  const inconclusive = result.inconclusive ?? false;

  // The matching persona scenario's authored acceptance criteria + failure signals,
  // surfaced verbatim so a judge can render the P1 verdict from this file alone.
  const finding = {
    schema: 'agentweaver.persona-finding/v2',
    generatedAt: new Date().toISOString(),
    target: persistedTarget,
    preflight: {
      ...networkTargetEvidence(baseUrl, {
        surface: 'api',
        authSource: authProvider ? `provider:${args.authProvider}` : args.token ? 'cli-token' : process.env.AGENTWEAVER_TOKEN ? 'environment' : 'none',
      }),
      projectId: evidence.projectId ?? null,
      runId: evidence.runId ?? null,
      cleanupIntent: args.keep ? 'retain-harness-created-resources' : 'delete-harness-created-resources',
      cleanupResult: args.keep ? 'retained' : 'completed-by-scenario',
    },
    kind,
    persona: {
      title: personaTitle,
      coreVersion: sharedPersona?.version ?? null,
      adapterVersion: sharedPersona?.adapter?.version ?? null,
    },
    // NOTE: the informational `persona.*Version` fields above stay `null` when
    // there is no persona (this is a structural check) — only the join-key
    // metadata below needs the non-empty NO_PERSONA_VERSION_SENTINEL, since that's
    // what the shared judge schema validates.
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
      personaTitle,
      personaCore: sharedPersona?.content ?? null,
      surfaceAdapter: sharedPersona?.adapter?.content ?? null,
      scenarioName: scenario.personaScenario,
      submittedGoal: result.evidence.submittedGoal ?? null,
      successCriteria: sharedPersona?.content ?? null,
      scenarioSpec: sharedPersona?.adapter?.content ?? null,
      failureSignals: [],
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

  const metadata = {
    batchId: args.batchId ?? `api-${stamp}`,
    scenarioId: args.scenario,
    inputSeed: args.seed ?? args.scenario,
    // See NO_PERSONA_VERSION_SENTINEL above: this file only ever drives
    // kind: 'generation-seam' structural scenarios, so sharedPersona is legitimately
    // null here — the sentinel keeps these join-key fields non-empty strings
    // (required by REQUIRED_JOIN_KEY_FIELDS) without inventing a fake version.
    adapterVersion: sharedPersona?.adapter?.version ?? NO_PERSONA_VERSION_SENTINEL,
    personaCoreVersion: sharedPersona?.version ?? NO_PERSONA_VERSION_SENTINEL,
    targetRevision: redact(args.targetRevision ?? persistedTarget),
    runId: result.evidence.runId ?? `harness-${stamp}`,
    timestamp: finding.generatedAt,
    persona: sharedPersona?.name ?? personaTitle,
  };
  const normalizedEvidence = adaptApiEvidence({
    metadata,
    persona: {
      name: sharedPersona?.name ?? personaTitle,
      briefText: sharedPersona?.content ?? null,
      surfaceAdapterText: sharedPersona?.adapter?.content ?? null,
      authoredCriteriaText: sharedPersona?.content ?? null,
    },
    turns: client.calls.map((call, n) => ({
      n: n + 1,
      action: `${call.method} ${call.path}`,
      request: { method: call.method, path: call.path, body: call.requestBody },
      response: { status: call.status, body: call.responseBody },
      latencyMs: call.ms,
      upstreamMs: call.upstreamMs,
      outcome: { ok: call.ok, status: call.status },
    })),
    findingsContext: [finding.driver],
    attachments: [{ kind: 'finding', evidence: JSON.stringify(finding.evidence) }],
    summary: `API harness ${scenario.id}`,
  });
  const judged = await judgeEvidence(normalizedEvidence, {
    timeoutMs: args.timeoutMs,
  });
  const verdictPath = args.out ?? join(HERE, 'verdicts', `${scenario.id}-${stamp}.json`);
  await writeFile(verdictPath, `${JSON.stringify(judged.verdict, null, 2)}\n`, 'utf8');

  printReport(finding);
  console.log(`Finding written: ${outPath.replace(join(HERE, '..', '..') + '\\', '')}`);
  console.log(`Verdict written: ${verdictPath.replace(join(HERE, '..', '..') + '\\', '')}`);

  await result.cleanup();
  if (!args.keep) {
    console.log('Cleaned up throwaway project (pass --keep to retain).');
  }

  // Exit 3 = inconclusive (e.g. the generator's model provider was unavailable, so the
  // seam couldn't be assessed) — distinct from a real structural FAIL (exit 1).
  if (inconclusive && platformPass) return 3;
  return platformPass ? 0 : 1;
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