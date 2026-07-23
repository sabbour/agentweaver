// prompt.test.mjs -- Unit tests for lib/prompt.mjs's pure, TTY-free logic:
// the arrow-key select() reducer (reduceSelectKey) and the numbered-fallback
// answer parser (parseNumberedSelection). These intentionally avoid touching
// real stdin/raw-mode -- select()/text()/secret() themselves are exercised
// indirectly via provision-infra.test.mjs's injected fake `prompt`.

import test from "node:test";
import assert from "node:assert/strict";
import { reduceSelectKey, parseNumberedSelection } from "../lib/prompt.mjs";

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
