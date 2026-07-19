// variables.test.mjs -- IMAGE_TAG derivation parity tests for variables.mjs,
// with `az` and `git` stubbed so these run without Azure or a git context.

import test from "node:test";
import assert from "node:assert/strict";
import {
  resolveVariables,
  deriveImageTag,
  validateImageTag,
  InvalidImageTagError,
  DEFAULTS,
} from "../variables.mjs";

const FAKE_REPO_ROOT = "C:\\fake\\repo";

function stubReadFileVersion(version) {
  return (filePath) => {
    if (filePath.endsWith("VERSION")) return version;
    throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
  };
}

function stubReadFileMissing() {
  return () => {
    throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
  };
}

test("validateImageTag: rejects 'latest' and 'latest-release'", () => {
  assert.throws(() => validateImageTag("latest", "IMAGE_TAG"), InvalidImageTagError);
  assert.throws(() => validateImageTag("latest-release", "IMAGE_TAG"), InvalidImageTagError);
});

test("validateImageTag: accepts a git short SHA", () => {
  assert.doesNotThrow(() => validateImageTag("330b2adb", "IMAGE_TAG"));
  assert.doesNotThrow(() => validateImageTag("1234567", "IMAGE_TAG"));
});

test("validateImageTag: accepts a v-prefixed semver tag", () => {
  assert.doesNotThrow(() => validateImageTag("v0.9.70", "IMAGE_TAG"));
  assert.doesNotThrow(() => validateImageTag("v1.2.3-rc.1", "IMAGE_TAG"));
});

test("validateImageTag: rejects anything else", () => {
  assert.throws(() => validateImageTag("my-branch", "IMAGE_TAG"), InvalidImageTagError);
  assert.throws(() => validateImageTag("1.2.3", "IMAGE_TAG"), InvalidImageTagError); // missing 'v' prefix
});

test("deriveImageTag: env IMAGE_TAG takes precedence over VERSION file and git", async () => {
  const tag = await deriveImageTag({
    env: { IMAGE_TAG: "v9.9.9" },
    repoRoot: FAKE_REPO_ROOT,
    readFile: stubReadFileVersion("1.2.3"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(tag, "v9.9.9");
});

test("deriveImageTag: prefers VERSION file over git short SHA when IMAGE_TAG unset", async () => {
  const tag = await deriveImageTag({
    env: {},
    repoRoot: FAKE_REPO_ROOT,
    readFile: stubReadFileVersion("0.9.70\n"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(tag, "v0.9.70");
});

test("deriveImageTag: falls back to git short SHA when no VERSION file exists", async () => {
  const tag = await deriveImageTag({
    env: {},
    repoRoot: FAKE_REPO_ROOT,
    readFile: stubReadFileMissing(),
    gitShortSha: async () => "330b2adb",
  });
  assert.equal(tag, "330b2adb");
});

test("deriveImageTag: throws when neither VERSION file nor git resolves anything", async () => {
  await assert.rejects(
    () =>
      deriveImageTag({
        env: {},
        repoRoot: FAKE_REPO_ROOT,
        readFile: stubReadFileMissing(),
        gitShortSha: async () => "",
      }),
    InvalidImageTagError,
  );
});

test("deriveImageTag: rejects IMAGE_TAG='latest' supplied explicitly via env", async () => {
  await assert.rejects(
    () =>
      deriveImageTag({
        env: { IMAGE_TAG: "latest" },
        repoRoot: FAKE_REPO_ROOT,
        readFile: stubReadFileMissing(),
        gitShortSha: async () => "deadbee",
      }),
    InvalidImageTagError,
  );
});

test("resolveVariables: applies env-var defaults matching 00-variables.sh", async () => {
  const vars = await resolveVariables({
    env: {},
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    readFile: stubReadFileVersion("1.0.0"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.RESOURCE_GROUP, DEFAULTS.RESOURCE_GROUP);
  assert.equal(vars.CLUSTER_NAME, DEFAULTS.CLUSTER_NAME);
  assert.equal(vars.ACR_NAME, DEFAULTS.ACR_NAME);
  assert.equal(vars.LOCATION, DEFAULTS.LOCATION);
  assert.equal(vars.KEYVAULT_NAME, DEFAULTS.KEYVAULT_NAME);
  assert.equal(vars.NAMESPACE, DEFAULTS.NAMESPACE);
  assert.equal(vars.KATA_POOL_NAME, DEFAULTS.KATA_POOL_NAME);
  assert.equal(vars.APP_POOL_NAME, DEFAULTS.APP_POOL_NAME);
  assert.equal(vars.ACR_LOGIN_SERVER, `${DEFAULTS.ACR_NAME}.azurecr.io`);
  assert.equal(vars.AGENTHOST_KEYVAULT_URI, `https://${DEFAULTS.KEYVAULT_NAME}.vault.azure.net/`);
  assert.equal(vars.IMAGE_TAG, "v1.0.0");
  assert.equal(vars.AGENTHOST_IMAGE_TAG, "v1.0.0", "AGENTHOST_IMAGE_TAG defaults to IMAGE_TAG");
});

test("resolveVariables: env overrides beat defaults for every field", async () => {
  const vars = await resolveVariables({
    env: {
      RESOURCE_GROUP: "custom-rg",
      CLUSTER_NAME: "custom-cluster",
      ACR_NAME: "customacr",
      LOCATION: "eastus",
      KEYVAULT_NAME: "custom-kv",
      NAMESPACE: "custom-ns",
      KATA_POOL_NAME: "customkata",
      APP_POOL_NAME: "customapp",
      IMAGE_TAG: "v2.0.0",
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    readFile: stubReadFileMissing(),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.RESOURCE_GROUP, "custom-rg");
  assert.equal(vars.CLUSTER_NAME, "custom-cluster");
  assert.equal(vars.ACR_NAME, "customacr");
  assert.equal(vars.ACR_LOGIN_SERVER, "customacr.azurecr.io");
  assert.equal(vars.LOCATION, "eastus");
  assert.equal(vars.KEYVAULT_NAME, "custom-kv");
  assert.equal(vars.NAMESPACE, "custom-ns");
  assert.equal(vars.KATA_POOL_NAME, "customkata");
  assert.equal(vars.APP_POOL_NAME, "customapp");
  assert.equal(vars.IMAGE_TAG, "v2.0.0");
});

test("resolveVariables: resolveLive=false skips az entirely (no Azure needed for tests)", async () => {
  const az = {
    getTenantId: async () => {
      throw new Error("az must not be called when resolveLive is false");
    },
    getIdentityClientId: async () => {
      throw new Error("az must not be called when resolveLive is false");
    },
    getLogAnalyticsWorkspaceCustomerId: async () => {
      throw new Error("az must not be called when resolveLive is false");
    },
  };
  const vars = await resolveVariables({
    env: {},
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    az,
    readFile: stubReadFileVersion("1.0.0"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "");
  assert.equal(vars.IDENTITY_CLIENT_ID, "");
  assert.equal(vars.APPINSIGHTS_WORKSPACE_ID, "");
});

test("resolveVariables: resolveLive=true calls stubbed az helpers with the right resource group/names", async () => {
  const calls = [];
  const az = {
    getTenantId: async () => {
      calls.push("tenant");
      return "tenant-123";
    },
    getIdentityClientId: async (rg, name) => {
      calls.push(["identity", rg, name]);
      return "client-abc";
    },
    getLogAnalyticsWorkspaceCustomerId: async (rg, name) => {
      calls.push(["workspace", rg, name]);
      return "workspace-xyz";
    },
  };
  const vars = await resolveVariables({
    env: { RESOURCE_GROUP: "my-rg" },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: true,
    az,
    readFile: stubReadFileVersion("1.0.0"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "tenant-123");
  assert.equal(vars.IDENTITY_CLIENT_ID, "client-abc");
  assert.equal(vars.APPINSIGHTS_WORKSPACE_ID, "workspace-xyz");
  assert.deepEqual(calls, [
    "tenant",
    ["identity", "my-rg", "agentweaver-api-identity"],
    ["workspace", "my-rg", "agentweaver-logs"],
  ]);
});

test("resolveVariables: env-supplied live fields short-circuit az (lazy resolution)", async () => {
  const az = {
    getTenantId: async () => {
      throw new Error("must not be called when TENANT_ID already set via env");
    },
    getIdentityClientId: async () => {
      throw new Error("must not be called when IDENTITY_CLIENT_ID already set via env");
    },
    getLogAnalyticsWorkspaceCustomerId: async () => {
      throw new Error("must not be called when APPINSIGHTS_WORKSPACE_ID already set via env");
    },
  };
  const vars = await resolveVariables({
    env: {
      TENANT_ID: "env-tenant",
      IDENTITY_CLIENT_ID: "env-client",
      APPINSIGHTS_WORKSPACE_ID: "env-workspace",
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: true,
    az,
    readFile: stubReadFileVersion("1.0.0"),
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "env-tenant");
  assert.equal(vars.IDENTITY_CLIENT_ID, "env-client");
  assert.equal(vars.APPINSIGHTS_WORKSPACE_ID, "env-workspace");
});

test("resolveVariables: AGENTHOST_IMAGE_TAG explicit override is validated independently", async () => {
  await assert.rejects(
    () =>
      resolveVariables({
        env: { IMAGE_TAG: "v1.0.0", AGENTHOST_IMAGE_TAG: "latest" },
        repoRoot: FAKE_REPO_ROOT,
        resolveLive: false,
        readFile: stubReadFileMissing(),
        gitShortSha: async () => "deadbee",
      }),
    InvalidImageTagError,
  );
});
