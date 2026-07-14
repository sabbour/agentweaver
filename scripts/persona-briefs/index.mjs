import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SUPPORTED_SURFACES, validatePersonaBrief, validatePersonaCore, validateSurfaceAdapter } from './persona-schema.mjs';

export const PACKAGE_DIR = path.dirname(fileURLToPath(import.meta.url));
export const PERSONAS_DIR = path.join(PACKAGE_DIR, 'personas');
export const SURFACES_DIR = path.join(PACKAGE_DIR, 'surfaces');

function normalizeName(name) {
  const normalized = String(name ?? '').trim().toLowerCase();
  if (!/^[a-z0-9][a-z0-9-]*$/.test(normalized)) throw new Error(`Invalid persona name "${name}"`);
  return normalized;
}

function fingerprint(text) {
  return createHash('sha256').update(text).digest('hex').slice(0, 12);
}

async function readRequired(filePath, description) {
  try {
    return await readFile(filePath, 'utf8');
  } catch (error) {
    if (error?.code === 'ENOENT') throw new Error(`${description} was not found: ${filePath}`);
    throw error;
  }
}

export async function listPersonas({ surface } = {}) {
  if (surface && !SUPPORTED_SURFACES.includes(surface)) throw new Error(`Unsupported surface "${surface}"`);
  const files = await readdir(PERSONAS_DIR, { withFileTypes: true });
  const names = files.filter((entry) => entry.isFile() && entry.name.endsWith('.md'))
    .map((entry) => entry.name.slice(0, -3)).sort();
  if (!surface) return names;

  const available = [];
  for (const name of names) {
    try {
      await readFile(path.join(SURFACES_DIR, `${name}.${surface}.md`), 'utf8');
      available.push(name);
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
  }
  return available;
}

export async function loadPersonaCore(name) {
  const id = normalizeName(name);
  const filePath = path.join(PERSONAS_DIR, `${id}.md`);
  const content = await readRequired(filePath, `Persona core "${id}"`);
  const validation = validatePersonaCore(content);
  if (!validation.valid) throw new Error(`Invalid persona core "${id}": ${validation.errors.join('; ')}`);
  return { id, filePath, content, name: validation.name, version: `${id}@${fingerprint(content)}` };
}

export async function loadSurfaceAdapter(name, surface) {
  const id = normalizeName(name);
  if (!SUPPORTED_SURFACES.includes(surface)) throw new Error(`Unsupported surface "${surface}"`);
  const filePath = path.join(SURFACES_DIR, `${id}.${surface}.md`);
  const content = await readRequired(filePath, `Persona adapter "${id}.${surface}"`);
  const validation = validateSurfaceAdapter(content, surface);
  if (!validation.valid) throw new Error(`Invalid persona adapter "${id}.${surface}": ${validation.errors.join('; ')}`);
  return { id, surface, filePath, content, name: validation.name, version: `${id}.${surface}@${fingerprint(content)}` };
}

export async function loadPersona(name, surface = undefined) {
  const core = await loadPersonaCore(name);
  const adapter = surface ? await loadSurfaceAdapter(core.id, surface) : null;
  const validation = validatePersonaBrief({ core: core.content, adapter: adapter?.content, surface });
  if (!validation.valid) throw new Error(`Invalid persona brief "${core.id}": ${validation.errors.join('; ')}`);
  return {
    ...core,
    surface,
    adapter,
    text: [core.content, adapter?.content].filter(Boolean).join('\n\n'),
  };
}
