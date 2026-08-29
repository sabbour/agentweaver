// secret.mjs -- Secret redaction registry shared by log.mjs and exec.mjs.
//
// Any value that must never reach a log line, a rendered/echoed command, or a
// captured-output print (OAuth client secret, PATs, etc.) is registered here
// exactly once, as soon as it becomes known. Every logging/exec surface in
// this toolchain MUST run text through redact()/redactArgs() before it is
// written anywhere (stdout, files, error messages).

import fs from "node:fs";
import path from "node:path";

const REDACTED = "***REDACTED***";

// value -> label (label is only used for internal bookkeeping/debug; the
// redacted output is always the generic REDACTED marker so a secret's shape
// or origin can't be inferred from log output).
const registry = new Map();

/**
 * Register a secret value for redaction. No-ops for empty/nullish values so
 * callers can unconditionally register optional fields.
 * @param {unknown} value
 * @param {string} [label] Purely descriptive; never printed.
 */
export function registerSecret(value, label = "secret") {
  if (value === undefined || value === null) return;
  const str = String(value);
  if (str.length === 0) return;
  registry.set(str, label);
}

/** Register multiple secrets at once. Accepts an object of name -> value. */
export function registerSecrets(values) {
  if (!values) return;
  for (const [label, value] of Object.entries(values)) {
    registerSecret(value, label);
  }
}

/** Remove all registered secrets. Primarily for test isolation. */
export function clearSecrets() {
  registry.clear();
}

/** Returns true if any secrets are currently registered. */
export function hasSecrets() {
  return registry.size > 0;
}

/**
 * Redact every registered secret value found in `text`. Longest values are
 * replaced first so a secret that happens to be a substring of another
 * registered value doesn't leave a partial fragment behind.
 * @param {string} text
 * @returns {string}
 */
export function redact(text) {
  if (text === undefined || text === null) return text;
  let out = String(text);
  if (registry.size === 0) return out;
  const values = [...registry.keys()].sort((a, b) => b.length - a.length);
  for (const value of values) {
    if (!value) continue;
    out = out.split(value).join(REDACTED);
  }
  return out;
}

/** Redact each element of an argv-style array (used before logging commands). */
export function redactArgs(args) {
  return (args ?? []).map((arg) => redact(String(arg)));
}

export const REDACTED_MARKER = REDACTED;

/**
 * Writes `value` to a private (0600 where the platform supports it), unique
 * scratch file under `scratchDir` and invokes `callback(filePath)`, always
 * deleting the file afterwards -- even if `callback` throws. Use this instead
 * of passing a secret value as a CLI argument: argv is visible to any
 * co-resident process/user via `ps`/`/proc/<pid>/cmdline` for the whole
 * lifetime of the command, whereas a file path is not sensitive and the file
 * itself is removed as soon as the command that reads it exits.
 *
 * @template T
 * @param {string} scratchDir
 * @param {string} filenamePrefix
 * @param {string} value
 * @param {(filePath: string) => Promise<T> | T} callback
 * @param {{ fsImpl?: typeof fs }} [opts]
 * @returns {Promise<T>}
 */
export async function withSecretFile(scratchDir, filenamePrefix, value, callback, { fsImpl = fs } = {}) {
  fsImpl.mkdirSync(scratchDir, { recursive: true });
  const filePath = path.join(scratchDir, `${filenamePrefix}-${process.pid}-${Date.now()}.tmp`);
  fsImpl.writeFileSync(filePath, value, { mode: 0o600 });
  try {
    // Best-effort: writeFileSync's mode is umask-subject on POSIX, so set it
    // explicitly too. No-op (and harmless) on Windows, which doesn't support
    // POSIX file mode bits.
    fsImpl.chmodSync(filePath, 0o600);
  } catch {
    // Ignore -- not fatal if chmod isn't supported (e.g. some Windows setups).
  }
  try {
    return await callback(filePath);
  } finally {
    fsImpl.rmSync(filePath, { force: true });
  }
}
