// secret.mjs -- Secret redaction registry shared by log.mjs and exec.mjs.
//
// Any value that must never reach a log line, a rendered/echoed command, or a
// captured-output print (OAuth client secret, PATs, etc.) is registered here
// exactly once, as soon as it becomes known. Every logging/exec surface in
// this toolchain MUST run text through redact()/redactArgs() before it is
// written anywhere (stdout, files, error messages).

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
