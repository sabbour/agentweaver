// cli.test.mjs -- Tests for cli.mjs subcommand routing: help/unknown-command
// paths, routing to each subcommand's run() with injected fake modules, and
// the special-cased `verify` command (which resolves variables and calls
// steps/40-verify.mjs's run(cfg, opts) instead of run({argv, log})).

import test from "node:test";
import assert from "node:assert/strict";
import { HELP_TEXT, run } from "../cli.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec() };
}

test("run: no command prints HELP_TEXT", async () => {
  const messages = [];
  const log = { ...noopLog(), info: (m) => messages.push(m) };
  const result = await run([], { log });
  assert.equal(result.help, true);
  assert.ok(messages.includes(HELP_TEXT));
});

test("run: -h/--help/help all print HELP_TEXT", async () => {
  for (const arg of ["-h", "--help", "help"]) {
    const result = await run([arg], { log: noopLog() });
    assert.equal(result.help, true);
  }
});

test("run: unknown command throws and logs an error", async () => {
  const errors = [];
  const log = { ...noopLog(), error: (m) => errors.push(m) };
  await assert.rejects(run(["bogus"], { log }), /Unknown command: 'bogus'/);
  assert.ok(errors.some((e) => e.includes("bogus")));
});

test("run: routes 'deploy' to deploy.mjs's run() with argv + log", async () => {
  let received;
  const modules = { deploy: { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["deploy", "--local"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["--local"]);
});

test("run: routes 'upgrade' by resolving variables first, then calling run(cfg, opts) -- NOT run({argv, log})", async () => {
  let receivedCfg;
  let receivedOpts;
  const fakeCfg = { NAMESPACE: "agentweaver" };
  const modules = {
    upgrade: {
      run: async (cfg, opts) => {
        receivedCfg = cfg;
        receivedOpts = opts;
        return { ok: true };
      },
    },
    variables: { resolveVariables: async () => fakeCfg },
  };
  const result = await run(["upgrade", "--allow-dirty"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(receivedCfg, fakeCfg);
  assert.ok("log" in receivedOpts);
  assert.equal(receivedOpts.allowDirty, true);
  // Crucially: upgrade's run() is called with (cfg, opts), never {argv, log}.
  assert.equal(receivedCfg.argv, undefined);
});

test("run: 'upgrade' without --allow-dirty passes allowDirty:false", async () => {
  let receivedOpts;
  const modules = {
    upgrade: { run: async (_cfg, opts) => { receivedOpts = opts; return { ok: true }; } },
    variables: { resolveVariables: async () => ({}) },
  };
  await run(["upgrade"], { log: noopLog(), modules });
  assert.equal(receivedOpts.allowDirty, false);
});

test("run: 'upgrade --help' prints help without resolving variables or calling run()", async () => {
  const messages = [];
  const log = { ...noopLog(), info: (m) => messages.push(m) };
  let variablesResolved = false;
  let runCalled = false;
  const modules = {
    upgrade: { run: async () => { runCalled = true; return { ok: true }; }, HELP_TEXT: "UPGRADE HELP" },
    variables: { resolveVariables: async () => { variablesResolved = true; return {}; } },
  };
  const result = await run(["upgrade", "--help"], { log, modules });
  assert.equal(result.help, true);
  assert.equal(runCalled, false);
  assert.equal(variablesResolved, false);
  assert.ok(messages.includes("UPGRADE HELP"));
});

test("run: routes 'release' to release.mjs's run()", async () => {
  let received;
  const modules = { release: { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["release", "patch"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["patch"]);
});

test("run: routes 'dev' to dev.mjs's run()", async () => {
  let received;
  const modules = { dev: { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["dev", "--no-browser"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["--no-browser"]);
});

test("run: routes 'verify' by resolving variables first, then calling run(cfg, opts) -- NOT run({argv, log})", async () => {
  let receivedCfg;
  let receivedOpts;
  const fakeCfg = { NAMESPACE: "agentweaver" };
  const modules = {
    verify: {
      run: async (cfg, opts) => {
        receivedCfg = cfg;
        receivedOpts = opts;
        return { ok: true, pass: 3, fail: 0 };
      },
    },
    variables: { resolveVariables: async () => fakeCfg },
  };
  const result = await run(["verify"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(receivedCfg, fakeCfg);
  assert.ok("log" in receivedOpts);
  // Crucially: verify's run() is called with (cfg, opts), never {argv, log}.
  assert.equal(receivedCfg.argv, undefined);
});

test("run: falls back to dynamic import via importFn when no module override is supplied", async () => {
  const importedSpecifiers = [];
  const fakeModule = { run: async () => ({ ok: true }) };
  const importFn = async (specifier) => {
    importedSpecifiers.push(specifier);
    return fakeModule;
  };
  const result = await run(["deploy"], { log: noopLog(), importFn });
  assert.equal(result.ok, true);
  assert.ok(importedSpecifiers.includes("./deploy.mjs"));
});

test("run: 'verify --help' prints help without resolving variables or calling run()", async () => {
  const messages = [];
  const log = { ...noopLog(), info: (m) => messages.push(m) };
  let variablesResolved = false;
  let runCalled = false;
  const modules = {
    verify: { run: async () => { runCalled = true; return { ok: true }; }, HELP_TEXT: "VERIFY HELP" },
    variables: { resolveVariables: async () => { variablesResolved = true; return {}; } },
  };
  const result = await run(["verify", "--help"], { log, modules });
  assert.equal(result.help, true);
  assert.equal(runCalled, false);
  assert.equal(variablesResolved, false);
  assert.ok(messages.includes("VERIFY HELP"));
});

test("run: 'verify' via importFn dynamically imports both steps/40-verify.mjs and variables.mjs", async () => {
  const importedSpecifiers = [];
  const fakeCfg = { NAMESPACE: "agentweaver" };
  const importFn = async (specifier) => {
    importedSpecifiers.push(specifier);
    if (specifier === "./variables.mjs") return { resolveVariables: async () => fakeCfg };
    return { run: async (cfg) => ({ ok: true, cfgSeen: cfg }) };
  };
  const result = await run(["verify"], { log: noopLog(), importFn });
  assert.equal(result.ok, true);
  assert.deepEqual(result.cfgSeen, fakeCfg);
  assert.ok(importedSpecifiers.includes("./steps/40-verify.mjs"));
  assert.ok(importedSpecifiers.includes("./variables.mjs"));
});
