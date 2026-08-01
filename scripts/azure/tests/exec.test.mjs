// exec.test.mjs -- Real (non-mocked) spawn behavior for lib/exec.mjs. Every
// other test file injects a fake `exec` object, so a genuine spawn-level
// failure (ENOENT: binary not found at all) was never exercised anywhere in
// the suite. This gap let a real bug ship: `capture(cmd, args, {
// allowFailure: true })` was documented/used (e.g. dev.mjs's requireCmd())
// to probe whether a binary exists, but the spawn `error` event handler
// unconditionally rejected regardless of `allowFailure`, so a
// genuinely-missing binary (e.g. `dotnet` not on PATH) crashed with a raw
// ExecError instead of returning a non-zero code for the caller to handle
// gracefully. Reproduced live via `npm run azure:provision-infra -- --local` (now
// `npm run setup` / `dev --setup`) on a machine without .NET installed.

import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import fs from "node:fs";
import { capture, run, resolveExecutable } from "../lib/exec.mjs";

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

test("run: opt-in timeout terminates the local process and never retries it", async () => {
  await assert.rejects(
    run(process.execPath, ["-e", "setTimeout(() => {}, 5000)"], { timeoutMs: 25 }),
    /timed out after 25ms; remote operation state is unknown and was not retried/,
  );
});

test("capture: timeout remains an indeterminate failure even with allowFailure", async () => {
  await assert.rejects(
    capture(process.execPath, ["-e", "setTimeout(() => {}, 5000)"], { timeoutMs: 25, allowFailure: true }),
    /timed out after 25ms; remote operation state is unknown and was not retried/,
  );
});

test("resolveExecutable('openssl'): falls back to Git for Windows' bundled usr/bin/openssl.exe when not on PATH directly", {
  skip: process.platform !== "win32" ? "Windows-only fallback path" : false,
}, () => {
  // Git for Windows only puts <root>\cmd (or \bin) on PATH, never \usr\bin,
  // so `openssl` alone never resolves via the plain PATH scan on a stock
  // install -- reproduces the real `spawn openssl ENOENT` failure seen when
  // running provision-infra/deploy-from-local on Windows.
  const resolved = resolveExecutable("openssl");
  assert.ok(
    path.isAbsolute(resolved) && fs.existsSync(resolved),
    `expected resolveExecutable('openssl') to find a real binary via the Git-bundled fallback, got: ${resolved}`
  );
  assert.match(resolved, /usr[\\/]bin[\\/]openssl\.exe$/i);
});
