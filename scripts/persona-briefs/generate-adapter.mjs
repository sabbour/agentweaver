#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ADAPTER_HEADINGS, SUPPORTED_SURFACES } from './persona-schema.mjs';
import { loadPersonaCore } from './index.mjs';

export function assembleAdapterGenerationPrompt({ core, surface } = {}) {
  if (!SUPPORTED_SURFACES.includes(surface)) throw new Error(`surface must be one of: ${SUPPORTED_SURFACES.join(', ')}`);
  if (!String(core ?? '').trim()) throw new Error('A validated persona core is required');
  return [
    '# TASK: Generate one thin persona surface adapter',
    '',
    `Target surface: ${surface}`,
    'Write only the final markdown adapter. Preserve the persona’s identity, goals, tone, and success criteria from the core; do not duplicate or redefine them.',
    'Map abstract intent (propose, inspect, push back, stop) to this surface only. Keep driver/judge separation: record deterministic evidence but do not judge quality.',
    '',
    'Persona core:',
    core.trim(),
    '',
    'Use exactly this heading structure:',
    '# Persona surface adapter: <Persona Name> — ' + surface,
    '',
    ...ADAPTER_HEADINGS,
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
  const persona = take('--persona');
  const surface = take('--surface');
  const out = take('--out');
  if (args.length || !persona || !surface) throw new Error('usage: node generate-adapter.mjs --persona <name> --surface <api|ui|mcp> [--out file]');
  const core = await loadPersonaCore(persona);
  const prompt = assembleAdapterGenerationPrompt({ core: core.content, surface });
  if (out) {
    fs.writeFileSync(out, prompt, 'utf8');
    console.error(`persona-adapter generation prompt written to ${out} — feed it to an LLM.`);
  } else process.stdout.write(prompt);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 2; });
}
