// exec.test.mjs -- Real (non-mocked) spawn behavior for lib/exec.mjs. Every
// other test file injects a fake `exec` object, so a genuine spawn-level
// failure (ENOENT: binary not found at all) was never exercised anywhere in
// the suite. This gap let a real bug ship: `capture(cmd, args, {
// allowFailure: true })` was documented/used (e.g. dev.mjs's requireCmd())
// to probe whether a binary exists, but the spawn `error` event handler
// unconditionally rejected regardless of `allowFailure`, so a
// genuinely-missing binary (e.g. `dotnet` not on PATH) crashed with a raw
// ExecError instead of returning a non-zero code for the caller to handle
// gracefully. Reproduced live via `npm run azure:deploy -- --local` (now
// `npm run setup` / `dev --setup`) on a machine without .NET installed.

import test from "node:test";
import assert from "node:assert/strict";
import { capture, run } from "../lib/exec.mjs";

const DEFINITELY_MISSING_BINARY = "agentweaver-definitely-not-a-real-binary-xyz";

test("capture: allowFailure:true resolves (does not reject) when the binary does not exist on PATH", async () => {
  const result = await capture(DEFINITELY_MISSING_BINARY, ["--version"], { allowFailure: true });
  assert.notEqual(result.code, 0);
  assert.equal(result.stdout, "");
});

test("capture: allowFailure unset (default) still rejects when the binary does not exist on PATH", async () => {
  await assert.rejects(capture(DEFINITELY_MISSING_BINARY, ["--version"]), /Failed to spawn/);
});

test("capture: a real, existing binary (node) succeeds and returns stdout", async () => {
  const result = await capture(process.execPath, ["--version"]);
  assert.equal(result.code, 0);
  assert.match(result.stdout, /^v\d+\.\d+\.\d+/);
});

test("run: rejects with ExecError when the binary does not exist on PATH (no allowFailure option)", async () => {
  await assert.rejects(run(DEFINITELY_MISSING_BINARY, ["--version"]), /Failed to spawn/);
});
