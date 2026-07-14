// Structured finding writer + console reporter.
//
// A "finding" is the machine-readable output of one persona scenario run under the
// DRIVER/JUDGE separation: the persona/scenario identity, the FULL API call trail
// (with request+response bodies), the complete event stream, the drafted outcome
// spec verbatim, per-phase timings + performance/cost, and the DRIVER's objective
// platform-correctness (P0) checks. It intentionally carries NO subjective quality
// verdict — `judgment` is null and a separate LLM/human judge fills it in later
// from `judgeInputs` + `evidence`. Findings are the raw material for that judge and
// for filing GitHub issues.

import { writeFile, mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';

const GREEN = '\x1b[32m';
const RED = '\x1b[31m';
const YELLOW = '\x1b[33m';
const DIM = '\x1b[2m';
const BOLD = '\x1b[1m';
const RESET = '\x1b[0m';

/**
 * @param {Object} finding
 * @param {string} outPath absolute path to write the JSON finding
 */
export async function writeFinding(finding, outPath) {
  await mkdir(dirname(outPath), { recursive: true });
  await writeFile(outPath, JSON.stringify(finding, null, 2), 'utf8');
  return outPath;
}

export function printReport(finding) {
  const driver = finding.driver ?? {};
  const platformPass = driver.platformPass;
  const inconclusive = driver.inconclusive;
  const checks = driver.platformChecks ?? [];

  // The banner reflects the DRIVER's objective verdict only (did we drive the run
  // and capture evidence with platform-correctness intact?) — NOT output quality.
  const banner = inconclusive && platformPass
    ? `${YELLOW}INCONCLUSIVE${RESET}`
    : platformPass
      ? `${GREEN}DRIVE+CAPTURE OK${RESET}`
      : `${RED}DRIVER P0 FAIL${RESET}`;

  const personaTitle = finding.persona?.title ?? finding.persona ?? 'unknown';
  const scenarioName = finding.scenario?.personaScenario ?? finding.scenario ?? '';

  console.log('');
  console.log(`${BOLD}=== Persona scenario: ${personaTitle} / ${scenarioName} ===${RESET}`);
  console.log(`Driver: ${banner}   (${finding.durationMs} ms, ${finding.apiCalls.length} API calls)`);
  console.log('');

  const groups = [
    ['P0 — platform correctness (driver, deterministic)', 'P0'],
    ['Structural checks (driver, deterministic)', 'STRUCTURAL'],
    ['Cannot determine (unobservable — not scored)', 'CANNOT_DETERMINE'],
  ];
  for (const [heading, cat] of groups) {
    const group = checks.filter((c) => normalizeCat(c.category) === cat);
    if (group.length === 0) continue;
    console.log(`${BOLD}${heading}${RESET}`);
    for (const c of group) {
      const mark =
        cat === 'CANNOT_DETERMINE' || c.skipped
          ? `${YELLOW}∼${RESET}`
          : c.pass
            ? `${GREEN}✓${RESET}`
            : `${RED}✗${RESET}`;
      console.log(`  ${mark} ${c.name}${c.detail ? `  ${DIM}— ${c.detail}${RESET}` : ''}`);
    }
    console.log('');
  }

  // Make the deferred-quality contract explicit in the console output.
  if (finding.judgment === null && finding.kind !== 'generation-seam') {
    console.log(`${BOLD}P1 — output quality${RESET}`);
    console.log(`  ${YELLOW}⧗ DEFERRED to LLM judge${RESET}  ${DIM}— evidence + judgeInputs captured in the finding JSON${RESET}`);
    const crit = finding.judgeInputs?.successCriteria;
    if (crit) console.log(`  ${DIM}persona success criteria: ${crit}${RESET}`);
    console.log('');
  }

  if (finding.phaseTimings && Object.keys(finding.phaseTimings).length) {
    console.log(`${BOLD}Phase timings${RESET}`);
    for (const [k, v] of Object.entries(finding.phaseTimings)) {
      console.log(`  ${DIM}${k}${RESET}  ${v}ms`);
    }
    console.log('');
  }

  const perf = finding.performance;
  if (perf && perf.available) {
    console.log(`${BOLD}Performance / cost${RESET}`);
    if (perf.hasData) {
      console.log(
        `  ${DIM}tokens${RESET} ${perf.totalTokens}  ${DIM}aiu${RESET} ${perf.totalAiu?.toFixed?.(6) ?? perf.totalAiu}  ${DIM}invocations${RESET} ${perf.totalInvocations}`,
      );
      if (perf.responseDuration) {
        console.log(`  ${DIM}response p50/p95${RESET} ${perf.responseDuration.p50Ms ?? '—'}/${perf.responseDuration.p95Ms ?? '—'}ms`);
      }
    } else {
      console.log(`  ${DIM}no usage ingested yet (App Insights lag) — metrics endpoint reachable${RESET}`);
    }
    console.log('');
  }

  const approvals = finding.evidence?.approvalDecisions ?? [];
  if (approvals.length) {
    console.log(`${BOLD}Approval gates driven (judge-gated)${RESET}`);
    for (const a of approvals) {
      if (a.error) {
        console.log(`  ${RED}!${RESET} ${a.error}`);
        continue;
      }
      const d = a.judge?.decision?.decision ?? 'defer';
      const mark = d === 'approve' ? `${GREEN}approve${RESET}` : d === 'deny' ? `${RED}deny${RESET}` : `${YELLOW}defer${RESET}`;
      const src = a.judge?.source ?? a.judge?.decision?.source ?? 'judge';
      console.log(`  ${mark} ${DIM}${a.gate?.description ?? a.gate?.key ?? ''}${RESET}  ${DIM}(${src}${a.apiCall ? `, api ${a.apiCall.status}` : ', not executed'})${RESET}`);
    }
    console.log('');
  }

  console.log(`${BOLD}API call trace${RESET}`);
  for (const call of finding.apiCalls) {
    const ok = call.status >= 200 && call.status < 300;
    const mark = ok ? `${GREEN}${call.status}${RESET}` : `${RED}${call.status}${RESET}`;
    console.log(`  ${mark} ${call.method.padEnd(6)} ${call.path}  ${DIM}${call.ms}ms${RESET}`);
  }
  console.log('');
}

// Seam checks may be tagged 'P0' (they are deterministic structural checks) — map
// any non-P0/CANNOT_DETERMINE category (e.g. legacy 'P1') into the structural bucket
// so nothing is silently dropped from the console output.
function normalizeCat(category) {
  if (category === 'CANNOT_DETERMINE') return 'CANNOT_DETERMINE';
  if (category === 'P0') return 'P0';
  return 'STRUCTURAL';
}
