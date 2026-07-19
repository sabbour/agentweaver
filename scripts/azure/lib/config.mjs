// config.mjs -- Input/config model with precedence:
//
//   flags > env > params-file > detected-defaults > prompt
//
// Config is resolved ONCE, in full, and validated BEFORE any side effect
// runs (no partial provisioning against half-resolved/invalid input). The
// resolved object is what every later phase (steps/deploy/upgrade/etc.)
// consumes -- callers should not re-read process.env or re-parse flags
// downstream of resolveConfig().

import fs from "node:fs";
import { NonInteractiveError } from "./prompt.mjs";

/**
 * @typedef {Object} FieldSpec
 * @property {string} [env] Environment variable name backing this field
 *   (defaults to the field's own key, uppercased is NOT assumed -- callers
 *   pass the exact env var name to match legacy scripts' naming).
 * @property {*} [default] Detected/static default value.
 * @property {boolean} [required] If true and still unresolved after
 *   flags/env/params-file/defaults, a prompt is attempted; if prompting is
 *   unavailable (non-interactive) or fails, resolveConfig() throws.
 * @property {(raw: unknown) => unknown} [parse] Optional coercion applied to
 *   whatever value is resolved (any source) before validation.
 * @property {(value: unknown, config: Record<string, unknown>) => string|void} [validate]
 *   Optional validator; return an error message string to fail, or nothing/
 *   undefined to pass. Runs after ALL fields have an initial value so
 *   cross-field validation is possible.
 * @property {(config: Record<string, unknown>) => Promise<unknown>} [prompt]
 *   Optional custom prompt function used when the field is still unresolved
 *   and required. Receives the config resolved so far.
 * @property {boolean} [secret] If true, the resolved value is registered for
 *   log/exec redaction (see secret.mjs) as soon as it is resolved.
 */

/**
 * Strips `//` line comments and `/* *\/` block comments plus trailing commas
 * from JSON-with-comments text, then JSON.parses it. This is a deliberately
 * light-touch, best-effort JSONC tolerance -- NOT a full JSONC parser (it
 * does not understand comment-like sequences inside strings that contain
 * `//`; params files should stick to plain JSON when in doubt). Plain JSON
 * always parses correctly through this path since the stripping is a no-op.
 */
export function parseJsonc(text) {
  let out = "";
  let inString = false;
  let stringQuote = "";
  let inLineComment = false;
  let inBlockComment = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    const next = text[i + 1];
    if (inLineComment) {
      if (ch === "\n") {
        inLineComment = false;
        out += ch;
      }
      continue;
    }
    if (inBlockComment) {
      if (ch === "*" && next === "/") {
        inBlockComment = false;
        i++;
      }
      continue;
    }
    if (inString) {
      out += ch;
      if (ch === "\\") {
        out += next;
        i++;
        continue;
      }
      if (ch === stringQuote) inString = false;
      continue;
    }
    if (ch === '"' || ch === "'") {
      inString = true;
      stringQuote = ch;
      out += ch;
      continue;
    }
    if (ch === "/" && next === "/") {
      inLineComment = true;
      i++;
      continue;
    }
    if (ch === "/" && next === "*") {
      inBlockComment = true;
      i++;
      continue;
    }
    out += ch;
  }
  // Strip trailing commas before a closing ] or } (light JSONC tolerance).
  out = out.replace(/,(\s*[}\]])/g, "$1");
  return JSON.parse(out);
}

/**
 * Loads a params file from disk. JSON is always supported; JSONC (comments,
 * trailing commas) is tolerated on a best-effort basis via parseJsonc().
 * Returns {} if `filePath` is falsy (no params file specified) -- this is
 * NOT an error, since params files are optional.
 * @param {string|undefined} filePath
 */
export function loadParamsFile(filePath) {
  if (!filePath) return {};
  const text = fs.readFileSync(filePath, "utf8");
  return parseJsonc(text);
}

/**
 * Resolves a single field's raw value (before parse/validate) and the source
 * it came from, following flags > env > params-file > defaults precedence.
 * Exported mainly for testability/introspection.
 */
export function resolveRawValue(name, spec, { flags = {}, env = process.env, paramsFile = {} } = {}) {
  if (Object.prototype.hasOwnProperty.call(flags, name) && flags[name] !== undefined) {
    return { value: flags[name], source: "flag" };
  }
  const envKey = spec.env ?? name;
  if (env[envKey] !== undefined && env[envKey] !== "") {
    return { value: env[envKey], source: "env" };
  }
  if (Object.prototype.hasOwnProperty.call(paramsFile, name) && paramsFile[name] !== undefined) {
    return { value: paramsFile[name], source: "params-file" };
  }
  if (Object.prototype.hasOwnProperty.call(spec, "default")) {
    return { value: spec.default, source: "default" };
  }
  return { value: undefined, source: "unset" };
}

/**
 * Resolves a full config object from a field-spec schema, applying
 * flags > env > params-file > detected-defaults > prompt precedence per
 * field, then running parse/validate for every field, ONCE, before
 * returning. Throws (does not partially resolve) if any required field
 * cannot be resolved, or any validator fails.
 *
 * @param {Record<string, FieldSpec>} schema
 * @param {{ flags?: Record<string,unknown>, env?: Record<string,string>, paramsFile?: Record<string,unknown> }} sources
 * @returns {Promise<Record<string, unknown>>}
 */
export async function resolveConfig(schema, sources = {}) {
  const { registerSecret } = await import("./secret.mjs");
  const config = {};
  const resolvedSources = {};

  for (const [name, spec] of Object.entries(schema)) {
    let { value, source } = resolveRawValue(name, spec, sources);

    if (value === undefined && spec.required) {
      if (typeof spec.prompt === "function") {
        try {
          value = await spec.prompt(config);
          source = "prompt";
        } catch (err) {
          if (err instanceof NonInteractiveError) {
            throw new Error(
              `Missing required config '${name}' and no interactive TTY is available to prompt for it. ` +
                `Supply it via a flag, environment variable${spec.env ? ` (${spec.env})` : ""}, or params file.`,
            );
          }
          throw err;
        }
      } else {
        throw new Error(
          `Missing required config '${name}'. Supply it via a flag, environment variable` +
            `${spec.env ? ` (${spec.env})` : ""}, or params file.`,
        );
      }
    }

    if (value !== undefined && typeof spec.parse === "function") {
      value = spec.parse(value);
    }

    config[name] = value;
    resolvedSources[name] = source;

    if (spec.secret && value !== undefined) {
      registerSecret(value, name);
    }
  }

  const errors = [];
  for (const [name, spec] of Object.entries(schema)) {
    if (typeof spec.validate !== "function") continue;
    const result = spec.validate(config[name], config);
    if (typeof result === "string" && result.length > 0) {
      errors.push(`${name}: ${result}`);
    }
  }
  if (errors.length > 0) {
    throw new Error(`Config validation failed:\n  ${errors.join("\n  ")}`);
  }

  Object.defineProperty(config, "__sources", { value: resolvedSources, enumerable: false });
  return config;
}
