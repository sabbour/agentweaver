import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import {
  FRUSTRATION_SCORES,
  P0_VERDICTS,
  P1_VERDICTS,
  REQUIRED_JOIN_KEY_FIELDS,
  VERDICT_SCHEMA,
  extractJoinKey,
  validateVerdict,
} from './verdict-schema.mjs';

export const DEFAULT_TIMEOUT_MS = 120_000;
export const DEFAULT_RETRIES = 1;
export const DEFAULT_RETRY_DELAY_MS = 1_000;

function isPlainObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function tail(text, limit = 1_000) {
  const value = String(text ?? '');
  return value.length <= limit ? value : value.slice(-limit);
}

function fence(value) {
  return `\`\`\`json\n${JSON.stringify(value, null, 2)}\n\`\`\``;
}

function splitWindowsCommand(command) {
  const parts = String(command).match(/"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|[^\s]+/g) ?? [];
  return parts.map((part) => {
    if ((part.startsWith('"') && part.endsWith('"')) || (part.startsWith("'") && part.endsWith("'"))) {
      return part.slice(1, -1);
    }
    return part;
  });
}

function normalizeMetadata(metadata = {}) {
  const out = { ...metadata };
  if (!out.timestamp) out.timestamp = new Date().toISOString();
  return out;
}

function validateEvidenceShape(evidence) {
  const errors = [];
  if (!isPlainObject(evidence)) return { ok: false, errors: ['normalized evidence must be an object'] };
  if (!isPlainObject(evidence.metadata)) errors.push('normalized evidence metadata must be an object');
  else {
    for (const field of REQUIRED_JOIN_KEY_FIELDS) {
      if (typeof evidence.metadata[field] !== 'string' || !evidence.metadata[field].trim()) {
        errors.push(`metadata.${field} must be a non-empty string`);
      }
    }
  }
  if (!Array.isArray(evidence.turns)) errors.push('normalized evidence turns must be an array');
  return { ok: errors.length === 0, errors };
}

function verdictTemplate(metadata, persona = null) {
  return {
    schema: VERDICT_SCHEMA,
    persona,
    batchId: metadata.batchId,
    scenarioId: metadata.scenarioId,
    inputSeed: metadata.inputSeed,
    adapterVersion: metadata.adapterVersion,
    personaCoreVersion: metadata.personaCoreVersion,
    targetRevision: metadata.targetRevision,
    surface: metadata.surface,
    runId: metadata.runId,
    timestamp: metadata.timestamp,
    p0: { verdict: P0_VERDICTS.join(' | '), evidence: '<objective mechanics summary>' },
    p1: { verdict: P1_VERDICTS.join(' | '), evidence: '<quality summary vs persona criteria>', criteriaCoverage: [] },
    frustration: {
      level: 'none | mild | moderate | severe | abandoned | not_assessed',
      score: 0,
      signals: [{ kind: '<observed signal>', evidence: '<turn refs / quote>' }],
      rationale: '<one or two sentences grounded in observed evidence>',
    },
    pushback: { count: 0, requirementMet: true, each: [] },
    cannotDetermine: [],
    findings: [{ title: '<finding>', kind: 'P0 | P1 | usability | capability-gap | drift', evidence: '<turn refs>' }],
  };
}

/**
 * Prompt contract for the shared judge.
 * @param {object} normalizedEvidence
 * @param {object} [ctx]
 * @param {string} [ctx.judgeMd]
 * @param {string} [ctx.surfaceAppendix]
 */
export function buildJudgePrompt(normalizedEvidence, ctx = {}) {
  const validation = validateEvidenceShape(normalizedEvidence);
  if (!validation.ok) throw new Error(`invalid normalized evidence: ${validation.errors.join('; ')}`);

  const metadata = normalizeMetadata(normalizedEvidence.metadata);
  const persona = normalizedEvidence.persona ?? {};
  const judgeMd = ctx.judgeMd?.trim() || '(no shared JUDGE.md supplied)';
  const surfaceAppendix = ctx.surfaceAppendix?.trim() || '(no surface appendix supplied)';
  const template = verdictTemplate(metadata, metadata.persona ?? persona.name ?? null);

  return [
    '# TASK: Judge one normalized harness run',
    '',
    'You are the shared Agentweaver harness judge. Judge ONLY from the evidence below.',
    'Return exactly one machine-readable verdict JSON object conforming to the shared',
    `schema \`${VERDICT_SCHEMA}\`. Preserve the join-key metadata exactly as provided.`,
    '',
    '---',
    '## Shared methodology',
    '',
    judgeMd,
    '',
    '---',
    `## Surface appendix (${metadata.surface})`,
    '',
    surfaceAppendix,
    '',
    '---',
    '## Required join-key metadata (copy exactly)',
    '',
    fence(extractJoinKey(metadata)),
    '',
    '---',
    '## Persona context',
    '',
    fence({
      name: persona.name ?? metadata.persona ?? null,
      briefText: persona.briefText ?? null,
      authoredCriteriaText: persona.authoredCriteriaText ?? null,
      surfaceAdapterText: persona.surfaceAdapterText ?? null,
    }),
    '',
    '---',
    '## Run metadata',
    '',
    fence({
      ...metadata,
      scenarioTitle: normalizedEvidence.scenarioTitle ?? null,
      target: normalizedEvidence.target ?? null,
      summary: normalizedEvidence.summary ?? null,
    }),
    '',
    '---',
    '## Normalized turn evidence',
    '',
    normalizedEvidence.turns.length ? fence(normalizedEvidence.turns) : '(no turns captured)',
    '',
    '---',
    '## Supplemental evidence',
    '',
    fence({
      attachments: normalizedEvidence.attachments ?? [],
      findingsContext: normalizedEvidence.findingsContext ?? [],
      rawSummary: normalizedEvidence.rawSummary ?? null,
    }),
    '',
    '---',
    '## Output shape',
    '',
    fence(template),
    '',
    'Rules:',
    '- `frustration.level = "not_assessed"` MUST have `score: null` and is used only when the evidence is insufficient.',
    '- `none` means frustration was assessed and none was observed.',
    '- If the evidence is insufficient overall, use `CANNOT_DETERMINE` in p0/p1 and explain why in cannotDetermine.',
    '- Do not invent evidence. Cite observed turn refs or quotes in findings and frustration signals.',
  ].join('\n');
}

export function parseVerdictText(text) {
  if (typeof text !== 'string' || !text.trim()) {
    return { ok: false, error: { kind: 'unparseable', message: 'judge output was empty' } };
  }
  const fenced = text.match(/```json\s*([\s\S]*?)```/i);
  const candidate = fenced ? fenced[1] : text;
  const braced = candidate.match(/\{[\s\S]*\}/);
  const jsonText = braced ? braced[0] : candidate;
  try {
    return { ok: true, verdict: JSON.parse(jsonText) };
  } catch (error) {
    return { ok: false, error: { kind: 'unparseable', message: `judge output was not valid JSON: ${error.message}` } };
  }
}

export function makeCommandJudge(cmd, opts = {}) {
  const timeoutMs = Number.isFinite(opts.timeoutMs) ? opts.timeoutMs : DEFAULT_TIMEOUT_MS;
  const maxBuffer = Number.isFinite(opts.maxBuffer) ? opts.maxBuffer : 32 * 1024 * 1024;
  return async ({ prompt }) => {
    try {
      // On Windows, terminating a shell command on timeout can leave its child model
      // process running. Invoke the executable directly there while retaining the
      // shell-command behavior used by the existing approval judge elsewhere.
      const windowsParts = process.platform === 'win32' ? splitWindowsCommand(cmd) : null;
      const command = windowsParts?.shift() ?? cmd;
      const args = windowsParts ?? [];
      if (!command) {
        return {
          ok: false,
          error: { kind: 'exception', message: 'judge command was empty' },
        };
      }
      const res = spawnSync(command, args, {
        input: prompt,
        shell: process.platform !== 'win32',
        encoding: 'utf8',
        maxBuffer,
        timeout: timeoutMs,
      });
      if (res.error) {
        const kind = res.error.code === 'ETIMEDOUT' ? 'timeout' : 'exception';
        return {
          ok: false,
          error: {
            kind,
            message: res.error.message,
            exitCode: res.status ?? null,
            stderrTail: tail(res.stderr),
          },
        };
      }
      if (res.status !== 0) {
        return {
          ok: false,
          error: {
            kind: 'nonzero_exit',
            message: `judge command exited ${res.status}`,
            exitCode: res.status ?? null,
            stderrTail: tail(res.stderr),
          },
        };
      }
      return parseVerdictText(res.stdout);
    } catch (error) {
      return {
        ok: false,
        error: {
          kind: 'exception',
          message: String(error?.message ?? error),
        },
      };
    }
  };
}

export function makeDefaultJudge(opts = {}) {
  const env = opts.env ?? process.env;
  const judgeCmd = opts.judgeCmd ?? env.AGENTWEAVER_JUDGE_CMD ?? null;
  if (!judgeCmd) {
    return async () => ({
      ok: false,
      error: {
        kind: 'missing_command',
        message: 'no judge command configured (set AGENTWEAVER_JUDGE_CMD or inject a judge)',
      },
    });
  }
  return makeCommandJudge(judgeCmd, opts);
}

export function buildFallbackVerdict(metadata, judgeError) {
  const join = extractJoinKey(metadata);
  const persona = metadata.persona ?? null;
  const reason = judgeError?.message ?? 'judge execution failed before a verdict was produced';
  return {
    schema: VERDICT_SCHEMA,
    persona,
    ...join,
    p0: {
      verdict: 'CANNOT_DETERMINE',
      evidence: 'Judge execution failed before objective mechanics could be assessed.',
    },
    p1: {
      verdict: 'CANNOT_DETERMINE',
      evidence: 'Judge execution failed before quality could be assessed.',
      criteriaCoverage: [],
    },
    frustration: {
      level: 'not_assessed',
      score: FRUSTRATION_SCORES.not_assessed,
      signals: [],
      rationale: 'Judge execution failed, so frustration could not be assessed from evidence.',
    },
    pushback: {
      count: 0,
      requirementMet: false,
      each: [],
    },
    cannotDetermine: [reason],
    findings: [],
    judgeError,
  };
}

export async function judgeEvidence(normalizedEvidence, opts = {}) {
  const shapeValidation = validateEvidenceShape(normalizedEvidence);
  if (!shapeValidation.ok) {
    throw new Error(`invalid normalized evidence: ${shapeValidation.errors.join('; ')}`);
  }

  const metadata = normalizeMetadata(normalizedEvidence.metadata);
  if (metadata.persona == null && normalizedEvidence.persona?.name) metadata.persona = normalizedEvidence.persona.name;
  const prompt = buildJudgePrompt({ ...normalizedEvidence, metadata }, opts);
  const retries = Number.isInteger(opts.retries) && opts.retries >= 0 ? opts.retries : DEFAULT_RETRIES;
  const retryDelayMs = Number.isFinite(opts.retryDelayMs) ? opts.retryDelayMs : DEFAULT_RETRY_DELAY_MS;
  const judge = typeof opts.judge === 'function' ? opts.judge : makeDefaultJudge(opts);

  let lastError = null;
  let lastRaw = null;

  for (let attempt = 1; attempt <= retries + 1; attempt += 1) {
    try {
      const raw = await judge({ prompt, evidence: normalizedEvidence, metadata, attempt });
      lastRaw = raw;
      if (raw?.ok === false) {
        lastError = { ...raw.error, attempts: attempt };
      } else {
        const candidate = raw?.ok === true && raw.verdict ? raw.verdict : raw;
        const validation = validateVerdict(candidate, { expectedMetadata: metadata });
        if (validation.ok) {
          return { prompt, verdict: candidate, rawVerdict: candidate, attempts: attempt };
        }
        lastError = {
          kind: 'schema_invalid',
          message: validation.errors.join('; '),
          attempts: attempt,
        };
      }
    } catch (error) {
      lastError = {
        kind: 'exception',
        message: String(error?.message ?? error),
        attempts: attempt,
      };
    }

    if (attempt <= retries) await sleep(retryDelayMs);
  }

  return {
    prompt,
    verdict: buildFallbackVerdict(metadata, lastError),
    rawVerdict: lastRaw,
    attempts: retries + 1,
  };
}

function loadJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function isMain() {
  return process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}

if (isMain()) {
  const args = process.argv.slice(2);
  const outIdx = args.indexOf('--out');
  const promptOutIdx = args.indexOf('--prompt-out');
  const timeoutIdx = args.indexOf('--judge-timeout');
  const retriesIdx = args.indexOf('--judge-retries');
  let outFile = null;
  let promptOutFile = null;
  let timeoutMs = DEFAULT_TIMEOUT_MS;
  let retries = DEFAULT_RETRIES;

  if (outIdx !== -1) {
    outFile = args[outIdx + 1];
    args.splice(outIdx, 2);
  }
  if (promptOutIdx !== -1) {
    promptOutFile = args[promptOutIdx + 1];
    args.splice(promptOutIdx, 2);
  }
  if (timeoutIdx !== -1) {
    timeoutMs = Number(args[timeoutIdx + 1]) || DEFAULT_TIMEOUT_MS;
    args.splice(timeoutIdx, 2);
  }
  if (retriesIdx !== -1) {
    retries = Number(args[retriesIdx + 1]) || 0;
    args.splice(retriesIdx, 2);
  }

  const evidenceFile = args[0];
  if (!evidenceFile) {
    console.error('usage: node core.mjs <normalized-evidence.json> [--out verdict.json] [--prompt-out prompt.txt] [--judge-timeout ms] [--judge-retries n]');
    process.exit(2);
  }

  const evidence = loadJson(evidenceFile);
  const { prompt, verdict } = await judgeEvidence(evidence, { timeoutMs, retries });
  if (promptOutFile) fs.writeFileSync(promptOutFile, prompt, 'utf8');

  const output = `${JSON.stringify(verdict, null, 2)}\n`;
  if (outFile) fs.writeFileSync(outFile, output, 'utf8');
  else process.stdout.write(output);
}
