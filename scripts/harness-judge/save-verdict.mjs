#!/usr/bin/env node
// Parse + validate a raw Judge-subagent response into a schema-valid verdict, and
// write it to disk. This is the "no subprocess judge involved" counterpart to
// core.mjs's judgeEvidence(): used when the verdict text already came back from a
// `task` tool dispatch to the `Judge` custom agent (see .github/agents/harness.agent.md
// "Judging" and .github/agents/judge.agent.md), not from spawning an external
// AGENTWEAVER_JUDGE_CMD process.
//
// On invalid/unparseable judge output, this falls back to core.mjs's schema-valid
// CANNOT_DETERMINE verdict (buildFallbackVerdict) rather than persisting bad data —
// mirroring judgeEvidence()'s own fallback behavior — and exits non-zero so the
// caller can decide whether to retry.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildFallbackVerdict, parseVerdictText } from './core.mjs';
import { validateVerdict } from './verdict-schema.mjs';

function loadJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function normalizeMetadata(metadata = {}) {
  const out = { ...metadata };
  if (!out.timestamp) out.timestamp = new Date().toISOString();
  return out;
}

/**
 * @param {string} rawText the Judge subagent's raw response text
 * @param {object} metadata join-key metadata from the normalized evidence (used to
 *   validate/repair join-key fields and to build a fallback verdict on failure)
 * @returns {{ ok: boolean, verdict: object, error?: object }}
 */
export function saveVerdict(rawText, metadata) {
  const parsed = parseVerdictText(rawText);
  if (!parsed.ok) {
    return { ok: false, verdict: buildFallbackVerdict(metadata, parsed.error), error: parsed.error };
  }
  const validation = validateVerdict(parsed.verdict, { expectedMetadata: metadata });
  if (!validation.ok) {
    const error = { kind: 'schema_invalid', message: validation.errors.join('; ') };
    return { ok: false, verdict: buildFallbackVerdict(metadata, error), error };
  }
  return { ok: true, verdict: parsed.verdict };
}

function isMain() {
  return process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}

if (isMain()) {
  const args = process.argv.slice(2);
  const outIdx = args.indexOf('--out');
  const evidenceIdx = args.indexOf('--evidence');
  let outFile = null;
  let evidenceFile = null;

  if (outIdx !== -1) {
    outFile = args[outIdx + 1];
    args.splice(outIdx, 2);
  }
  if (evidenceIdx !== -1) {
    evidenceFile = args[evidenceIdx + 1];
    args.splice(evidenceIdx, 2);
  }

  const rawTextFile = args[0];
  if (!rawTextFile || !evidenceFile) {
    console.error('usage: node save-verdict.mjs <raw-judge-response.txt> --evidence <normalized-evidence.json> [--out verdict.json]');
    process.exit(2);
  }

  const rawText = fs.readFileSync(rawTextFile, 'utf8');
  const evidence = loadJson(evidenceFile);
  const metadata = normalizeMetadata(evidence.metadata ?? {});
  if (metadata.persona == null && evidence.persona?.name) metadata.persona = evidence.persona.name;

  const result = saveVerdict(rawText, metadata);
  const output = `${JSON.stringify(result.verdict, null, 2)}\n`;
  if (outFile) fs.writeFileSync(outFile, output, 'utf8');
  else process.stdout.write(output);

  if (!result.ok) {
    console.error(`save-verdict: judge output was invalid (${result.error.kind}): ${result.error.message}`);
    process.exitCode = 1;
  }
}
