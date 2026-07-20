// dev.test.mjs -- Smoke tests for dev.mjs: argv parsing, WSL path
// conversion, HTTP readiness polling (fetch/sleep injected), and the main
// run() orchestration with injected exec/spawn/fetch stubs. No real
// process spawning, WSL, or network access.

import test from "node:test";
import assert from "node:assert/strict";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { API_URL, WEB_URL, parseArgs, HELP_TEXT, waitForHttpOk, toWslPath, run, runLocalSetup, installHint, checkPrerequisites } from "../dev.mjs";

const TEST_TMP_ROOT = path.join(process.cwd(), "scripts", "azure", "tests", ".tmp");

async function createRepoRootFixture(testName, { exampleContents, developmentContents } = {}) {
  const fixtureName = `${testName.replaceAll(/[^a-z0-9-]+/gi, "-").toLowerCase()}-${Date.now()}-${Math.random().toString(16).slice(2, 10)}`;
  const repoRoot = path.join(TEST_TMP_ROOT, fixtureName);
  const apiRoot = path.join(repoRoot, "apps", "Agentweaver.Api");
  await mkdir(apiRoot, { recursive: true });

  const defaultExampleContents = `{
  "_comment": "fixture",
  "Auth": {
    "GitHub": {
      "ClientId": "",
      "ClientSecret": "",
      "CallbackUrl": "http://localhost:5000/auth/github/callback",
      "FrontendUrl": "http://localhost:5173"
    }
  },
  "Providers": {
    "GitHubCopilot": {
      "GitHubToken": "",
      "Model": "claude-sonnet-4.6"
    }
  }
}
`;

  await writeFile(path.join(apiRoot, "appsettings.Development.json.example"), exampleContents ?? defaultExampleContents, "utf8");
  if (developmentContents !== undefined) {
    await writeFile(path.join(apiRoot, "appsettings.Development.json"), developmentContents, "utf8");
  }

  return {
    repoRoot,
    apiRoot,
    cleanup: () => rm(repoRoot, { recursive: true, force: true }),
  };
}

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

test("run: --setup delegates to runLocalSetup and never starts the API/Web dev servers", async (t) => {
  const fixture = await createRepoRootFixture("run-setup");
  t.after(fixture.cleanup);
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
  const result = await run({ argv: ["--setup"], exec, log: noopLog(), repoRoot: fixture.repoRoot, spawn });
  assert.equal(result.ok, true);
  assert.equal(spawnCalls.length, 0); // no API/Web processes started
  assert.ok(execCalls.some(([cmd, ...args]) => cmd === "npm" && args.includes("install")));
  assert.ok(execCalls.some(([cmd, ...args]) => cmd === "dotnet" && args[0] === "restore"));
});

test("runLocalSetup: throws a clear error naming every failed prerequisite", async () => {
  const exec = {
    async capture(cmd) {
      if (cmd === "git") return { stdout: "git version 2.40", stderr: "", code: 0 };
      return { stdout: "", stderr: "not found", code: 1 };
    },
    async run() {
      return { code: 0 };
    },
  };
  await assert.rejects(runLocalSetup({ exec, log: noopLog(), repoRoot: os.tmpdir() }), /dotnet.*node|node.*dotnet/);
});

test("runLocalSetup: reports every missing prerequisite, not just the first one", async () => {
  const capturedCalls = [];
  const exec = {
    async capture(cmd, args) {
      capturedCalls.push(cmd);
      // Both dotnet and node are missing; only git succeeds.
      if (cmd === "git") return { stdout: "git version 2.40", stderr: "", code: 0 };
      return { stdout: "", stderr: "not found", code: 1 };
    },
    async run() {
      return { code: 0 };
    },
  };
  const errorLines = [];
  const log = { ...noopLog(), error: (msg) => errorLines.push(msg) };
  await assert.rejects(runLocalSetup({ exec, log, repoRoot: os.tmpdir() }));
  // All three checks should have been attempted (not stopped after the first failure).
  assert.ok(capturedCalls.includes("git"));
  assert.ok(capturedCalls.includes("dotnet"));
  assert.ok(capturedCalls.includes("node"));
  // Both failures should be reported, each with its install hint.
  assert.ok(errorLines.some((l) => /dotnet/.test(l) && /Install with:|dot\.net\/download/.test(l)));
  assert.ok(errorLines.some((l) => /node/.test(l) && /Install with:|nodejs\.org/.test(l)));
});

test("runLocalSetup: scaffolds appsettings.Development.json from the checked-in example when missing", async (t) => {
  const fixture = await createRepoRootFixture("scaffold-if-missing", {
    exampleContents: `{
  "_comment": "fixture",
  "Auth": {
    "GitHub": {
      "ClientId": "",
      "ClientSecret": "",
      "CallbackUrl": "http://localhost:5000/auth/github/callback",
      "FrontendUrl": "http://localhost:5173"
    }
  },
  "Providers": {
    "GitHubCopilot": {
      "GitHubToken": "",
      "Model": "claude-sonnet-4.6"
    }
  }
}
`,
  });
  t.after(fixture.cleanup);

  const infoLines = [];
  const exec = {
    async capture(cmd) {
      if (cmd === "dotnet") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
    async run() {
      return { code: 0 };
    },
  };

  const result = await runLocalSetup({
    exec,
    log: { ...noopLog(), info: (msg) => infoLines.push(msg) },
    repoRoot: fixture.repoRoot,
  });

  const developmentPath = path.join(fixture.apiRoot, "appsettings.Development.json");
  const examplePath = path.join(fixture.apiRoot, "appsettings.Development.json.example");
  const [developmentContents, exampleContents] = await Promise.all([
    readFile(developmentPath, "utf8"),
    readFile(examplePath, "utf8"),
  ]);

  assert.equal(result.ok, true);
  assert.equal(developmentContents, exampleContents);
  assert.ok(
    infoLines.includes(
      "  Scaffolded apps/Agentweaver.Api/appsettings.Development.json from .example; set Auth:GitHub:ClientId in it, then store ClientSecret and Providers:GitHubCopilot:GitHubToken via dotnet user-secrets before first sign-in.",
    ),
  );
});

test("runLocalSetup: leaves an existing appsettings.Development.json untouched", async (t) => {
  const existingContents = `{
  "existing": true
}
`;
  const fixture = await createRepoRootFixture("leave-existing", {
    developmentContents: existingContents,
  });
  t.after(fixture.cleanup);

  const infoLines = [];
  const exec = {
    async capture(cmd) {
      if (cmd === "dotnet") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
    async run() {
      return { code: 0 };
    },
  };

  const result = await runLocalSetup({
    exec,
    log: { ...noopLog(), info: (msg) => infoLines.push(msg) },
    repoRoot: fixture.repoRoot,
  });

  const developmentContents = await readFile(path.join(fixture.apiRoot, "appsettings.Development.json"), "utf8");
  assert.equal(result.ok, true);
  assert.equal(developmentContents, existingContents);
  assert.ok(
    !infoLines.includes(
      "  Scaffolded apps/Agentweaver.Api/appsettings.Development.json from .example; set Auth:GitHub:ClientId in it, then store ClientSecret and Providers:GitHubCopilot:GitHubToken via dotnet user-secrets before first sign-in.",
    ),
  );
});

test("runLocalSetup: does not overwrite an existing appsettings.Development.json that differs from the example (COPYFILE_EXCL race-safety)", async (t) => {
  // Guards the check-then-copy (TOCTOU) race: even though the destination
  // content differs from the example (so a naive unconditional copy WOULD
  // clobber it), scaffolding must use COPYFILE_EXCL and leave the existing
  // file byte-for-byte untouched, returning no "Scaffolded" message.
  const existingContents = `{
  "Auth": { "GitHub": { "ClientId": "already-configured-by-the-user" } }
}
`;
  const fixture = await createRepoRootFixture("race-safe-no-overwrite", {
    developmentContents: existingContents,
  });
  t.after(fixture.cleanup);

  const infoLines = [];
  const exec = {
    async capture(cmd) {
      if (cmd === "dotnet") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
    async run() {
      return { code: 0 };
    },
  };

  const result = await runLocalSetup({
    exec,
    log: { ...noopLog(), info: (msg) => infoLines.push(msg) },
    repoRoot: fixture.repoRoot,
  });

  const developmentPath = path.join(fixture.apiRoot, "appsettings.Development.json");
  const examplePath = path.join(fixture.apiRoot, "appsettings.Development.json.example");
  const [developmentContents, exampleContents] = await Promise.all([
    readFile(developmentPath, "utf8"),
    readFile(examplePath, "utf8"),
  ]);

  assert.equal(result.ok, true);
  assert.equal(developmentContents, existingContents);
  assert.notEqual(developmentContents, exampleContents);
  assert.ok(
    !infoLines.includes(
      "  Scaffolded apps/Agentweaver.Api/appsettings.Development.json from .example; set Auth:GitHub:ClientId in it, then store ClientSecret and Providers:GitHubCopilot:GitHubToken via dotnet user-secrets before first sign-in.",
    ),
  );
});

test("runLocalSetup: prints GitHub OAuth guidance in the local dev ready summary", async (t) => {
  const fixture = await createRepoRootFixture("ready-summary");
  t.after(fixture.cleanup);
  const infoLines = [];
  const sectionLines = [];
  const log = {
    ...noopLog(),
    info: (msg) => infoLines.push(msg),
    section: (msg) => sectionLines.push(msg),
  };
  const exec = {
    async capture(cmd) {
      if (cmd === "dotnet") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
    async run() {
      return { code: 0 };
    },
  };

  const result = await runLocalSetup({ exec, log, repoRoot: fixture.repoRoot });

  assert.equal(result.ok, true);
  assert.ok(sectionLines.includes("LOCAL DEV READY"));
  assert.ok(
    infoLines.includes(
      "  Scaffolded apps/Agentweaver.Api/appsettings.Development.json from .example; set Auth:GitHub:ClientId in it, then store ClientSecret and Providers:GitHubCopilot:GitHubToken via dotnet user-secrets before first sign-in.",
    ),
  );
  assert.ok(infoLines.includes("  For local sign-in: create a GitHub OAuth App: https://github.com/settings/developers"));
  assert.ok(infoLines.includes("  Callback URL:      http://localhost:5000/auth/github/callback"));
  assert.ok(
    infoLines.includes("  Set Auth:GitHub:ClientId (non-secret) in apps/Agentweaver.Api/appsettings.Development.json."),
  );
  assert.ok(
    infoLines.includes("  Store secrets via user-secrets (run in apps/Agentweaver.Api), never in the JSON file:"),
  );
  assert.ok(infoLines.includes('    dotnet user-secrets set Auth:GitHub:ClientSecret "<client-secret>"'));
  assert.ok(infoLines.includes('    dotnet user-secrets set Providers:GitHubCopilot:GitHubToken "<github-pat-with-copilot-access>"'));
  assert.ok(infoLines.includes("  Full walkthrough:  docs/guide/getting-started.md#1-configure-the-api"));
});

test("checkPrerequisites: runs all checks concurrently and reports ok:false with per-tool results", async () => {
  const exec = {
    async capture(cmd) {
      if (cmd === "git") return { stdout: "git version 2.40", stderr: "", code: 0 };
      if (cmd === "dotnet") return { stdout: "9.0.100", stderr: "", code: 0 }; // too old
      return { stdout: "", stderr: "not found", code: 1 }; // node missing
    },
  };
  const { ok, results } = await checkPrerequisites({ exec });
  assert.equal(ok, false);
  const byName = Object.fromEntries(results.map((r) => [r.name, r]));
  assert.equal(byName.git.ok, true);
  assert.equal(byName.dotnet.ok, false);
  assert.match(byName.dotnet.message, /\.NET 10 SDK is required/);
  assert.equal(byName.node.ok, false);
  assert.match(byName.node.message, /not found/);
});

test("checkPrerequisites: reports ok:true when every tool is present and new enough", async () => {
  const exec = {
    async capture(cmd) {
      if (cmd === "dotnet") return { stdout: "10.0.100", stderr: "", code: 0 };
      if (cmd === "node") return { stdout: "v22.12.0", stderr: "", code: 0 };
      return { stdout: "git version 2.40", stderr: "", code: 0 };
    },
  };
  const { ok } = await checkPrerequisites({ exec });
  assert.equal(ok, true);
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
