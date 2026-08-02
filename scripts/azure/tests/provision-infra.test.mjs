// provision-infra.test.mjs -- Tests for provision-infra.mjs: argv parsing, help output, the
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
import { clearSecrets, redact, REDACTED_MARKER } from "../lib/secret.mjs";
import {
  parseArgs,
  HELP_TEXT,
  normalizeGithubOrgList,
  runInteractiveInstaller,
  shouldRunInteractiveInstaller,
  run,
  validateGithubOrgList,
} from "../provision-infra.mjs";
import { NonInteractiveError } from "../lib/prompt.mjs";

function noopLog() {
  const rec = () => () => {};
  return { info: rec(), section: rec(), field: rec(), ok: rec(), skip: rec(), warn: rec(), error: rec(), debug: rec(), command: rec(), banner: rec(), rule: rec(), step: rec(), withProgress: (_label, task) => task() };
}

function fakeStep(name, calls, result = {}) {
  return {
    async run(cfg, opts) {
      calls.push({ step: name, cfg, opts });
      return result;
    },
  };
}

test("validateGithubOrgList: accepts a bare org login", () => {
  assert.equal(validateGithubOrgList("microsoft"), true);
});

test("validateGithubOrgList: accepts org/* (explicit wildcard, same as bare org)", () => {
  assert.equal(validateGithubOrgList("azure-management-and-platforms/*"), true);
});

test("validateGithubOrgList: accepts org/team-slug entries, comma-separated", () => {
  assert.equal(validateGithubOrgList("Azure/aks,Azure/AKS PM,azure-management-and-platforms/*"), true);
});

test("validateGithubOrgList: accepts semicolon as a separator too (matches GitHubOrgList.cs)", () => {
  assert.equal(validateGithubOrgList("Azure/aks;azure-management-and-platforms/*"), true);
});

test("validateGithubOrgList: rejects an empty value", () => {
  assert.match(validateGithubOrgList(""), /Enter at least one/);
});

test("validateGithubOrgList: rejects an invalid org login", () => {
  assert.match(validateGithubOrgList("not a valid org!"), /doesn't look like a valid/);
});

test("validateGithubOrgList: rejects an invalid org before the slash", () => {
  assert.match(validateGithubOrgList("not a valid org!/team"), /doesn't look like a valid/);
});

test("normalizeGithubOrgList: trims, drops empties, and rejoins comma-separated", () => {
  assert.equal(normalizeGithubOrgList(" microsoft , azure/aks ,, "), "microsoft,azure/aks");
});

test("normalizeGithubOrgList: normalizes semicolon-separated input to comma-separated", () => {
  assert.equal(normalizeGithubOrgList("Azure/aks;azure-management-and-platforms/*"), "Azure/aks,azure-management-and-platforms/*");
});

test("parseArgs: recognizes flags and takes values for valued flags", () => {
  const parsed = parseArgs([
    "--skip-postgres",
    "--skip-oauth-key",
    "--image-tag",
    "v1.2.3",
    "--resource-group=my-rg",
    "--node-vm-size",
    "Standard_D8s_v6",
    "--postgres-server-name",
    "custom-pg",
    "--postgres-location",
    "eastus2",
    "--postgres-ha-mode",
    "Disabled",
    "--postgres-access-mode",
    "public",
    "--github-client-secret",
    "shh",
  ]);
  assert.equal(parsed.flags.SKIP_POSTGRES, true);
  assert.equal(parsed.flags.SKIP_OAUTH_KEY, true);
  assert.equal(parsed.flags.IMAGE_TAG, "v1.2.3");
  assert.equal(parsed.flags.RESOURCE_GROUP, "my-rg");
  assert.equal(parsed.flags.NODE_VM_SIZE, "Standard_D8s_v6");
  assert.equal(parsed.flags.PG_SERVER_NAME, "custom-pg");
  assert.equal(parsed.flags.PG_LOCATION, "eastus2");
  assert.equal(parsed.flags.PG_HA_MODE, "Disabled");
  assert.equal(parsed.flags.PG_ACCESS_MODE, "public");
  assert.equal(parsed.flags.GITHUB_CLIENT_SECRET, "shh");
});

test("parseArgs: recognizes GHCR import flags", () => {
  const parsed = parseArgs([
    "--image-source",
    "ghcr",
    "--ghcr-ref=v0.15.0",
    "--ghcr-token",
    "topsecret",
    "--force",
  ]);
  assert.equal(parsed.flags.IMAGE_SOURCE, "ghcr");
  assert.equal(parsed.flags.GHCR_REF, "v0.15.0");
  assert.equal(parsed.flags.GHCR_TOKEN, "topsecret");
  assert.equal(parsed.flags.FORCE, true);
});

test("parseArgs: rejects ghcr-owner overrides so GHCR owner is always derived from origin", () => {
  assert.throws(() => parseArgs(["--ghcr-owner", "sabbour"]), /Unknown argument/);
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

test("GitHub org validator: accepts the bare global wildcard alongside existing org/team forms", () => {
  assert.equal(validateGithubOrgList("*"), true);
  assert.equal(validateGithubOrgList("*,microsoft,Azure/AKS PM;contoso/*"), true);
  assert.equal(normalizeGithubOrgList(" * ; microsoft, Azure/AKS PM "), "*,microsoft,Azure/AKS PM");
});

test("GitHub org validator: rejects malformed wildcard forms", () => {
  assert.match(validateGithubOrgList("*/team"), /doesn't look like a valid/);
  assert.match(validateGithubOrgList("**"), /doesn't look like a valid/);
});

test("HELP_TEXT: mentions key flags", () => {
  assert.match(HELP_TEXT, /--skip-postgres/);
  assert.match(HELP_TEXT, /--params-file/);
  assert.match(HELP_TEXT, /--node-vm-size <sku>/);
  assert.match(HELP_TEXT, /--postgres-server-name <name>/);
  assert.match(HELP_TEXT, /--postgres-location <region>/);
  assert.match(HELP_TEXT, /--postgres-ha-mode <mode>/);
  assert.match(HELP_TEXT, /--postgres-access-mode <private\|public>/);
  assert.match(HELP_TEXT, /--image-source <source>/);
  assert.match(HELP_TEXT, /--ghcr-ref <ref>/);
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
  assert.ok(messages.some((m) => m.includes("Agentweaver Azure infrastructure installer")));
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
    NODE_VM_SIZE: e.NODE_VM_SIZE,
    KEYVAULT_NAME: e.KEYVAULT_NAME,
    PG_SERVER_NAME: e.PG_SERVER_NAME,
    PG_LOCATION: e.PG_LOCATION,
    PG_HA_MODE: e.PG_HA_MODE,
    PG_ACCESS_MODE: e.PG_ACCESS_MODE,
    NAMESPACE: e.NAMESPACE,
    IMAGE_TAG: e.IMAGE_TAG ?? "dev",
    AGENTHOST_IMAGE_TAG: "dev",
  });

  const result = await run({
    argv: [
      "--resource-group",
      "my-rg",
      "--node-vm-size",
      "Standard_D8s_v6",
      "--postgres-server-name",
      "custom-pg",
      "--postgres-location",
      "eastus2",
      "--postgres-ha-mode",
      "Disabled",
      "--postgres-access-mode",
      "public",
      "--github-client-id",
      "id-123",
      "--github-client-secret",
      "topsecret",
    ],
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
    ["createCluster", "setupIdentity", "provisionMonitoring", "oauthSigningKey", "provisionPostgres", "buildImages", "genA2aMtlsCerts", "deployStep", "verifyProvenance", "verifyStep"],
  );
  assert.equal(calls[0].cfg.RESOURCE_GROUP, "my-rg");
  assert.equal(calls[0].cfg.NODE_VM_SIZE, "Standard_D8s_v6");
  assert.equal(calls[0].cfg.PG_SERVER_NAME, "custom-pg");
  assert.equal(calls[0].cfg.PG_LOCATION, "eastus2");
  assert.equal(calls[0].cfg.PG_HA_MODE, "Disabled");
  assert.equal(calls[0].cfg.PG_ACCESS_MODE, "public");
  assert.equal(calls[0].cfg.GITHUB_CLIENT_SECRET, "topsecret");
});

test("run: rejects an invalid PG_SERVER_NAME before provisioning starts", async () => {
  await assert.rejects(
    run({
      argv: [
        "--postgres-server-name",
        "Invalid_Name",
        "--github-client-id",
        "id-123",
        "--github-client-secret",
        "topsecret",
      ],
      env: {},
      prompt: { isInteractive: () => false },
      log: noopLog(),
    }),
    /PG_SERVER_NAME must be 3-63 chars of lowercase letters, numbers, or hyphens/,
  );
});

test("run: rejects 1-2 character PG_SERVER_NAME values before provisioning starts", async () => {
  for (const name of ["a", "ab"]) {
    await assert.rejects(
      run({
        argv: [
          "--postgres-server-name",
          name,
          "--github-client-id",
          "id-123",
          "--github-client-secret",
          "topsecret",
        ],
        env: {},
        prompt: { isInteractive: () => false },
        log: noopLog(),
      }),
      /PG_SERVER_NAME must be 3-63 chars of lowercase letters, numbers, or hyphens/,
    );
  }
});

test("run: rejects an invalid PG_HA_MODE before provisioning starts", async () => {
  for (const mode of ["SameZone", "GeoRedundant"]) {
    await assert.rejects(
      run({
        argv: [
          "--postgres-ha-mode",
          mode,
          "--github-client-id",
          "id-123",
          "--github-client-secret",
          "topsecret",
        ],
        env: {},
        prompt: { isInteractive: () => false },
        log: noopLog(),
      }),
      /PG_HA_MODE must be one of: ZoneRedundant, Disabled\./,
    );
  }
});

test("run: rejects an invalid PG_ACCESS_MODE before provisioning starts", async () => {
  for (const mode of ["Public", "internet"]) {
    await assert.rejects(
      run({
        argv: [
          "--postgres-access-mode",
          mode,
          "--github-client-id",
          "id-123",
          "--github-client-secret",
          "topsecret",
        ],
        env: {},
        prompt: { isInteractive: () => false },
        log: noopLog(),
      }),
      /PG_ACCESS_MODE must be one of: private, public\./,
    );
  }
});

test("run: rejects cross-region Postgres when access mode remains private", async () => {
  let resolveVariablesCalls = 0;
  const steps = {
    createCluster: fakeStep("createCluster", []),
  };
  await assert.rejects(
    run({
      argv: [
        "--location",
        "eastus2euap",
        "--postgres-location",
        "eastus2",
        "--github-client-id",
        "id-123",
        "--github-client-secret",
        "topsecret",
      ],
      env: {},
      prompt: { isInteractive: () => false },
      log: noopLog(),
      resolveVariables: async () => {
        resolveVariablesCalls += 1;
        return { IMAGE_TAG: "dev", AGENTHOST_IMAGE_TAG: "dev" };
      },
      steps,
    }),
    /cross-region Postgres, set --postgres-access-mode public/i,
  );
  assert.equal(resolveVariablesCalls, 0, "validation must fail before variable resolution or Azure calls");
});

test("run: allows cross-region Postgres when access mode is explicitly public", async () => {
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
    NODE_VM_SIZE: e.NODE_VM_SIZE,
    KEYVAULT_NAME: e.KEYVAULT_NAME,
    PG_SERVER_NAME: e.PG_SERVER_NAME,
    PG_LOCATION: e.PG_LOCATION,
    PG_HA_MODE: e.PG_HA_MODE,
    PG_ACCESS_MODE: e.PG_ACCESS_MODE,
    NAMESPACE: e.NAMESPACE,
    IMAGE_TAG: e.IMAGE_TAG ?? "dev",
    AGENTHOST_IMAGE_TAG: "dev",
  });

  const result = await run({
    argv: [
      "--location",
      "eastus2euap",
      "--postgres-location",
      "eastus2",
      "--postgres-access-mode",
      "public",
      "--github-client-id",
      "id-123",
      "--github-client-secret",
      "topsecret",
    ],
    env: {
      RESOURCE_GROUP: "my-rg",
      CLUSTER_NAME: "my-cluster",
      ACR_NAME: "myacr",
      KEYVAULT_NAME: "my-kv",
      NAMESPACE: "agentweaver",
    },
    prompt: { isInteractive: () => false },
    exec,
    log: noopLog(),
    resolveVariables: resolveVariablesFn,
    steps,
  });

  assert.equal(result.ok, true);
  assert.equal(calls[0].cfg.LOCATION, "eastus2euap");
  assert.equal(calls[0].cfg.PG_LOCATION, "eastus2");
  assert.equal(calls[0].cfg.PG_ACCESS_MODE, "public");
});

test("run: rejects an invalid NODE_VM_SIZE before provisioning starts", async () => {
  for (const sku of ["", "D4s_v6", "Standard D4s v6"]) {
    await assert.rejects(
      run({
        argv: [
          "--node-vm-size",
          sku,
          "--github-client-id",
          "id-123",
          "--github-client-secret",
          "topsecret",
        ],
        env: {},
        prompt: { isInteractive: () => false },
        log: noopLog(),
      }),
      /NODE_VM_SIZE must be a non-empty Azure VM SKU like Standard_D4s_v6\./,
    );
  }
});

test("run: ghcr image-source resolves derived owner and passes GHCR config through to the image step", async () => {
  clearSecrets();
  const calls = [];
  const steps = {
    createCluster: fakeStep("createCluster", calls),
    setupIdentity: fakeStep("setupIdentity", calls),
    provisionMonitoring: fakeStep("provisionMonitoring", calls),
    oauthSigningKey: fakeStep("oauthSigningKey", calls),
    provisionPostgres: fakeStep("provisionPostgres", calls),
    buildImages: fakeStep("buildImages", calls, {
      expectedImageDigests: { "agentweaver-api": "sha256:" + "a".repeat(64) },
      importedImageSources: {
        "agentweaver-api": {
          digest: "sha256:" + "a".repeat(64),
          sourceCommit: "b".repeat(40),
          sourceRef: "v0.15.0",
        },
      },
    }),
    verifyProvenance: fakeStep("verifyProvenance", calls, { ok: true }),
    genA2aMtlsCerts: fakeStep("genA2aMtlsCerts", calls),
    deployStep: fakeStep("deployStep", calls, { HOST: "agentweaver.example.com", GATEWAY_IP: "1.2.3.4" }),
    verifyStep: fakeStep("verifyStep", calls, { ok: true, pass: 10, fail: 0 }),
  };
  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture(cmd, args) {
      if (cmd === "git" && args.join(" ") === "config --get remote.origin.url") {
        return { stdout: "https://github.com/sabbour/agentweaver.git", stderr: "", code: 0 };
      }
      return { stdout: "", stderr: "", code: 0 };
    },
  };
  const resolveVariablesFn = async () => ({
    RESOURCE_GROUP: "my-rg",
    CLUSTER_NAME: "my-cluster",
    ACR_NAME: "myacr",
    ACR_LOGIN_SERVER: "myacr.azurecr.io",
    LOCATION: "westus2",
    KEYVAULT_NAME: "my-kv",
    NAMESPACE: "agentweaver",
    IMAGE_TAG: "v0.15.0",
    AGENTHOST_IMAGE_TAG: "v0.15.0",
  });

  await run({
    argv: [
      "--image-source",
      "ghcr",
      "--ghcr-ref",
      "v0.15.0",
      "--github-client-id",
      "id-123",
      "--github-client-secret",
      "oauthsecret",
      "--ghcr-token",
      "ghcrsecret",
    ],
    env: { GITHUB_CLIENT_ID: "", GITHUB_CLIENT_SECRET: "" },
    prompt: { isInteractive: () => false },
    exec,
    log: noopLog(),
    resolveVariables: resolveVariablesFn,
    steps,
  });

  const buildCall = calls.find((c) => c.step === "buildImages");
  assert.equal(buildCall.cfg.IMAGE_SOURCE, "ghcr");
  assert.equal(buildCall.cfg.GHCR_REF, "v0.15.0");
  assert.equal(buildCall.cfg.GHCR_OWNER, "sabbour");
  assert.equal(buildCall.cfg.GHCR_REPOSITORY, "agentweaver");
  assert.equal(buildCall.cfg.GHCR_TOKEN, "ghcrsecret");
  const verifyCall = calls.find((c) => c.step === "verifyProvenance");
  assert.equal(verifyCall.cfg.IMPORTED_IMAGE_SOURCES["agentweaver-api"].sourceRef, "v0.15.0");
  assert.equal(redact("ghcrsecret"), REDACTED_MARKER);
  clearSecrets();
});

test("run: IMAGE_SOURCE=ghcr requires GHCR_REF", async () => {
  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture(cmd, args) {
      if (cmd === "git" && args.join(" ") === "config --get remote.origin.url") {
        return { stdout: "https://github.com/sabbour/agentweaver.git", stderr: "", code: 0 };
      }
      return { stdout: "", stderr: "", code: 0 };
    },
  };
  await assert.rejects(
    run({
      argv: ["--image-source", "ghcr", "--github-client-id", "id", "--github-client-secret", "secret"],
      env: {},
      prompt: { isInteractive: () => false },
      exec,
      log: noopLog(),
      resolveVariables: async () => ({ RESOURCE_GROUP: "rg", IMAGE_TAG: "v0.15.0", AGENTHOST_IMAGE_TAG: "v0.15.0" }),
    }),
    /GHCR_REF is required/,
  );
});

test("run: IMAGE_SOURCE=ghcr requires a GitHub origin remote so the GHCR owner cannot be overridden", async () => {
  const exec = {
    async run() {
      return { code: 0 };
    },
    async capture() {
      return { stdout: "", stderr: "", code: 1 };
    },
  };
  await assert.rejects(
    run({
      argv: ["--image-source", "ghcr", "--ghcr-ref", "v0.15.0", "--github-client-id", "id", "--github-client-secret", "secret"],
      env: {},
      prompt: { isInteractive: () => false },
      exec,
      log: noopLog(),
      resolveVariables: async () => ({ RESOURCE_GROUP: "rg", IMAGE_TAG: "v0.15.0", AGENTHOST_IMAGE_TAG: "v0.15.0" }),
    }),
    /requires a GitHub origin remote/i,
  );
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
// -- see tests/dev.test.mjs. provision-infra.mjs is Azure-only.

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
  let rgChoicesSeen = null;
  const prompt = {
    select: async (question, choices) => {
      selectCalls.push(question);
      if (question.toLowerCase().includes("resource group")) {
        rgChoicesSeen = choices;
        return choices.find((c) => c.label === "existing-rg").value;
      }
      return choices[0].value;
    },
    text: async (question, opts = {}) => opts.default ?? `answer-to-${question}`,
    secret: async () => "super-secret-value",
  };
  const collected = await runInteractiveInstaller({ prompt, az, log: noopLog() });
  assert.equal(collected.RESOURCE_GROUP, "existing-rg");
  assert.equal(rgChoicesSeen[0].label, "Create new...", "Create new... must be the first resource-group choice");
  assert.equal(collected.LOCATION, "westus2");
  assert.equal(collected.NODE_VM_SIZE, "Standard_D4s_v6");
  assert.equal(collected.PG_LOCATION, "westus2");
  assert.equal(collected.PG_ACCESS_MODE, "private");
  assert.equal(collected.GITHUB_CLIENT_SECRET, "super-secret-value");
  assert.equal(collected.GITHUB_ALLOWED_ORG, "microsoft");
  assert.ok(selectCalls.length >= 3);
});

test("runInteractiveInstaller: sorts resource groups and locations alphabetically for scannable menus", async () => {
  const az = {
    listSubscriptions: async () => [],
    showAccount: async () => null,
    listResourceGroups: async () => [{ name: "zeta-rg" }, { name: "alpha-rg" }, { name: "Mango-rg" }],
    listLocations: async () => [
      { name: "westus2", displayName: "West US 2" },
      { name: "eastus", displayName: "East US" },
      { name: "northeurope", displayName: "North Europe" },
    ],
  };
  let rgChoicesSeen = null;
  let locChoicesSeen = null;
  const prompt = {
    select: async (question, choices) => {
      if (question.toLowerCase().includes("resource group")) rgChoicesSeen = choices;
      if (question.toLowerCase().includes("location")) locChoicesSeen = choices;
      return choices[0].value;
    },
    text: async (question, opts = {}) => opts.default ?? `answer-to-${question}`,
    secret: async () => "super-secret-value",
  };
  await runInteractiveInstaller({ prompt, az, log: noopLog() });
  assert.deepEqual(
    rgChoicesSeen.map((c) => c.label),
    ["Create new...", "alpha-rg", "Mango-rg", "zeta-rg"],
    "resource groups sorted case-insensitively after 'Create new...'",
  );
  assert.deepEqual(
    locChoicesSeen.map((c) => c.label),
    ["East US", "North Europe", "West US 2"],
    "locations sorted alphabetically by display name",
  );
});

test("runInteractiveInstaller: normalizes a comma-separated GitHub org allowlist typed by the user", async () => {
  const az = {
    listSubscriptions: async () => [],
    showAccount: async () => null,
    listResourceGroups: async () => [],
    listLocations: async () => [],
  };
  const prompt = {
    select: async (_q, choices) => choices[0].value,
    text: async (question) => {
      if (question.startsWith("GitHub org(s)")) return " microsoft ,  azure-management-and-platforms ,,";
      return `answer-to-${question}`;
    },
    secret: async () => "super-secret-value",
  };
  const collected = await runInteractiveInstaller({ prompt, az, log: noopLog() });
  assert.equal(collected.GITHUB_ALLOWED_ORG, "microsoft,azure-management-and-platforms");
});

test("run: GITHUB_ALLOWED_ORG resolves from a flag, appears in the resolved-config log, and reaches resolveVariables' env override", async () => {
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
  let capturedEnv;
  const resolveVariablesFn = async ({ env: e }) => {
    capturedEnv = e;
    return { RESOURCE_GROUP: e.RESOURCE_GROUP, GITHUB_ALLOWED_ORG: e.GITHUB_ALLOWED_ORG, IMAGE_TAG: "dev", AGENTHOST_IMAGE_TAG: "dev" };
  };
  const fields = [];
  const log = { ...noopLog(), field: (label, value) => fields.push([label, value]) };

  await run({
    argv: [
      "--resource-group",
      "my-rg",
      "--github-client-id",
      "id",
      "--github-client-secret",
      "sec",
      "--github-allowed-org",
      " microsoft , azure-management-and-platforms ",
    ],
    env: {},
    prompt: { isInteractive: () => false },
    exec,
    log,
    resolveVariables: resolveVariablesFn,
    steps,
  });

  assert.equal(capturedEnv.GITHUB_ALLOWED_ORG, "microsoft,azure-management-and-platforms");
  const orgField = fields.find(([label]) => label === "Allowed GitHub org(s)");
  assert.ok(orgField, "expected an 'Allowed GitHub org(s)' field in the resolved-config/outputs logs");
  assert.equal(orgField[1], "microsoft,azure-management-and-platforms");
});

test("run: --github-allowed-org rejects an invalid org login with a clear validation error", async () => {
  await assert.rejects(
    run({
      argv: ["--github-client-id", "id", "--github-client-secret", "sec", "--github-allowed-org", "not a valid org!"],
      env: {},
      prompt: { isInteractive: () => false },
      log: noopLog(),
    }),
    /GITHUB_ALLOWED_ORG/,
  );
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
