import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

/**
 * Parse a surface CLI while leaving its option names and argument contract in the
 * owning adapter. A map value is either the destination property or `true` for
 * a value-less flag.
 */
export function parseLifecycleArgs(argv, { initial = {}, options }) {
  const parsed = { ...initial };
  for (let index = 0; index < argv.length; index += 1) {
    const option = argv[index];
    const property = options[option];
    if (!property) throw new Error(`unknown option: ${option.split('=', 1)[0]}`);
    if (property === true) {
      parsed[option.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = true;
      continue;
    }
    parsed[property] = argv[++index];
  }
  return parsed;
}

/** Persist redacted/normalized evidence or verdict JSON with the established path. */
export async function writeLifecycleJson(path, value) {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  return path;
}

/** Load the core persona plus the owning surface adapter without coupling transports. */
export async function loadLifecyclePersona(loadPersona, scenario, surface, { optional = false } = {}) {
  try {
    return await loadPersona(scenario, surface);
  } catch (error) {
    if (optional) return null;
    throw error;
  }
}

/** Keep the common judge invocation at the lifecycle boundary, not in a transport adapter. */
export function judgeLifecycleEvidence(evidence, judgeEvidence, { timeoutMs, judge } = {}) {
  return judgeEvidence(evidence, { timeoutMs, judge });
}

/** Shared deterministic/inconclusive process-code policy for every persona surface. */
export function lifecycleExitCode({ failed = false, inconclusive = false } = {}) {
  if (failed) return 1;
  return inconclusive ? 3 : 0;
}

/** Preserve each adapter's labels and values while sharing their result-line rendering. */
export function formatLifecycleLines(fields) {
  return fields.map(([label, value]) => `${label.padEnd(12)}: ${value}`);
}

/** Standard CLI boundary: redact unexpected errors and retain each adapter's exit code. */
export async function runLifecycleCli(main, { redact, exit = process.exit, error = console.error } = {}) {
  try {
    exit(await main());
  } catch (exception) {
    error(redact(String(exception?.stack ?? exception?.message ?? exception)));
    exit(2);
  }
}
