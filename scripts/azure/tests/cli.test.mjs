// cli.test.mjs -- Tests for cli.mjs subcommand routing: help/unknown-command
// paths, routing to each subcommand's run() with injected fake modules, and
// the special-cased `verify` command (which resolves variables and calls
// steps/40-verify.mjs's run(cfg, opts) instead of run({argv, log})).

import test from "node:test";
import assert from "node:assert/strict";
import { HELP_TEXT, main, run } from "../cli.mjs";

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

test("run: routes 'provision-infra' with argv + log", async () => {
  let received;
  const modules = { "provision-infra": { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["provision-infra", "--skip-postgres"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["--skip-postgres"]);
});

test("run: routes 'setup-entra-app' with argv + log", async () => {
  let received;
  const modules = { "setup-entra-app": { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["setup-entra-app", "--app-name", "agentweaver-prod-authn"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["--app-name", "agentweaver-prod-authn"]);
});

test("run: routes 'deploy-from-local' by resolving variables first", async () => {
  let receivedCfg;
  let receivedOpts;
  const fakeCfg = { NAMESPACE: "agentweaver" };
  const modules = {
    "deploy-from-local": {
      run: async (cfg, opts) => {
        receivedCfg = cfg;
        receivedOpts = opts;
        return { ok: true };
      },
    },
    variables: { resolveVariables: async () => fakeCfg },
    config: { loadParamsFile: () => ({}) },
  };
  const result = await run(["deploy-from-local", "--allow-dirty"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(receivedCfg, fakeCfg);
  assert.ok("log" in receivedOpts);
  assert.equal(receivedOpts.allowDirty, true);
  // The local deployment run() is called with (cfg, opts), never {argv, log}.
  assert.equal(receivedCfg.argv, undefined);
});

test("run: 'deploy-from-local' without --allow-dirty passes allowDirty:false", async () => {
  let receivedOpts;
  const modules = {
    "deploy-from-local": { run: async (_cfg, opts) => { receivedOpts = opts; return { ok: true }; } },
    variables: { resolveVariables: async () => ({}) },
  };
  await run(["deploy-from-local"], { log: noopLog(), modules });
  assert.equal(receivedOpts.allowDirty, false);
});

test("run: 'deploy-from-local --help' prints help without resolving variables or calling run()", async () => {
  const messages = [];
  const log = { ...noopLog(), info: (m) => messages.push(m) };
  let variablesResolved = false;
  let runCalled = false;
  const modules = {
    "deploy-from-local": { run: async () => { runCalled = true; return { ok: true }; }, HELP_TEXT: "LOCAL DEPLOY HELP" },
    variables: { resolveVariables: async () => { variablesResolved = true; return {}; } },
  };
  const result = await run(["deploy-from-local", "--help"], { log, modules });
  assert.equal(result.help, true);
  assert.equal(runCalled, false);
  assert.equal(variablesResolved, false);
  assert.ok(messages.includes("LOCAL DEPLOY HELP"));
});

test("run: routes 'release' to release.mjs's run()", async () => {
  let received;
  const modules = { release: { run: async (opts) => { received = opts; return { ok: true }; } } };
  const result = await run(["release", "patch"], { log: noopLog(), modules });
  assert.equal(result.ok, true);
  assert.deepEqual(received.argv, ["patch"]);
});

test("run: routes commit, publication, and release deployment commands with argv", async () => {
  const received = [];
  const modules = {
    "deploy-from-commit": { run: async (opts) => { received.push(["commit", opts.argv]); return { ok: true }; } },
    "publish-release": { run: async (opts) => { received.push(["publish", opts.argv]); return { ok: true }; } },
    "deploy-from-release": { run: async (opts) => { received.push(["deploy", opts.argv]); return { ok: true }; } },
    config: { loadParamsFile: () => ({}) },
  };
  await run(["deploy-from-commit", "origin/feature"], { log: noopLog(), modules });
  await run(["publish-release", "--dry-run"], { log: noopLog(), modules });
  await run(["deploy-from-release", "v1.2.3"], { log: noopLog(), modules });
  assert.deepEqual(received, [
    ["commit", ["origin/feature"]],
    ["publish", ["--dry-run"]],
    ["deploy", ["v1.2.3"]],
  ]);
});

test("run: 'deploy-from-commit' and 'deploy-from-release' auto-load the params file into opts.env", async () => {
  let receivedCommitOpts;
  let receivedReleaseOpts;
  const modules = {
    "deploy-from-commit": { run: async (opts) => { receivedCommitOpts = opts; return { ok: true }; } },
    "deploy-from-release": { run: async (opts) => { receivedReleaseOpts = opts; return { ok: true }; } },
    config: { loadParamsFile: () => ({ KEYVAULT_NAME: "kv-from-params-file" }) },
  };
  await run(["deploy-from-commit", "origin/dev"], { log: noopLog(), modules });
  await run(["deploy-from-release", "v1.2.3"], { log: noopLog(), modules });
  assert.equal(receivedCommitOpts.env.KEYVAULT_NAME, "kv-from-params-file");
  assert.equal(receivedReleaseOpts.env.KEYVAULT_NAME, "kv-from-params-file");
});

test("run: 'deploy-from-commit --help' and 'deploy-from-release --help' print help without loading params or calling run()", async () => {
  for (const command of ["deploy-from-commit", "deploy-from-release"]) {
    const messages = [];
    const log = { ...noopLog(), info: (m) => messages.push(m) };
    let runCalled = false;
    let paramsLoaded = false;
    const modules = {
      [command]: { run: async () => { runCalled = true; return { ok: true }; }, HELP_TEXT: `${command} HELP` },
      config: { loadParamsFile: () => { paramsLoaded = true; return {}; } },
    };
    const result = await run([command, "--help"], { log, modules });
    assert.equal(result.help, true);
    assert.equal(runCalled, false);
    assert.equal(paramsLoaded, false);
    assert.ok(messages.includes(`${command} HELP`));
  }
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
  const result = await run(["provision-infra"], { log: noopLog(), importFn });
  assert.equal(result.ok, true);
  assert.ok(importedSpecifiers.includes("./provision-infra.mjs"));
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
    if (specifier === "./lib/config.mjs") return { loadParamsFile: () => ({}) };
    return { run: async (cfg) => ({ ok: true, cfgSeen: cfg }) };
  };
  const result = await run(["verify"], { log: noopLog(), importFn });
  assert.equal(result.ok, true);
  assert.deepEqual(result.cfgSeen, fakeCfg);
  assert.ok(importedSpecifiers.includes("./steps/40-verify.mjs"));
  assert.ok(importedSpecifiers.includes("./variables.mjs"));
});

test("run: standalone verify resolves an explicit params file before certificate validation", async () => {
  let paramsPath;
  let resolvedEnv;
  const modules = {
    verify: { run: async (cfg) => ({ ok: true, cfg }) },
    config: {
      loadParamsFile: (value) => {
        paramsPath = value;
        return {
          KEYVAULT_NAME: "custom-kv",
          OAUTH_SIGNING_CERTIFICATE_NAME: "custom-signing",
          OAUTH_ENCRYPTION_CERTIFICATE_NAME: "custom-encryption",
        };
      },
    },
    variables: {
      resolveVariables: async ({ env }) => {
        resolvedEnv = env;
        return env;
      },
    },
  };
  await run(["verify", "--params-file", "scripts/azure/custom.json"], { log: noopLog(), modules });
  assert.equal(paramsPath, "scripts/azure/custom.json");
  assert.equal(resolvedEnv.OAUTH_SIGNING_CERTIFICATE_NAME, "custom-signing");
  assert.equal(resolvedEnv.OAUTH_ENCRYPTION_CERTIFICATE_NAME, "custom-encryption");
});

test("run: standalone verify auto-discovers params through the deploy command path", async () => {
  let paramsPath;
  let resolvedEnv;
  const modules = {
    verify: { run: async () => ({ ok: true }) },
    config: {
      loadParamsFile: (value) => {
        paramsPath = value;
        return { OAUTH_SIGNING_CERTIFICATE_NAME: "auto-signing" };
      },
    },
    variables: {
      resolveVariables: async ({ env }) => {
        resolvedEnv = env;
        return env;
      },
    },
  };
  await run(["verify"], {
    log: noopLog(),
    modules,
    findParamsFile: () => "scripts/azure/params.test-user.json",
  });
  assert.equal(paramsPath, "scripts/azure/params.test-user.json");
  assert.equal(resolvedEnv.OAUTH_SIGNING_CERTIFICATE_NAME, "auto-signing");
});

test("main: standalone verify exits non-zero when health verification returns ok:false", async () => {
  const processImpl = { exitCode: 0, env: {} };
  const result = await main(["verify"], {
    processImpl,
    log: noopLog(),
    modules: {
      verify: { run: async () => ({ ok: false, pass: 4, fail: 1 }) },
      variables: { resolveVariables: async () => ({ NAMESPACE: "agentweaver" }) },
    },
  });

  assert.equal(result.ok, false);
  assert.equal(processImpl.exitCode, 1);
});

test("main: provision-infra exits non-zero when its final verification returns ok:false", async () => {
  const processImpl = { exitCode: 0, env: {} };
  const result = await main(["provision-infra"], {
    processImpl,
    log: noopLog(),
    modules: {
      "provision-infra": { run: async () => ({ ok: false, verify: { pass: 8, fail: 2 } }) },
    },
  });

  assert.equal(result.ok, false);
  assert.equal(processImpl.exitCode, 1);
});
