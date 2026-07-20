// deploy.test.mjs -- Tests for deploy.mjs: argv parsing, help output, the
// non-interactive config-resolution path (flags/env/params-file precedence
// and TTY-fallback error behavior), params-file loading, guided-installer
// flow with fully stubbed prompt/az, and pipeline delegation call order.
// All az/exec/prompt/step calls are injected fakes -- no real Azure CLI,
// kubectl, npm, dotnet, or network access, and no live prompting.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  parseArgs,
  HELP_TEXT,
  runInteractiveInstaller,
  shouldRunInteractiveInstaller,
  run,
} from "../deploy.mjs";
import { NonInteractiveError } from "../lib/prompt.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec() };
}

function fakeStep(name, calls, result = {}) {
  return {
    async run(cfg, opts) {
      calls.push({ step: name, cfg, opts });
      return result;
    },
  };
}

test("parseArgs: recognizes flags and takes values for valued flags", () => {
  const parsed = parseArgs([
    "--skip-postgres",
    "--skip-oauth-key",
    "--image-tag",
    "v1.2.3",
    "--resource-group=my-rg",
    "--github-client-secret",
    "shh",
  ]);
  assert.equal(parsed.flags.SKIP_POSTGRES, true);
  assert.equal(parsed.flags.SKIP_OAUTH_KEY, true);
  assert.equal(parsed.flags.IMAGE_TAG, "v1.2.3");
  assert.equal(parsed.flags.RESOURCE_GROUP, "my-rg");
  assert.equal(parsed.flags.GITHUB_CLIENT_SECRET, "shh");
});

test("parseArgs: --params-file / --config both set paramsFile", () => {
  assert.equal(parseArgs(["--params-file", "a.json"]).paramsFile, "a.json");
  assert.equal(parseArgs(["--config=b.json"]).paramsFile, "b.json");
});

test("parseArgs: throws on unknown argument", () => {
  assert.throws(() => parseArgs(["--bogus"]), /Unknown argument/);
});

test("parseArgs: -h/--help sets help", () => {
  assert.equal(parseArgs(["--help"]).help, true);
  assert.equal(parseArgs(["-h"]).help, true);
});

test("HELP_TEXT: mentions key flags", () => {
  assert.match(HELP_TEXT, /--skip-postgres/);
  assert.match(HELP_TEXT, /--params-file/);
  assert.match(HELP_TEXT, /dev --setup/);
});

test("shouldRunInteractiveInstaller: true only with zero argv and a TTY", () => {
  assert.equal(shouldRunInteractiveInstaller([], { prompt: { isInteractive: () => true } }), true);
  assert.equal(shouldRunInteractiveInstaller(["--skip-postgres"], { prompt: { isInteractive: () => true } }), false);
  assert.equal(shouldRunInteractiveInstaller([], { prompt: { isInteractive: () => false } }), false);
});

test("run: --help prints HELP_TEXT and returns without doing work", async () => {
  const log = noopLog();
  const messages = [];
  log.info = (m) => messages.push(m);
  const result = await run({ argv: ["--help"], log });
  assert.equal(result.help, true);
  assert.ok(messages.some((m) => m.includes("Agentweaver Azure installer")));
});

test("run: non-interactive path throws a clear error when GITHUB_CLIENT_ID/SECRET are missing and no TTY", async () => {
  const prompt = {
    isInteractive: () => false,
    text: async () => {
      throw new NonInteractiveError("cannot prompt: no TTY");
    },
    secret: async () => {
      throw new NonInteractiveError("cannot prompt: no TTY");
    },
  };
  await assert.rejects(
    run({ argv: ["--resource-group", "my-rg"], env: {}, prompt, log: noopLog() }),
    /Missing required config 'GITHUB_CLIENT_ID'/,
  );
});

test("run: non-interactive path resolves config from flags and env, then delegates through the full pipeline in order", async () => {
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls, { IMAGE_TAG: "abc123" }),
    verifyProvenance: fakeStep("verifyProvenance", calls, { ok: true }),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, { HOST: "agentweaver.example.com", GATEWAY_IP: "1.2.3.4" }),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 10, fail: 0 }),
  };
  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture() {
      return { stdout: "", stderr: "", code: 0 };
    },
  };
  const resolveVariablesFn = async ({ env: e }) => ({
    RESOURCE_GROUP: e.RESOURCE_GROUP,
    CLUSTER_NAME: e.CLUSTER_NAME,
    ACR_NAME: e.ACR_NAME,
    ACR_LOGIN_SERVER: `${e.ACR_NAME}.azurecr.io`,
    LOCATION: e.LOCATION,
    KEYVAULT_NAME: e.KEYVAULT_NAME,
    NAMESPACE: e.NAMESPACE,
    IMAGE_TAG: e.IMAGE_TAG ?? "dev",
    AGENTHOST_IMAGE_TAG: "dev",
  });

  const result = await run({
    argv: ["--resource-group", "my-rg", "--github-client-id", "id-123", "--github-client-secret", "topsecret"],
    env: { GITHUB_CLIENT_ID: "", GITHUB_CLIENT_SECRET: "" },
    prompt: { isInteractive: () => false },
    exec,
    log: noopLog(),
    resolveVariables: resolveVariablesFn,
    steps,
  });

  assert.equal(result.ok, true);
  assert.deepEqual(
    calls.map((c) => c.step),
    ["createCluster", "setupIdentity", "provisionMonitoring", "oauthSigningKey", "provisionPostgres", "buildImages", "verifyProvenance", "genA2aMtlsCerts", "deployStep", "verifyStep"],
  );
  assert.equal(calls[0].cfg.RESOURCE_GROUP, "my-rg");
  assert.equal(calls[0].cfg.GITHUB_CLIENT_SECRET, "topsecret");
});

test("run: outputs summary includes the GitHub OAuth callback URL derived from the Gateway host", async () => {
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls, { IMAGE_TAG: "abc123" }),
    verifyProvenance: fakeStep("verifyProvenance", calls, { ok: true }),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, { HOST: "agentweaver.example.com", GATEWAY_IP: "1.2.3.4" }),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 10, fail: 0 }),
  };
  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture() {
      return { stdout: "", stderr: "", code: 0 };
    },
  };
  const resolveVariablesFn = async ({ env: e }) => ({
    RESOURCE_GROUP: e.RESOURCE_GROUP,
    IMAGE_TAG: e.IMAGE_TAG ?? "dev",
    AGENTHOST_IMAGE_TAG: "dev",
  });
  const fields = [];
  const log = { ...noopLog(), field: (label, value) => fields.push([label, value]) };

  await run({
    argv: ["--resource-group", "my-rg", "--github-client-id", "id-123", "--github-client-secret", "topsecret"],
    env: { GITHUB_CLIENT_ID: "", GITHUB_CLIENT_SECRET: "" },
    prompt: { isInteractive: () => false },
    exec,
    log,
    resolveVariables: resolveVariablesFn,
    steps,
  });

  const callbackField = fields.find(([label]) => label === "GitHub OAuth callback URL");
  assert.ok(callbackField, "expected a 'GitHub OAuth callback URL' field in the outputs summary");
  assert.equal(callbackField[1], "https://agentweaver.example.com/auth/github/callback");
});

test("run: --skip-postgres and --skip-oauth-key omit those steps from the call sequence", async () => {
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls),
    verifyProvenance: fakeStep("verifyProvenance", calls),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, {}),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 1, fail: 0 }),
  };
  const exec = { async run() { return { code: 0 }; }, async capture() { return { stdout: "", stderr: "", code: 0 }; } };
  const resolveVariablesFn = async () => ({ RESOURCE_GROUP: "rg", IMAGE_TAG: "dev", AGENTHOST_IMAGE_TAG: "dev" });

  await run({
    argv: ["--skip-postgres", "--skip-oauth-key", "--github-client-id", "id", "--github-client-secret", "sec"],
    env: {},
    prompt: { isInteractive: () => false },
    exec,
    log: noopLog(),
    resolveVariables: resolveVariablesFn,
    steps,
  });

  const stepNames = calls.map((c) => c.step);
  assert.ok(!stepNames.includes("oauthSigningKey"));
  assert.ok(!stepNames.includes("provisionPostgres"));
});

// Local dev setup (--local / runLocalSetup) moved to dev.mjs's `--setup` flag
// -- see tests/dev.test.mjs. deploy.mjs is Azure-only now.

test("run: loads GITHUB_CLIENT_ID/SECRET from a params-file (JSONC) when no flags/env are set", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "deploy-test-"));
  const paramsPath = path.join(dir, "params.jsonc");
  fs.writeFileSync(
    paramsPath,
    `{
      // GitHub OAuth app credentials
      "GITHUB_CLIENT_ID": "from-params-file",
      "GITHUB_CLIENT_SECRET": "from-params-secret",
    }`,
  );
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls),
    verifyProvenance: fakeStep("verifyProvenance", calls),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, {}),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 1, fail: 0 }),
  };
  const exec = { async run() { return { code: 0 }; }, async capture() { return { stdout: "", stderr: "", code: 0 }; } };
  const resolveVariablesFn = async () => ({ IMAGE_TAG: "dev", AGENTHOST_IMAGE_TAG: "dev" });

  await run({
    argv: ["--params-file", paramsPath],
    env: {},
    prompt: { isInteractive: () => false },
    exec,
    log: noopLog(),
    resolveVariables: resolveVariablesFn,
    steps,
  });

  assert.equal(calls[0].cfg.GITHUB_CLIENT_ID, "from-params-file");
  assert.equal(calls[0].cfg.GITHUB_CLIENT_SECRET, "from-params-secret");
  fs.rmSync(dir, { recursive: true, force: true });
});

test("runInteractiveInstaller: collects subscription/RG/location/names/OAuth via stubbed prompt+az, never real prompting", async () => {
  const az = {
    listSubscriptions: async () => [
      { id: "sub-1", name: "Sub One" },
      { id: "sub-2", name: "Sub Two" },
    ],
    showAccount: async () => ({ id: "sub-2" }),
    setActiveSubscription: async () => {},
    listResourceGroups: async () => [{ name: "existing-rg" }],
    listLocations: async () => [{ name: "westus2", displayName: "West US 2" }],
  };
  const selectCalls = [];
  const prompt = {
    select: async (question, choices) => {
      selectCalls.push(question);
      return choices[0].value;
    },
    text: async (question, opts = {}) => opts.default ?? `answer-to-${question}`,
    secret: async () => "super-secret-value",
  };
  const collected = await runInteractiveInstaller({ prompt, az, log: noopLog() });
  assert.equal(collected.RESOURCE_GROUP, "existing-rg");
  assert.equal(collected.LOCATION, "westus2");
  assert.equal(collected.GITHUB_CLIENT_SECRET, "super-secret-value");
  assert.ok(selectCalls.length >= 3);
});

test("run: NEVER logs the GitHub OAuth client secret anywhere in output", async () => {
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls),
    verifyProvenance: fakeStep("verifyProvenance", calls),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, {}),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 1, fail: 0 }),
  };
  const exec = { async run() { return { code: 0 }; }, async capture() { return { stdout: "", stderr: "", code: 0 }; } };
  const resolveVariablesFn = async () => ({ IMAGE_TAG: "dev", AGENTHOST_IMAGE_TAG: "dev" });
  const SECRET_VALUE = "sekrit-value-never-printed";
  const loggedLines = [];
  const log = noopLog();
  for (const key of Object.keys(log)) {
    const orig = log[key];
    log[key] = (...args) => {
      loggedLines.push(args.map(String).join(" "));
      return orig(...args);
    };
  }

  await run({
    argv: ["--github-client-id", "id", "--github-client-secret", SECRET_VALUE],
    env: {},
    prompt: { isInteractive: () => false },
    exec,
    log,
    resolveVariables: resolveVariablesFn,
    steps,
  });

  assert.ok(!loggedLines.some((l) => l.includes(SECRET_VALUE)));
});
