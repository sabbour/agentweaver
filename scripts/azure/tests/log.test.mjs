// Tests for the visual log helpers (banner, rule, step). Tests run without a
// TTY, so color is disabled and output is deterministic plain text -- we assert
// on structure/content rather than escape codes.
import test from "node:test";
import assert from "node:assert/strict";

import * as log from "../lib/log.mjs";

/** Captures everything written to process.stdout while `fn` runs. */
async function captureStdout(fn) {
  const lines = [];
  const original = process.stdout.write;
  process.stdout.write = (chunk, ...rest) => {
    lines.push(String(chunk));
    const cb = rest.find((a) => typeof a === "function");
    if (cb) cb();
    return true;
  };
  try {
    await fn();
  } finally {
    process.stdout.write = original;
  }
  return lines.join("");
}

test("banner: draws a box that frames the title and any subtitle lines", async () => {
  const out = await captureStdout(() => log.banner("Hello", "world"));
  assert.match(out, /╭─+╮/, "has a top border");
  assert.match(out, /╰─+╯/, "has a bottom border");
  assert.ok(out.includes("Hello"), "includes the title");
  assert.ok(out.includes("world"), "includes the subtitle");
  // Each content line is wrapped in vertical bars.
  assert.match(out, /│ Hello\s+│/);
});

test("banner: box width tracks the longest line so borders enclose the text", async () => {
  const out = await captureStdout(() => log.banner("short", "a much longer subtitle line"));
  const topBorder = out.split("\n").find((l) => l.startsWith("╭"));
  const longest = "a much longer subtitle line".length;
  // top border = tl + h*(inner+2) + tr, inner = longest line length.
  assert.equal(topBorder.length, longest + 4);
});

test("step: prints a numbered header and a percentage progress bar", async () => {
  const out = await captureStdout(() => log.step(3, 10, "Provisioning monitoring"));
  assert.ok(out.includes("Step 3/10"), "shows the step counter");
  assert.ok(out.includes("Provisioning monitoring"), "shows the title");
  assert.ok(out.includes("30%"), "shows the computed percentage");
  assert.match(out, /[█░]/, "renders a progress bar");
});

test("step: clamps ratio so the bar never overflows or underflows", async () => {
  const over = await captureStdout(() => log.step(12, 10, "past the end"));
  assert.ok(over.includes("100%"));
  const zero = await captureStdout(() => log.step(0, 0, "no steps"));
  assert.ok(zero.includes("0%"));
});

test("rule: renders a labelled divider containing the label", async () => {
  const out = await captureStdout(() => log.rule("Section"));
  assert.ok(out.includes("Section"));
  assert.match(out, /─+/);
});

test("withTiming: emits start and elapsed completion lines", async () => {
  let now = 1_000;
  const out = await captureStdout(async () => {
    const result = await log.withTiming(
      "ACR build agentweaver-api:v1.2.3",
      async () => {
        now = 2_550;
        return "done";
      },
      { now: () => now },
    );
    assert.equal(result, "done");
  });
  assert.match(out, /ACR build agentweaver-api:v1\.2\.3\.\.\./);
  assert.match(out, /completed in 1\.6s/);
});
