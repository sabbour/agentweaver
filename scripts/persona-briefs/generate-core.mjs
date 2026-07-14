#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CORE_HEADINGS } from './persona-schema.mjs';
import { listPersonas } from './index.mjs';

export function parseCommaList(input) {
  const values = Array.isArray(input) ? input : String(input ?? '').split(',');
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))];
}

export function assembleCoreGenerationPrompt({ description, exclude = [] } = {}) {
  const request = String(description ?? '').trim();
  if (!request) throw new Error('A natural-language persona description is required');
  const exclusions = parseCommaList(exclude);
  return [
    '# TASK: Generate one new surface-agnostic persona core',
    '',
    'Write only the final markdown core. Do not call tools, describe a workflow, or add a preamble.',
    `Natural-language testing intent: ${request}`,
    `Existing personas/archetypes to stay distinct from: ${exclusions.length ? exclusions.map((x) => `\`${x}\``).join(', ') : '`(none supplied)`'}.`,
    '',
    'The core must describe identity, goal, tone, low-tolerance triggers, success criteria, and a mandatory two grounded-pushback rule. It must work unchanged through API, UI, and MCP harnesses.',
    'Do not mention APIs, HTTP, curl, buttons, clicks, tool names, or a particular transport. Put only action mapping in a future surface adapter.',
    '',
    'Use exactly this heading structure:',
    '# Persona core: <Name> — <Short role/context label>',
    '',
    ...CORE_HEADINGS,
    '',
    'The mandatory pushback section must require at least two objections grounded in observed results, not pre-written complaints. The safe checkpoint must stop before work begins.',
  ].join('\n');
}

async function main() {
  const args = process.argv.slice(2);
  const take = (flag) => {
    const index = args.indexOf(flag);
    if (index < 0) return null;
    const value = args[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`${flag} requires a value`);
    args.splice(index, 2);
    return value;
  };
  const description = take('--description');
  const exclude = take('--exclude');
  const out = take('--out');
  if (args.length) throw new Error('usage: node generate-core.mjs --description <text> [--exclude a,b] [--out file]');
  const prompt = assembleCoreGenerationPrompt({ description, exclude: exclude ? parseCommaList(exclude) : await listPersonas() });
  if (out) {
    fs.writeFileSync(out, prompt, 'utf8');
    console.error(`persona-core generation prompt written to ${out} — feed it to an LLM.`);
  } else process.stdout.write(prompt);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 2; });
}
