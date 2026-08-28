// variables.test.mjs -- IMAGE_TAG derivation parity tests for variables.mjs,
// with `az` and `git` stubbed so these run without Azure or a git context.

import test from "node:test";
import assert from "node:assert/strict";
import {
  resolveVariables,
  deriveImageTag,
  validateImageTag,
  validateQualifiedImageReference,
  resolveKeyvaultName,
  InvalidImageTagError,
  InvalidImageReferenceError,
  MissingRequiredVariableError,
  DEFAULTS,
} from "../variables.mjs";

const FAKE_REPO_ROOT = "C:\\fake\\repo";
const TEST_KEYVAULT_NAME = "test-kv-fixture";

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

test("validateQualifiedImageReference: accepts tag and digest forms with an explicit registry", () => {
  assert.doesNotThrow(() => validateQualifiedImageReference("ghcr.io/someuser/agentweaver-api:v1.2.3", "IMAGE_API"));
  assert.doesNotThrow(() => validateQualifiedImageReference("localhost:5000/someuser/agentweaver-api:v1", "IMAGE_API"));
  assert.doesNotThrow(() => validateQualifiedImageReference("myregistry.local/someuser/agentweaver-api:v1", "IMAGE_API"));
  assert.doesNotThrow(() => validateQualifiedImageReference("docker.io/someuser/agentweaver-mcp@sha256:" + "a".repeat(64), "IMAGE_MCP"));
});

test("validateQualifiedImageReference: rejects shorthand or malformed refs", () => {
  assert.throws(() => validateQualifiedImageReference("agentweaver-api:v1.2.3", "IMAGE_API"), InvalidImageReferenceError);
  assert.throws(() => validateQualifiedImageReference("myorg/myimage:v1.2.3", "IMAGE_API"), InvalidImageReferenceError);
  assert.throws(() => validateQualifiedImageReference("someuser/agentweaver-api:v1", "IMAGE_API"), InvalidImageReferenceError);
  assert.throws(() => validateQualifiedImageReference("ghcr.io/someuser/agentweaver-api", "IMAGE_API"), InvalidImageReferenceError);
  assert.throws(() => validateQualifiedImageReference("ghcr.io/someuser/agentweaver-api@sha256:1234", "IMAGE_API"), InvalidImageReferenceError);
});

test("deriveImageTag: env IMAGE_TAG takes precedence over git", async () => {
  const tag = await deriveImageTag({
    env: { IMAGE_TAG: "v9.9.9" },
    repoRoot: FAKE_REPO_ROOT,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(tag, "v9.9.9");
});

test("deriveImageTag: defaults to git short SHA instead of VERSION", async () => {
  const tag = await deriveImageTag({
    env: {},
    repoRoot: FAKE_REPO_ROOT,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(tag, "deadbee");
});

test("deriveImageTag: throws when git cannot resolve and no override is supplied", async () => {
  await assert.rejects(
    () =>
      deriveImageTag({
        env: {},
        repoRoot: FAKE_REPO_ROOT,
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
        gitShortSha: async () => "deadbee",
      }),
    InvalidImageTagError,
  );
});

test("resolveKeyvaultName: throws MissingRequiredVariableError when unset (no generic default)", () => {
  assert.throws(() => resolveKeyvaultName({}), MissingRequiredVariableError);
  assert.throws(() => resolveKeyvaultName({ KEYVAULT_NAME: "" }), MissingRequiredVariableError);
});

test("resolveKeyvaultName: returns the explicit env override verbatim", () => {
  assert.equal(resolveKeyvaultName({ KEYVAULT_NAME: "my-real-vault" }), "my-real-vault");
});

test("resolveVariables: throws MissingRequiredVariableError when KEYVAULT_NAME is unset (fail-fast, no bogus default)", async () => {
  await assert.rejects(
    () =>
      resolveVariables({
        env: {},
        repoRoot: FAKE_REPO_ROOT,
        resolveLive: false,
        gitShortSha: async () => "deadbee",
      }),
    MissingRequiredVariableError,
  );
});

test("resolveVariables: applies env-var defaults matching 00-variables.sh", async () => {
  const vars = await resolveVariables({
    env: { KEYVAULT_NAME: TEST_KEYVAULT_NAME },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.RESOURCE_GROUP, DEFAULTS.RESOURCE_GROUP);
  assert.equal(vars.CLUSTER_NAME, DEFAULTS.CLUSTER_NAME);
  assert.equal(vars.ACR_NAME, DEFAULTS.ACR_NAME);
  assert.equal(vars.LOCATION, DEFAULTS.LOCATION);
  assert.equal(vars.NODE_VM_SIZE, DEFAULTS.NODE_VM_SIZE);
  assert.equal(vars.MONITORING_LOCATION, DEFAULTS.LOCATION);
  assert.equal(vars.PG_SERVER_NAME, DEFAULTS.PG_SERVER_NAME);
  assert.equal(vars.PG_LOCATION, DEFAULTS.LOCATION);
  assert.equal(vars.PG_HA_MODE, DEFAULTS.PG_HA_MODE);
  assert.equal(vars.PG_ACCESS_MODE, DEFAULTS.PG_ACCESS_MODE);
  assert.equal(vars.KEYVAULT_NAME, TEST_KEYVAULT_NAME, "KEYVAULT_NAME has no generic default -- must come from env");
  assert.equal(vars.NAMESPACE, DEFAULTS.NAMESPACE);
  assert.equal(vars.KATA_POOL_NAME, DEFAULTS.KATA_POOL_NAME);
  assert.equal(vars.APP_POOL_NAME, DEFAULTS.APP_POOL_NAME);
  assert.equal(vars.ACR_LOGIN_SERVER, `${DEFAULTS.ACR_NAME}.azurecr.io`);
  assert.equal(vars.AGENTHOST_KEYVAULT_URI, `https://${TEST_KEYVAULT_NAME}.vault.azure.net/`);
  assert.equal(vars.IMAGE_TAG, "deadbee");
  assert.equal(vars.AGENTHOST_IMAGE_TAG, "deadbee", "AGENTHOST_IMAGE_TAG defaults to IMAGE_TAG");
  assert.equal(vars.AUTH_MODE, DEFAULTS.AUTH_MODE, "Entra is the only browser sign-in mode");
  assert.equal(vars.AUTH_MODE, "Entra");
  assert.equal(vars.ENTRA_CLIENT_ID, "", "no generic default -- empty means Entra mode is not configured");
  assert.equal(vars.ENTRA_TENANT_ID, "");
});

test("resolveVariables: AUTH_MODE/ENTRA_CLIENT_ID/ENTRA_TENANT_ID env overrides beat the defaults", async () => {
  const vars = await resolveVariables({
    env: {
      KEYVAULT_NAME: TEST_KEYVAULT_NAME,
      AUTH_MODE: "Entra",
      ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
      ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000",
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.AUTH_MODE, "Entra");
  assert.equal(vars.ENTRA_CLIENT_ID, "11111111-2222-3333-4444-555555555555");
  assert.equal(vars.ENTRA_TENANT_ID, "66666666-7777-8888-9999-000000000000");
});

test("resolveVariables: forwards opt-in ACR CLI timeout settings", async () => {
  const vars = await resolveVariables({
    env: {
      KEYVAULT_NAME: TEST_KEYVAULT_NAME,
      ACR_BUILD_TIMEOUT_MS: "1800000",
      ACR_IMPORT_TIMEOUT_MS: "600000",
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.ACR_BUILD_TIMEOUT_MS, "1800000");
  assert.equal(vars.ACR_IMPORT_TIMEOUT_MS, "600000");
});

test("resolveVariables: env overrides beat defaults for every field", async () => {
  const vars = await resolveVariables({
    env: {
      RESOURCE_GROUP: "custom-rg",
      CLUSTER_NAME: "custom-cluster",
      ACR_NAME: "customacr",
      LOCATION: "eastus",
      NODE_VM_SIZE: "Standard_D8s_v6",
      MONITORING_LOCATION: "northeurope",
      PG_SERVER_NAME: "custom-pg",
      PG_LOCATION: "eastus2",
      PG_HA_MODE: "Disabled",
      PG_ACCESS_MODE: "public",
      KEYVAULT_NAME: "custom-kv",
      NAMESPACE: "custom-ns",
      KATA_POOL_NAME: "customkata",
      APP_POOL_NAME: "customapp",
      IMAGE_TAG: "v2.0.0",
      IMAGE_API: "ghcr.io/custom/agentweaver-api:v2.0.0",
      IMAGE_FRONTEND: "ghcr.io/custom/agentweaver-frontend:v2.0.0",
      IMAGE_MCP: "ghcr.io/custom/agentweaver-mcp:v2.0.0",
      IMAGE_AGENT_HOST: "ghcr.io/custom/agentweaver-agent-host:v2.0.0",
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.RESOURCE_GROUP, "custom-rg");
  assert.equal(vars.CLUSTER_NAME, "custom-cluster");
  assert.equal(vars.ACR_NAME, "customacr");
  assert.equal(vars.ACR_LOGIN_SERVER, "customacr.azurecr.io");
  assert.equal(vars.LOCATION, "eastus");
  assert.equal(vars.NODE_VM_SIZE, "Standard_D8s_v6");
  assert.equal(vars.MONITORING_LOCATION, "northeurope");
  assert.equal(vars.PG_SERVER_NAME, "custom-pg");
  assert.equal(vars.PG_LOCATION, "eastus2");
  assert.equal(vars.PG_HA_MODE, "Disabled");
  assert.equal(vars.PG_ACCESS_MODE, "public");
  assert.equal(vars.KEYVAULT_NAME, "custom-kv");
  assert.equal(vars.NAMESPACE, "custom-ns");
  assert.equal(vars.KATA_POOL_NAME, "customkata");
  assert.equal(vars.APP_POOL_NAME, "customapp");
  assert.equal(vars.IMAGE_TAG, "v2.0.0");
  assert.equal(vars.IMAGE_API, "ghcr.io/custom/agentweaver-api:v2.0.0");
  assert.equal(vars.IMAGE_FRONTEND, "ghcr.io/custom/agentweaver-frontend:v2.0.0");
  assert.equal(vars.IMAGE_MCP, "ghcr.io/custom/agentweaver-mcp:v2.0.0");
  assert.equal(vars.IMAGE_AGENT_HOST, "ghcr.io/custom/agentweaver-agent-host:v2.0.0");
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
    env: { KEYVAULT_NAME: TEST_KEYVAULT_NAME },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: false,
    az,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "");
  assert.equal(vars.IDENTITY_CLIENT_ID, "");
  assert.equal(vars.AGENTHOST_IDENTITY_CLIENT_ID, "");
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
      return name === "agentweaver-agenthost-identity" ? "agenthost-client-abc" : "client-abc";
    },
    getLogAnalyticsWorkspaceCustomerId: async (rg, name) => {
      calls.push(["workspace", rg, name]);
      return "workspace-xyz";
    },
  };
  const vars = await resolveVariables({
    env: { RESOURCE_GROUP: "my-rg", KEYVAULT_NAME: TEST_KEYVAULT_NAME },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: true,
    az,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "tenant-123");
  assert.equal(vars.IDENTITY_CLIENT_ID, "client-abc");
  assert.equal(vars.AGENTHOST_IDENTITY_CLIENT_ID, "agenthost-client-abc");
  assert.equal(vars.APPINSIGHTS_WORKSPACE_ID, "workspace-xyz");
  assert.deepEqual(calls, [
    "tenant",
    ["identity", "my-rg", "agentweaver-api-identity"],
    ["identity", "my-rg", "agentweaver-agenthost-identity"],
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
      AGENTHOST_IDENTITY_CLIENT_ID: "env-agenthost-client",
      APPINSIGHTS_WORKSPACE_ID: "env-workspace",
      KEYVAULT_NAME: TEST_KEYVAULT_NAME,
    },
    repoRoot: FAKE_REPO_ROOT,
    resolveLive: true,
    az,
    gitShortSha: async () => "deadbee",
  });
  assert.equal(vars.TENANT_ID, "env-tenant");
  assert.equal(vars.IDENTITY_CLIENT_ID, "env-client");
  assert.equal(vars.AGENTHOST_IDENTITY_CLIENT_ID, "env-agenthost-client");
  assert.equal(vars.APPINSIGHTS_WORKSPACE_ID, "env-workspace");
});

test("resolveVariables: AGENTHOST_IMAGE_TAG explicit override is validated independently", async () => {
  await assert.rejects(
    () =>
      resolveVariables({
        env: { IMAGE_TAG: "v1.0.0", AGENTHOST_IMAGE_TAG: "latest", KEYVAULT_NAME: TEST_KEYVAULT_NAME },
        repoRoot: FAKE_REPO_ROOT,
        resolveLive: false,
        gitShortSha: async () => "deadbee",
      }),
    InvalidImageTagError,
  );
});

test("resolveVariables: validates custom image refs when supplied via env", async () => {
  await assert.rejects(
    () =>
      resolveVariables({
        env: { IMAGE_TAG: "v1.0.0", IMAGE_API: "agentweaver-api:v1.0.0", KEYVAULT_NAME: TEST_KEYVAULT_NAME },
        repoRoot: FAKE_REPO_ROOT,
        resolveLive: false,
        gitShortSha: async () => "deadbee",
      }),
    InvalidImageReferenceError,
  );
});
