// log.mjs -- Leveled step logging matching the legacy scripts' echo UX.
//
// The bash/PowerShell scripts under scripts/aks/ use a small informal
// vocabulary: "=== Section ===" headers, "[OK] ...", "[SKIP] ...",
// "WARNING: ..." and plain informational echoes. This module reproduces that
// vocabulary so ported steps read the same in a terminal, while adding
// colorization (TTY only) and secret redaction on every line.

import { redact } from "./secret.mjs";

const isTTY = Boolean(process.stdout && process.stdout.isTTY);
// Respect the NO_COLOR convention (https://no-color.org/) in addition to TTY
// detection.
const colorEnabled = isTTY && !process.env.NO_COLOR;

const codes = {
  reset: "\x1b[0m",
  bold: "\x1b[1m",
  dim: "\x1b[2m",
  red: "\x1b[31m",
  green: "\x1b[32m",
  yellow: "\x1b[33m",
  cyan: "\x1b[36m",
  gray: "\x1b[90m",
};

function color(code, text) {
  if (!colorEnabled) return text;
  return `${codes[code]}${text}${codes.reset}`;
}

function write(stream, line) {
  stream.write(`${redact(line)}\n`);
}

/** Prints a "=== Title ===" section header, matching the bash scripts. */
export function section(title) {
  const line = `=== ${title} ===`;
  write(process.stdout, color("bold", line));
}

/** Plain informational line (matches bare `echo "..."`). */
export function info(message) {
  write(process.stdout, message);
}

/** `[OK] message` — a step completed successfully. */
export function ok(message) {
  write(process.stdout, `${color("green", "[OK]")} ${message}`);
}

/** `[SKIP] message` — a step was skipped (already satisfied / not needed). */
export function skip(message) {
  write(process.stdout, `${color("yellow", "[SKIP]")} ${message}`);
}

/** `WARNING: message` on stderr, matching the bash scripts' warnings. */
export function warn(message) {
  write(process.stderr, `${color("yellow", "WARNING:")} ${message}`);
}

/** `ERROR: message` on stderr. */
export function error(message) {
  write(process.stderr, `${color("red", "ERROR:")} ${message}`);
}

/** Debug-only line, gated on DEBUG or AGENTWEAVER_DEBUG env vars. */
export function debug(message) {
  if (!process.env.DEBUG && !process.env.AGENTWEAVER_DEBUG) return;
  write(process.stderr, `${color("gray", "[DEBUG]")} ${message}`);
}

/** `  key: value` indented summary line, matching the variables summary block. */
export function field(label, value) {
  write(process.stdout, `  ${label}: ${color("cyan", String(value))}`);
}

/** Logs a command about to run (redacted), used by exec.mjs's dry-run mode. */
export function command(cmdLine) {
  write(process.stdout, `${color("dim", "$")} ${color("dim", cmdLine)}`);
}

export const isColorEnabled = () => colorEnabled;
