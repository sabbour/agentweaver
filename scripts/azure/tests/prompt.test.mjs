// prompt.test.mjs -- Unit tests for lib/prompt.mjs's pure, TTY-free logic:
// the arrow-key select() reducer (reduceSelectKey) and the numbered-fallback
// answer parser (parseNumberedSelection). These intentionally avoid touching
// real stdin/raw-mode -- select()/text()/secret() themselves are exercised
// indirectly via provision-infra.test.mjs's injected fake `prompt`.

import test from "node:test";
import assert from "node:assert/strict";
import { reduceSelectKey, parseNumberedSelection, resolveTextAnswer, computeSelectWindow } from "../lib/prompt.mjs";

test("reduceSelectKey: Down moves to the next index and wraps past the last", () => {
  const count = 3;
  let state = { index: 0, count };
  state = { index: reduceSelectKey(state, "\x1b[B").index, count };
  assert.equal(state.index, 1);
  state = { index: reduceSelectKey(state, "\x1b[B").index, count };
  assert.equal(state.index, 2);
  // Wraps back to 0 past the last choice.
  const wrapped = reduceSelectKey(state, "\x1b[B");
  assert.equal(wrapped.index, 0);
  assert.equal(wrapped.action, "none");
});

test("reduceSelectKey: Up moves to the previous index and wraps past the first", () => {
  const count = 3;
  const atTop = { index: 0, count };
  const wrapped = reduceSelectKey(atTop, "\x1b[A");
  assert.equal(wrapped.index, 2);
  assert.equal(wrapped.action, "none");

  const middle = { index: 2, count };
  const up = reduceSelectKey(middle, "\x1b[A");
  assert.equal(up.index, 1);
});

test("reduceSelectKey: a digit 1..N (N<=9) jumps straight to that choice and accepts", () => {
  const count = 5;
  const state = { index: 0, count };
  const result = reduceSelectKey(state, "3");
  assert.equal(result.index, 2);
  assert.equal(result.action, "accept");
});

test("reduceSelectKey: a digit greater than the choice count is ignored", () => {
  const count = 3;
  const state = { index: 1, count };
  const result = reduceSelectKey(state, "9");
  assert.equal(result.index, 1);
  assert.equal(result.action, "none");
});

test("reduceSelectKey: Enter (\\r or \\n) accepts the currently highlighted index", () => {
  const state = { index: 2, count: 4 };
  assert.deepEqual(reduceSelectKey(state, "\r"), { index: 2, action: "accept" });
  assert.deepEqual(reduceSelectKey(state, "\n"), { index: 2, action: "accept" });
});

test("reduceSelectKey: Ctrl+C aborts without changing the index", () => {
  const state = { index: 1, count: 4 };
  const result = reduceSelectKey(state, "\x03");
  assert.equal(result.index, 1);
  assert.equal(result.action, "abort");
});

test("reduceSelectKey: unrecognized keys are a no-op", () => {
  const state = { index: 1, count: 4 };
  const result = reduceSelectKey(state, "z");
  assert.equal(result.index, 1);
  assert.equal(result.action, "none");
});

test("parseNumberedSelection: parses a valid 1-based number to a 0-based index", () => {
  assert.equal(parseNumberedSelection("1", 3, undefined), 0);
  assert.equal(parseNumberedSelection("3", 3, undefined), 2);
});

test("parseNumberedSelection: empty answer falls back to the default index when one is set", () => {
  assert.equal(parseNumberedSelection("", 3, 1), 1);
  assert.equal(parseNumberedSelection("", 3, undefined), null);
});

test("parseNumberedSelection: out-of-range or non-numeric answers are invalid", () => {
  assert.equal(parseNumberedSelection("0", 3, undefined), null);
  assert.equal(parseNumberedSelection("4", 3, undefined), null);
  assert.equal(parseNumberedSelection("abc", 3, undefined), null);
});

test("resolveTextAnswer: no validator returns the trimmed answer (or default if blank)", () => {
  assert.deepEqual(resolveTextAnswer("  hello  "), { done: true, value: "hello" });
  assert.deepEqual(resolveTextAnswer("", { default: "fallback" }), { done: true, value: "fallback" });
  assert.deepEqual(resolveTextAnswer(""), { done: true, value: "" });
});

test("resolveTextAnswer: a validator rejects an invalid answer with its error message, surfaced for reprompting", () => {
  const validate = (v) => (v === "good" ? true : `'${v}' is not allowed`);
  const rejected = resolveTextAnswer("bad", { validate });
  assert.equal(rejected.done, false);
  assert.equal(rejected.error, "'bad' is not allowed");
});

test("resolveTextAnswer: invalid then valid input -- reprompt loop eventually accepts the valid value", () => {
  const validate = (v) => (v === "good" ? true : "try again");
  const attempts = ["bad", "still-bad", "good"];
  let outcome;
  for (const attempt of attempts) {
    outcome = resolveTextAnswer(attempt, { validate });
    if (outcome.done) break;
    assert.equal(outcome.error, "try again");
  }
  assert.deepEqual(outcome, { done: true, value: "good" });
});

test("resolveTextAnswer: an empty answer with no default and a validator is rejected (required)", () => {
  const outcome = resolveTextAnswer("   ", { validate: () => true });
  assert.equal(outcome.done, false);
  assert.match(outcome.error, /required/);
});

test("computeSelectWindow: list shorter than the cap shows everything with no indicators", () => {
  const w = computeSelectWindow({ activeIndex: 2, count: 5, maxVisible: 10 });
  assert.deepEqual(w, { start: 0, end: 5, hasAbove: false, hasBelow: false });
});

test("computeSelectWindow: list equal to the cap still shows everything with no indicators", () => {
  const w = computeSelectWindow({ activeIndex: 0, count: 8, maxVisible: 8 });
  assert.deepEqual(w, { start: 0, end: 8, hasAbove: false, hasBelow: false });
});

test("computeSelectWindow: active at the very top clamps the window to the start", () => {
  const w = computeSelectWindow({ activeIndex: 0, count: 20, maxVisible: 7 });
  // 7 lines cap -> 5 item rows + 2 indicator rows.
  assert.equal(w.start, 0);
  assert.equal(w.end, 5);
  assert.equal(w.hasAbove, false);
  assert.equal(w.hasBelow, true);
});

test("computeSelectWindow: active at the very bottom clamps the window to the end", () => {
  const w = computeSelectWindow({ activeIndex: 19, count: 20, maxVisible: 7 });
  assert.equal(w.end, 20);
  assert.equal(w.start, 15);
  assert.equal(w.hasAbove, true);
  assert.equal(w.hasBelow, false);
});

test("computeSelectWindow: active in the middle keeps the window centered", () => {
  const w = computeSelectWindow({ activeIndex: 10, count: 20, maxVisible: 7 });
  const visible = 5;
  assert.equal(w.end - w.start, visible);
  assert.ok(w.start <= 10 && 10 < w.end, "active index is inside the window");
  assert.equal(w.hasAbove, true);
  assert.equal(w.hasBelow, true);
});

test("computeSelectWindow: rendered item count stays constant while scrolling top->bottom", () => {
  const count = 30;
  const maxVisible = 8; // -> 6 item rows every time
  let expected = null;
  for (let active = 0; active < count; active++) {
    const w = computeSelectWindow({ activeIndex: active, count, maxVisible });
    const items = w.end - w.start;
    if (expected === null) expected = items;
    assert.equal(items, expected, `window item count changed at active=${active}`);
    assert.ok(w.start <= active && active < w.end, `active=${active} not visible`);
  }
});

test("computeSelectWindow: a tiny cap still yields at least one visible item", () => {
  const w = computeSelectWindow({ activeIndex: 3, count: 10, maxVisible: 1 });
  assert.ok(w.end - w.start >= 1, "at least one item is always visible");
});
