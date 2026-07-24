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
 * presses enter without typing anything) and an optional validator. When
 * `opts.validate` is supplied, invalid answers (including an empty answer
 * with no default) print the validator's error message and reprompt instead
 * of ever returning or throwing -- this keeps the installer from crashing or
 * silently proceeding on bad input.
 * @param {string} question
 * @param {{ default?: string, validate?: (value: string) => true | string }} [opts]
 */
export async function text(question, opts = {}) {
  assertInteractive(question);
  const suffix = opts.default !== undefined ? ` [${opts.default}]` : "";
  const rl = openInterface();
  try {
    for (;;) {
      const answer = await rl.question(`${question}${suffix}: `);
      const outcome = resolveTextAnswer(answer, opts);
      if (outcome.done) {
        return outcome.value;
      }
      process.stdout.write(`${outcome.error}\n`);
    }
  } finally {
    rl.close();
  }
}

/**
 * Pure raw-answer -> outcome reducer for text(), extracted so the
 * validate/reprompt loop is unit-testable without a real TTY. Given the
 * raw (untrimmed) answer typed by the user, returns either
 * `{ done: true, value }` (accept and return `value`) or
 * `{ done: false, error }` (print `error` and reprompt).
 *
 * Required-by-default: a prompt with no `default` key is required, so a blank
 * answer reprompts. Prompts that allow blank input opt out with an explicit
 * `default` (commonly "").
 * @param {string} rawAnswer
 * @param {{ default?: string, validate?: (value: string) => true | string }} [opts]
 * @returns {{done: true, value: string} | {done: false, error: string}}
 */
export function resolveTextAnswer(rawAnswer, opts = {}) {
  const trimmed = String(rawAnswer ?? "").trim();
  const hasDefault = opts.default !== undefined;
  const value = trimmed.length === 0 && hasDefault ? opts.default : trimmed;
  // A prompt with no configured default is required. Reprompt on empty rather
  // than letting a blank required value flow downstream and fail much later
  // (e.g. an empty GitHub client secret surfacing as "credentials missing"
  // only after ~15 min of cluster provisioning). Prompts that legitimately
  // allow blank input pass an explicit `default` (often "") to opt out.
  if (value.length === 0 && !hasDefault) {
    return { done: false, error: "This value is required." };
  }
  if (typeof opts.validate !== "function") {
    return { done: true, value };
  }
  if (value.length === 0) {
    return { done: false, error: "This value is required." };
  }
  const result = opts.validate(value);
  if (result === true) {
    return { done: true, value };
  }
  return { done: false, error: typeof result === "string" ? result : "Invalid value." };
}

/**
 * True when the current process can flip stdin into raw mode, i.e. arrow-key
 * navigation is actually possible (a real TTY, not a pipe/redirect/CI
 * pseudo-tty without setRawMode).
 */
function rawModeAvailable() {
  return Boolean(process.stdin.isTTY && typeof process.stdin.setRawMode === "function");
}

/**
 * Pure keypress -> next-state reducer for the arrow-key select() UI. Exported
 * so it is unit-testable without a real TTY/raw-mode stdin.
 * @param {{index:number, count:number}} state Current highlighted index and
 *   total choice count (0-based index).
 * @param {string} key One decoded key: `"\x1b[A"` (up), `"\x1b[B"` (down),
 *   `"\r"`/`"\n"` (enter), `"\x03"` (Ctrl+C), a single digit char, or
 *   anything else (ignored).
 * @returns {{index:number, action:"none"|"accept"|"abort"}}
 */
export function reduceSelectKey(state, key) {
  const { index, count } = state;
  if (key === "\x1b[A") {
    return { index: (index - 1 + count) % count, action: "none" };
  }
  if (key === "\x1b[B") {
    return { index: (index + 1) % count, action: "none" };
  }
  if (key === "\r" || key === "\n") {
    return { index, action: "accept" };
  }
  if (key === "\x03") {
    return { index, action: "abort" };
  }
  if (count <= 9 && /^[1-9]$/.test(key)) {
    const digit = Number.parseInt(key, 10);
    if (digit <= count) {
      return { index: digit - 1, action: "accept" };
    }
  }
  return { index, action: "none" };
}

/**
 * Pure viewport calculation for the arrow-key select() UI. Given the active
 * index, total choice count, and the maximum number of *lines* the list block
 * may occupy, returns the half-open window `[start, end)` of items to render
 * plus whether items are hidden above/below. Exported so the scrolling logic
 * is unit-testable without a real TTY.
 *
 * When everything fits (`count <= maxVisible`) the whole list is returned with
 * no scroll indicators. Otherwise two of the `maxVisible` lines are reserved
 * for the `↑ (n more)` / `↓ (n more)` indicator rows and the remaining rows
 * form a window kept centered on the active index and clamped to the ends.
 * @param {{activeIndex:number, count:number, maxVisible:number}} state
 * @returns {{start:number, end:number, hasAbove:boolean, hasBelow:boolean}}
 */
export function computeSelectWindow({ activeIndex, count, maxVisible }) {
  const cap = Math.max(1, maxVisible);
  if (count <= cap) {
    return { start: 0, end: count, hasAbove: false, hasBelow: false };
  }
  const visible = Math.max(1, cap - 2); // reserve two rows for scroll indicators
  let start = activeIndex - Math.floor(visible / 2);
  if (start < 0) start = 0;
  if (start > count - visible) start = count - visible;
  const end = start + visible;
  return { start, end, hasAbove: start > 0, hasBelow: end < count };
}

/**
 * The constant number of terminal lines the select() block occupies for a
 * given choice count and viewport cap. This MUST stay constant across every
 * redraw within one select() call so the in-place cursor-up math is exact;
 * when the list scrolls, the indicator rows are drawn as blanks at the edges
 * rather than being added/removed (which would desync the redraw and cause the
 * list to reprint endlessly).
 */
function selectRenderHeight(count, maxVisible) {
  const cap = Math.max(1, maxVisible);
  if (count <= cap) return count;
  return Math.max(1, cap - 2) + 2;
}

/**
 * Redraws the choice list in place (used by the arrow-key raw-mode path).
 * `prevHeight` is the number of lines the previous render wrote (0 on the very
 * first render); the cursor is moved up by exactly that many lines so the new
 * block overwrites the old one instead of appending beneath it.
 */
function renderSelectList(normalized, activeIndex, maxVisible, prevHeight) {
  const count = normalized.length;
  const showIndicators = count > Math.max(1, maxVisible);
  const { start, end, hasAbove, hasBelow } = computeSelectWindow({ activeIndex, count, maxVisible });

  const lines = [];
  if (showIndicators) {
    lines.push(hasAbove ? `  \x1b[2m↑ (${start} more)\x1b[22m` : "");
  }
  for (let i = start; i < end; i++) {
    const label = redact(normalized[i].label);
    lines.push(i === activeIndex ? `\x1b[7m❯ ${label}\x1b[27m` : `  ${label}`);
  }
  if (showIndicators) {
    lines.push(hasBelow ? `  \x1b[2m↓ (${count - end} more)\x1b[22m` : "");
  }

  // On redraw, the cursor sits at the END of the last line of the previous
  // block (we never emit a trailing newline, so the block never forces the
  // terminal to scroll). Move up to the first line, return to column 0, and
  // clear everything below before repainting. Emitting a trailing newline here
  // instead would scroll the terminal whenever the block sits at the bottom,
  // desyncing the cursor-up count and reprinting the list endlessly.
  if (prevHeight > 0) {
    process.stdout.write(`\x1b[${prevHeight - 1}A\r\x1b[0J`);
  }
  // Clear each line before its content so leftover glyphs from a wider prior
  // render never linger, and join without a trailing newline.
  process.stdout.write(lines.map((l) => `\x1b[2K${l}`).join("\n"));
}

/**
 * Clears the previously rendered choice list block. The cursor sits at the end
 * of the block's last line (no trailing newline is ever emitted); move up to
 * the first line and clear to the end of the screen, leaving the cursor at the
 * block's start so the caller can print the final selection summary there.
 */
function clearSelectList(height) {
  if (height > 0) {
    process.stdout.write(`\x1b[${height - 1}A`);
  }
  process.stdout.write("\r\x1b[0J");
}

/**
 * Maximum number of terminal lines the select() list block may use. Derived
 * from the terminal height, reserving a few rows for the question line and
 * surrounding output, with a sane fallback when the row count is unknown.
 */
function selectMaxVisible() {
  const rows = Number.isInteger(process.stdout.rows) ? process.stdout.rows : 12;
  return Math.max(3, rows - 3);
}

/**
 * Arrow-key navigable choice prompt (raw-mode stdin). Up/Down move the
 * highlight (wrapping top/bottom), Enter accepts, a digit 1..N (when
 * N<=9) jumps straight to that choice, and Ctrl+C aborts the process
 * cleanly. Always restores stdin state (raw mode off, listener removed,
 * paused) in a `finally`, even on abort/error, so the parent shell never
 * inherits a broken terminal.
 */
async function selectArrowKey(question, normalized, opts) {
  const defaultIndex =
    Number.isInteger(opts.default) && opts.default >= 0 && opts.default < normalized.length ? opts.default : 0;
  let state = { index: defaultIndex, count: normalized.length };

  const maxVisible = selectMaxVisible();
  const renderHeight = selectRenderHeight(normalized.length, maxVisible);

  process.stdout.write(`${question}\n`);
  renderSelectList(normalized, state.index, maxVisible, 0);

  let onData;
  process.stdin.resume();
  process.stdin.setRawMode(true);
  try {
    const outcome = await new Promise((resolve) => {
      let pending = "";
      const handleKey = (key) => {
        const next = reduceSelectKey(state, key);
        state = { index: next.index, count: state.count };
        if (next.action === "abort") {
          resolve({ aborted: true });
          return;
        }
        if (next.action === "accept") {
          resolve({ index: state.index });
          return;
        }
        renderSelectList(normalized, state.index, maxVisible, renderHeight);
      };
      onData = (chunk) => {
        pending += chunk.toString("utf8");
        while (pending.length > 0) {
          if (pending[0] === "\x1b") {
            // Escape sequences of interest are 3 bytes (\x1b[A / \x1b[B); if
            // a chunk boundary split it, wait for the rest to arrive.
            if (pending.length < 3) return;
            const key = pending.slice(0, 3);
            pending = pending.slice(3);
            handleKey(key);
          } else {
            const key = pending[0];
            pending = pending.slice(1);
            handleKey(key);
          }
        }
      };
      process.stdin.on("data", onData);
    });

    if (outcome.aborted) {
      process.stdout.write("\n");
      process.exitCode = 130;
      process.exit(130);
      return undefined; // unreachable, keeps linters happy
    }

    clearSelectList(renderHeight);
    process.stdout.write(`${question}: ${redact(normalized[outcome.index].label)}\n`);
    return normalized[outcome.index].value;
  } finally {
    process.stdin.setRawMode(false);
    process.stdin.pause();
    if (onData) process.stdin.removeListener("data", onData);
  }
}

/**
 * Pure parser for the numbered-fallback prompt's typed answer. Exported so
 * the "numbered fallback still parses input as before" behavior is
 * unit-testable without a real TTY.
 * @param {string} answer Trimmed raw text typed by the user.
 * @param {number} count Number of choices (1-based valid range is 1..count).
 * @param {number|undefined} defaultIndex 0-based default index, if any.
 * @returns {number|null} 0-based index, or null when the answer is invalid.
 */
export function parseNumberedSelection(answer, count, defaultIndex) {
  if (answer.length === 0 && defaultIndex !== undefined) {
    return defaultIndex;
  }
  const index = Number.parseInt(answer, 10);
  if (Number.isInteger(index) && index >= 1 && index <= count) {
    return index - 1;
  }
  return null;
}

/** The original numbered-input select() body, used as a fallback when raw mode is unavailable. */
async function selectNumbered(question, normalized, opts) {
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
      const chosen = parseNumberedSelection(answer, normalized.length, defaultIndex);
      if (chosen !== null) {
        return normalized[chosen].value;
      }
      process.stdout.write(`Please enter a number between 1 and ${normalized.length}.\n`);
    }
  } finally {
    rl.close();
  }
}

/**
 * Single-choice prompt. Uses arrow-key navigation (Up/Down + Enter, plus a
 * 1..N digit shortcut when there are 9 or fewer choices, Ctrl+C to abort)
 * when raw-mode stdin is available; falls back to the classic numbered
 * "type a digit" prompt otherwise (no TTY raw-mode support, e.g. some CI
 * pseudo-ttys or piped input that still reports isInteractive() === true).
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
  if (rawModeAvailable()) {
    return selectArrowKey(question, normalized, opts);
  }
  return selectNumbered(question, normalized, opts);
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
  const maxAttempts = 5;
  try {
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      // Write the prompt visibly FIRST, then mute so only the typed characters
      // are swallowed. Muting before rl.question() would also swallow the prompt
      // text itself -- the user would see nothing and the process would appear to
      // hang waiting on invisible input.
      process.stdout.write(`${question}: `);
      mutableStdout.muted = true;
      const answer = await rl.question("");
      mutableStdout.muted = false;
      process.stdout.write("\n");
      const trimmed = answer.trim();
      if (trimmed.length > 0) {
        return trimmed;
      }
      // A secret is never legitimately empty. Reprompt with a clear message
      // instead of letting an empty value flow downstream and fail far later
      // (e.g. after ~15 min of cluster provisioning) as "credentials missing".
      process.stdout.write("This value is required and cannot be empty.\n");
    }
    throw new Error(`${question}: no value provided after ${maxAttempts} attempts.`);
  } finally {
    mutableStdout.muted = false;
    rl.close();
  }
}

// Minimal Writable-like shim readline can write its prompt/echo through. When
// `muted` is true, keystroke echo is swallowed but the initial prompt text
// (written once, before muting begins) still appears.
export class MutableStdout {
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

  // readline (readlinePromises) treats `output` as a stream and, on a TTY,
  // attaches a "resize" listener via output.on(...). Delegate EventEmitter and
  // TTY-probe members to the real stdout so the shim behaves like a stream.
  get isTTY() {
    return this.realStdout.isTTY;
  }

  get rows() {
    return this.realStdout.rows;
  }

  on(...args) {
    this.realStdout.on(...args);
    return this;
  }

  once(...args) {
    this.realStdout.once(...args);
    return this;
  }

  off(...args) {
    this.realStdout.off?.(...args);
    return this;
  }

  removeListener(...args) {
    this.realStdout.removeListener(...args);
    return this;
  }

  addListener(...args) {
    this.realStdout.addListener(...args);
    return this;
  }

  emit(...args) {
    return this.realStdout.emit(...args);
  }

  end(...args) {
    if (typeof args[args.length - 1] === "function") args[args.length - 1]();
    return this;
  }
}
