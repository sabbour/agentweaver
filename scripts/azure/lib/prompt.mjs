// prompt.mjs -- Interactive prompts via node:readline/promises.
//
// Non-interactive (no TTY, or CI-style environment) MUST NOT prompt: every
// helper here throws NonInteractiveError instead of hanging on a read that
// will never come, so callers can catch it and fall back to flags/env/params
// file/defaults per the config precedence (flags > env > params-file >
// detected-defaults > prompt).

import * as readlinePromises from "node:readline/promises";
import { redact } from "./secret.mjs";

export class NonInteractiveError extends Error {
  constructor(question) {
    super(`Cannot prompt (no interactive TTY available): ${question}`);
    this.name = "NonInteractiveError";
  }
}

/**
 * True when both stdin and stdout are TTYs, i.e. a human is plausibly at the
 * keyboard. CI runners, piped input, and `node script.mjs < params` all
 * report false here.
 */
export function isInteractive() {
  return Boolean(process.stdin.isTTY && process.stdout.isTTY);
}

function assertInteractive(question) {
  if (!isInteractive()) {
    throw new NonInteractiveError(question);
  }
}

function openInterface(opts = {}) {
  return readlinePromises.createInterface({
    input: process.stdin,
    output: process.stdout,
    terminal: true,
    ...opts,
  });
}

/**
 * Free-text prompt with an optional default value (used when the user
 * presses enter without typing anything).
 * @param {string} question
 * @param {{ default?: string }} [opts]
 */
export async function text(question, opts = {}) {
  assertInteractive(question);
  const suffix = opts.default !== undefined ? ` [${opts.default}]` : "";
  const rl = openInterface();
  try {
    const answer = await rl.question(`${question}${suffix}: `);
    const trimmed = answer.trim();
    if (trimmed.length === 0 && opts.default !== undefined) return opts.default;
    return trimmed;
  } finally {
    rl.close();
  }
}

/**
 * Numbered single-choice prompt.
 * @param {string} question
 * @param {Array<string|{label:string,value:any}>} choices
 * @param {{ default?: number }} [opts] default is a 0-based index.
 * @returns {Promise<any>} the selected choice's `value` (or the string itself).
 */
export async function select(question, choices, opts = {}) {
  assertInteractive(question);
  if (!Array.isArray(choices) || choices.length === 0) {
    throw new Error("select() requires a non-empty choices array");
  }
  const normalized = choices.map((c) =>
    typeof c === "string" ? { label: c, value: c } : c,
  );
  const rl = openInterface();
  try {
    process.stdout.write(`${question}\n`);
    normalized.forEach((c, i) => {
      process.stdout.write(`  ${i + 1}) ${redact(c.label)}\n`);
    });
    const defaultIndex = opts.default;
    const suffix = defaultIndex !== undefined ? ` [${defaultIndex + 1}]` : "";
    for (;;) {
      const answer = (await rl.question(`Select 1-${normalized.length}${suffix}: `)).trim();
      if (answer.length === 0 && defaultIndex !== undefined) {
        return normalized[defaultIndex].value;
      }
      const index = Number.parseInt(answer, 10);
      if (Number.isInteger(index) && index >= 1 && index <= normalized.length) {
        return normalized[index - 1].value;
      }
      process.stdout.write(`Please enter a number between 1 and ${normalized.length}.\n`);
    }
  } finally {
    rl.close();
  }
}

/**
 * Yes/no confirmation prompt.
 * @param {string} question
 * @param {{ default?: boolean }} [opts]
 */
export async function confirm(question, opts = {}) {
  assertInteractive(question);
  const hasDefault = opts.default !== undefined;
  const hint = hasDefault ? (opts.default ? "Y/n" : "y/N") : "y/n";
  const rl = openInterface();
  try {
    for (;;) {
      const answer = (await rl.question(`${question} (${hint}): `)).trim().toLowerCase();
      if (answer.length === 0 && hasDefault) return opts.default;
      if (["y", "yes"].includes(answer)) return true;
      if (["n", "no"].includes(answer)) return false;
      process.stdout.write("Please answer 'y' or 'n'.\n");
    }
  } finally {
    rl.close();
  }
}

/**
 * Prompts for a secret value without echoing typed characters to the
 * terminal. Implemented by muting the readline interface's output writer
 * while the question is being answered (a well-known node:readline trick,
 * since readline has no built-in "hidden input" mode).
 * @param {string} question
 */
export async function secret(question) {
  assertInteractive(question);
  const mutableStdout = new MutableStdout(process.stdout);
  const rl = openInterface({ output: mutableStdout, input: process.stdin });
  try {
    mutableStdout.muted = true;
    const answer = await rl.question(`${question}: `);
    process.stdout.write("\n");
    return answer.trim();
  } finally {
    mutableStdout.muted = false;
    rl.close();
  }
}

// Minimal Writable-like shim readline can write its prompt/echo through. When
// `muted` is true, keystroke echo is swallowed but the initial prompt text
// (written once, before muting begins) still appears.
class MutableStdout {
  constructor(realStdout) {
    this.realStdout = realStdout;
    this.muted = false;
  }

  write(chunk, encoding, callback) {
    if (!this.muted) {
      return this.realStdout.write(chunk, encoding, callback);
    }
    // Swallow echoed input while muted; readline still needs write() to
    // "succeed" so it doesn't stall.
    if (typeof encoding === "function") encoding();
    else if (typeof callback === "function") callback();
    return true;
  }

  // readline probes these for cursor movement; delegate harmlessly.
  get columns() {
    return this.realStdout.columns;
  }

  clearLine(...args) {
    return this.muted ? true : this.realStdout.clearLine(...args);
  }

  cursorTo(...args) {
    return this.muted ? true : this.realStdout.cursorTo(...args);
  }

  moveCursor(...args) {
    return this.muted ? true : this.realStdout.moveCursor(...args);
  }
}
