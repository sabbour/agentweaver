// dev.test.mjs -- Smoke tests for dev.mjs: argv parsing, WSL path
// conversion, HTTP readiness polling (fetch/sleep injected), and the main
// run() orchestration with injected exec/spawn/fetch stubs. No real
// process spawning, WSL, or network access.

import test from "node:test";
import assert from "node:assert/strict";
import os from "node:os";
import { API_URL, WEB_URL, parseArgs, HELP_TEXT, waitForHttpOk, toWslPath, run, runLocalSetup, installHint } from "../dev.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec() };
}

test("API_URL/WEB_URL: expected local dev ports", () => {
  assert.equal(API_URL, "http://localhost:5000");
  assert.equal(WEB_URL, "http://localhost:5173");
});

test("parseArgs: --skip-build and --no-browser", () => {
  assert.deepEqual(parseArgs(["--skip-build", "--no-browser"]), { skipBuild: true, noBrowser: true, setup: false, help: false });
});

test("parseArgs: recognizes --setup", () => {
  assert.deepEqual(parseArgs(["--setup"]), { skipBuild: false, noBrowser: false, setup: true, help: false });
});

test("parseArgs: throws on unknown argument", () => {
  assert.throws(() => parseArgs(["--bogus"]), /Unknown argument/);
});

test("HELP_TEXT: mentions both URLs and --setup", () => {
  assert.match(HELP_TEXT, /localhost:5000/);
  assert.match(HELP_TEXT, /localhost:5173/);
  assert.match(HELP_TEXT, /--setup/);
});

test("toWslPath: converts a Windows drive path to a WSL mount path", () => {
  assert.equal(toWslPath("C:\\Users\\me\\repo"), "/mnt/c/Users/me/repo");
});

test("toWslPath: lowercases the drive letter and preserves case elsewhere", () => {
  assert.equal(toWslPath("D:\\Repo\\Sub"), "/mnt/d/Repo/Sub");
});

test("toWslPath: throws for a non-drive-letter path", () => {
  assert.throws(() => toWslPath("/already/posix"), /Cannot convert path/);
});

test("waitForHttpOk: returns true as soon as fetch resolves 200", async () => {
  let calls = 0;
  const fetchImpl = async () => {
    calls++;
    return { status: 200 };
  };
  const sleep = async () => {};
  const ok = await waitForHttpOk("http://localhost:1234/health", { fetchImpl, sleep, log: noopLog(), timeoutMs: 5000 });
  assert.equal(ok, true);
  assert.equal(calls, 1);
});

test("waitForHttpOk: returns false after the timeout elapses without a 200", async () => {
  const fetchImpl = async () => ({ status: 503 });
  const sleep = async () => {};
  const ok = await waitForHttpOk("http://localhost:1234/health", { fetchImpl, sleep, log: noopLog(), timeoutMs: 1, intervalMs: 1 });
  assert.equal(ok, false);
});

test("waitForHttpOk: tolerates fetch throwing (treats as not-ready)", async () => {
  let attempts = 0;
  const fetchImpl = async () => {
    attempts++;
    if (attempts < 2) throw new Error("ECONNREFUSED");
    return { status: 200 };
  };
  const ok = await waitForHttpOk("http://localhost:1234/health", { fetchImpl, sleep: async () => {}, log: noopLog(), timeoutMs: 5000 });
  assert.equal(ok, true);
  assert.equal(attempts, 2);
});

test("run: --help returns without starting any processes", async () => {
  const spawnCalls = [];
  const spawn = (...args) => {
    spawnCalls.push(args);
    return { pid: 1 };
  };
  const result = await run({ argv: ["--help"], log: noopLog(), spawn });
  assert.equal(result.help, true);
  assert.equal(spawnCalls.length, 0);
});

test("run: orchestrates stop-stale, build, start api+web, waits for readiness, and opens the browser", async () => {
  const execCalls = [];
  const exec = {
    async run(cmd, args, opts) {
      execCalls.push({ cmd, args, opts });
      return { code: 0 };
    },
  };
  const spawnCalls = [];
  const spawn = (cmd, args, opts) => {
    spawnCalls.push({ cmd, args, opts });
    return { pid: 123, cmd };
  };
  const fetchImpl = async () => ({ status: 200 });
  const sleep = async () => {};

  const result = await run({
    argv: [],
    repoRoot: "C:\\repo",
    exec,
    log: noopLog(),
    fetchImpl,
    spawn,
    sleep,
  });

  assert.equal(result.ok, true);
  assert.equal(result.apiReady, true);
  assert.equal(result.webReady, true);
  assert.ok(spawnCalls.length >= 2); // API + Web processes started
  // openBrowser should have been invoked via exec.run (cmd/open/xdg-open) since webReady && !noBrowser
  assert.ok(execCalls.length > 0);
});

test("run: --no-browser skips opening the browser even when both are ready", async () => {
  const execCalls = [];
  const exec = {
    async run(cmd, args, opts) {
      execCalls.push({ cmd, args, opts });
      return { code: 0 };
    },
  };
  const spawn = () => ({ pid: 1 });
  const fetchImpl = async () => ({ status: 200 });
  const sleep = async () => {};

  await run({ argv: ["--no-browser", "--skip-build"], repoRoot: "C:\\repo", exec, log: noopLog(), fetchImpl, spawn, sleep });

  const openBrowserCommands = execCalls.filter(
    (c) => (c.cmd === "cmd" && c.args.includes("start")) || c.cmd === "open" || c.cmd === "xdg-open",
  );
  assert.equal(openBrowserCommands.length, 0);
});

test("run: --skip-build does not invoke a build command", async () => {
  const execCalls = [];
  const exec = {
    async run(cmd, args, opts) {
      execCalls.push({ cmd, args, opts });
      return { code: 0 };
    },
  };
  const spawn = () => ({ pid: 1 });
  const fetchImpl = async () => ({ status: 200 });
  const sleep = async () => {};

  await run({ argv: ["--skip-build"], repoRoot: "C:\\repo", exec, log: noopLog(), fetchImpl, spawn, sleep });

  const buildCalls = execCalls.filter((c) => c.args?.includes("build"));
  assert.equal(buildCalls.length, 0);
});

test("run: --setup delegates to runLocalSetup and never starts the API/Web dev servers", async () => {
  const execCalls = [];
  const exec = {
    async capture(cmd, args) {
      execCalls.push([cmd, ...args]);
      if (cmd === "dotnet" && args[0] === "--version") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node" && args[0] === "--version") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
    async run(cmd, args) {
      execCalls.push([cmd, ...args]);
      return { code: 0 };
    },
  };
  const spawnCalls = [];
  const spawn = (...args) => {
    spawnCalls.push(args);
    return { pid: 1 };
  };
  const result = await run({ argv: ["--setup"], exec, log: noopLog(), repoRoot: os.tmpdir(), spawn });
  assert.equal(result.ok, true);
  assert.equal(spawnCalls.length, 0); // no API/Web processes started
  assert.ok(execCalls.some(([cmd, ...args]) => cmd === "npm" && args.includes("install")));
  assert.ok(execCalls.some(([cmd, ...args]) => cmd === "dotnet" && args[0] === "restore"));
});

test("runLocalSetup: throws a clear error when a prerequisite is missing", async () => {
  const exec = {
    async capture(cmd) {
      if (cmd === "git") return { stdout: "git version 2.40", stderr: "", code: 0 };
      return { stdout: "", stderr: "not found", code: 1 };
    },
    async run() {
      return { code: 0 };
    },
  };
  await assert.rejects(runLocalSetup({ exec, log: noopLog(), repoRoot: os.tmpdir() }), /dotnet/);
});

test("runLocalSetup: missing-prerequisite error includes a platform-specific install command", async () => {
  const exec = {
    async capture(cmd) {
      if (cmd === "git") return { stdout: "git version 2.40", stderr: "", code: 0 };
      return { stdout: "", stderr: "not found", code: 1 };
    },
    async run() {
      return { code: 0 };
    },
  };
  await assert.rejects(runLocalSetup({ exec, log: noopLog(), repoRoot: os.tmpdir() }), /Install with:|dot\.net\/download/);
});

test("installHint: returns winget/brew/apt commands for each known tool on each platform", () => {
  for (const tool of ["git", "dotnet", "node"]) {
    assert.match(installHint(tool, "win32"), /winget install/);
    assert.match(installHint(tool, "darwin"), /brew install/);
    assert.match(installHint(tool, "linux"), /apt-get install|dotnet-install\.sh/);
  }
});

test("installHint: falls back to doc links on an unrecognized platform", () => {
  assert.match(installHint("git", "freebsd"), /git-scm\.org|git-scm\.com/);
});
