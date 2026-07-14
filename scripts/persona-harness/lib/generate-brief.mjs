#!/usr/bin/env node
// lib/generate-brief.mjs — LLM brief-generation PROMPT ASSEMBLER for persona runs.
//
// This follows the same architect-not-caller pattern as lib/judge.mjs: it NEVER
// invents the brief itself, and it NEVER calls an LLM API (no keys, no network).
// Its only job is to package the brief-generation constraints into a prompt string
// that a REAL LLM (this conversation, the coordinator, or a future automated step)
// can consume to propose a novel persona + scenario brief in the exact checked-in
// `briefs/*.md` shape so the existing agent-driver can use it unmodified.
//
// CLI:
//   node lib/generate-brief.mjs
//   node lib/generate-brief.mjs --category forum --exclude fittrack,bookclub
//   node lib/generate-brief.mjs --blueprint blueprint-content-authoring --out prompt.txt

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const BRIEF_TEMPLATE_HEADINGS = [
  '## Who you are',
  '## What you are trying to get done right now',
  '## Voice & behavior',
  '## MANDATORY behavior: push back at least TWICE',
  '## Where to stop (safe checkpoint)',
  '## What a good outcome would look like (for your own judgment, not a script)',
];

const ANY_HINT = 'any';

function dedupe(items) {
  return [...new Set(items)];
}

/** @param {string | string[] | undefined | null} input */
export function parseExcludeList(input) {
  if (!input) return [];
  const raw = Array.isArray(input) ? input : String(input).split(',');
  return dedupe(raw.map((value) => String(value).trim()).filter(Boolean));
}

/**
 * Normalize the target the caller wants variety around. Exactly one of
 * `blueprint` or `category` may be specific; otherwise this resolves to "any".
 * @param {{blueprint?: string | null, category?: string | null}} [opts]
 */
export function normalizeTargetHint(opts = {}) {
  const blueprint = String(opts.blueprint ?? '').trim();
  const category = String(opts.category ?? '').trim();
  if (blueprint && blueprint.toLowerCase() !== ANY_HINT) {
    return { kind: 'blueprint', value: blueprint };
  }
  if (category && category.toLowerCase() !== ANY_HINT) {
    return { kind: 'category', value: category };
  }
  return { kind: 'any', value: ANY_HINT };
}

function renderTargetGuidance(targetHint) {
  if (targetHint.kind === 'blueprint') {
    return `- Target blueprint hint: \`${targetHint.value}\`. Invent a scenario that plausibly fits this blueprint.`;
  }
  if (targetHint.kind === 'category') {
    return `- Target blueprint-category hint: \`${targetHint.value}\`. Invent a scenario that clearly belongs in this category.`;
  }
  return '- Target blueprint/category hint: `any`. You may choose any plausible product area, but it must still feel realistic for Agentweaver.';
}

function renderNoveltyGuidance(exclude) {
  if (!exclude.length) {
    return [
      '- Already-covered scenarios/archetypes: `(none supplied)`.',
      '- Still avoid clichéd repeats; prefer a concrete, realistic product/user problem over a generic CRUD toy.',
    ].join('\n');
  }
  return [
    `- Already-covered scenarios/archetypes to avoid repeating: ${exclude.map((item) => `\`${item}\``).join(', ')}.`,
    '- Do NOT rename one of those apps or lightly reskin the same concept. Choose a meaningfully different domain, workflow, and user pressure.',
  ].join('\n');
}

function renderTemplateContract() {
  return [
    'Use THIS exact markdown structure and heading text. Replace the placeholder content only:',
    '',
    '# Persona brief: <Persona Name> — <Short role/context label>',
    '',
    '> This is a **brief, not a script.** It gives you <Persona Name>\'s goals, constraints, and',
    '> voice. It does **not** tell you what to type, in what order, or exactly what to',
    '> object to. You decide each of <Persona Name>\'s turns *live*, based on what the real',
    '> Agentweaver API actually returns. Derived from',
    '> `[specs/personas/<new-authored-spec>.md](../../../specs/personas/<new-authored-spec>.md)`.',
    '',
    ...BRIEF_TEMPLATE_HEADINGS,
  ].join('\n');
}

/**
 * Build the prompt an external LLM should receive to draft a new persona brief.
 * @param {{blueprint?: string | null, category?: string | null, exclude?: string[]}} [opts]
 */
export function assembleBriefGenerationPrompt(opts = {}) {
  const targetHint = normalizeTargetHint(opts);
  const exclude = parseExcludeList(opts.exclude);

  return [
    '# TASK: Propose one NEW Agentweaver persona brief',
    '',
    'You are drafting a NEW persona brief for the Agentweaver persona harness.',
    'Important: you are writing the BRIEF markdown itself, not commentary about it.',
    'The brief will be consumed directly by the existing `agent-driver/` flow, so the',
    'format must match the checked-in hand-authored briefs exactly.',
    '',
    '## Constraints',
    renderTargetGuidance(targetHint),
    renderNoveltyGuidance(exclude),
    '- Invent BOTH: (1) a plausible human persona with name/role/context/goals/voice/quirks, and (2) a concrete product idea or scenario that person would bring to Agentweaver.',
    '- Make the scenario realistic, specific, and testable through the existing API-scoping harness. Avoid vague "build an app" asks.',
    '- The persona must have clear success criteria and at least two believable ways they might push back after reading an initial draft.',
    '- Keep the brief aligned with the current harness philosophy: it is a brief, not a script; it should guide live reactions to real API output.',
    '- Do NOT mention FitTrack, BookClub, TrailMix, LinkVault, HabitLoop, ForumHub, or any excluded archetype unless you are explicitly contrasting against them while choosing something different.',
    '',
    '## Format contract',
    renderTemplateContract(),
    '',
    '## Content requirements per section',
    '- `## Who you are`: define the persona\'s expertise, blind spots, operational context, and what they care about.',
    '- `## What you are trying to get done right now`: describe the concrete messy input, project ask, or business problem they bring today. Include a realistic starting payload/idea in a fenced code block when that helps, mirroring the existing briefs.',
    '- `## Voice & behavior`: 3-5 bullets on tone, patience level, and what kinds of failure they dislike.',
    '- `## MANDATORY behavior: push back at least TWICE`: explain that each pushback must be a genuine reaction to what the API actually returned, and list example categories of problems they might notice for THIS scenario.',
    '- `## Where to stop (safe checkpoint)`: stop at the outcome-spec confirmation gate; do not confirm the spec.',
    '- `## What a good outcome would look like (for your own judgment, not a script)`: summarize what success would look like from this persona\'s viewpoint without turning it into scoring logic.',
    '',
    '## Hard requirements',
    '- Output ONLY the final markdown brief. No preamble, no analysis, no code fences around the whole document.',
    '- Preserve the heading text exactly, including capitalization and punctuation.',
    '- Make the scenario clearly distinct from the excluded/already-covered list in domain, user pressure, and expected plan shape.',
    '- Prefer specificity over breadth: one sharp persona + one sharp scenario.',
  ].join('\n');
}

function isMain() {
  return process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}

if (isMain()) {
  const args = process.argv.slice(2);
  const takeValue = (flag) => {
    const idx = args.indexOf(flag);
    if (idx === -1) return null;
    const value = args[idx + 1];
    if (!value || value.startsWith('--')) {
      console.error(`usage: ${flag} requires a value`);
      process.exit(2);
    }
    args.splice(idx, 2);
    return value;
  };

  const outFile = takeValue('--out');
  const blueprint = takeValue('--blueprint');
  const category = takeValue('--category');
  const excludeArg = takeValue('--exclude');

  if (args.length) {
    console.error('usage: node lib/generate-brief.mjs [--blueprint <id> | --category <name>] [--exclude a,b,c] [--out prompt.txt]');
    process.exit(2);
  }
  if (blueprint && category && category.toLowerCase() !== ANY_HINT) {
    console.error('usage: provide either --blueprint or --category, not both');
    process.exit(2);
  }

  const prompt = assembleBriefGenerationPrompt({
    blueprint,
    category,
    exclude: parseExcludeList(excludeArg),
  });

  if (outFile) {
    fs.writeFileSync(outFile, prompt, 'utf8');
    console.error(`brief-generation prompt written to ${outFile} (${prompt.length} chars) — feed it to an LLM.`);
  } else {
    process.stdout.write(prompt);
  }
}
