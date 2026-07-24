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

const spinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

/**
 * Runs an async task while showing a live progress indicator so long-running
 * Azure calls don't look like the installer has hung. On a TTY an animated
 * spinner is drawn in place and replaced by an `[OK] label` line when the task
 * resolves; on a non-TTY (or when output is redirected) a single `label...`
 * line is printed up front, then `[OK] label`. The indicator is always torn
 * down (spinner cleared, interval stopped) even when the task rejects.
 * @template T
 * @param {string} label Human-readable description, e.g. "Loading resource groups".
 * @param {() => Promise<T>} task The async work to run.
 * @returns {Promise<T>}
 */
export async function withProgress(label, task) {
  if (!isTTY) {
    write(process.stdout, `${label}...`);
    const result = await task();
    ok(label);
    return result;
  }
  let i = 0;
  const draw = () => process.stdout.write(`\r\x1b[2K${color("cyan", spinnerFrames[i])} ${redact(label)}...`);
  draw();
  const timer = setInterval(() => {
    i = (i + 1) % spinnerFrames.length;
    draw();
  }, 80);
  if (typeof timer.unref === "function") timer.unref();
  try {
    const result = await task();
    clearInterval(timer);
    process.stdout.write("\r\x1b[2K");
    ok(label);
    return result;
  } catch (err) {
    clearInterval(timer);
    process.stdout.write("\r\x1b[2K");
    throw err;
  }
}
