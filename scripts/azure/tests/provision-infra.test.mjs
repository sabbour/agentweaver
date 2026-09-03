import test from "node:test";
import assert from "node:assert/strict";
import { parseArgs, runInteractiveInstaller } from "../provision-infra.mjs";

test("parseArgs accepts the optional Entra enterprise app object ID flag", () => {
  const parsed = parseArgs([
    "--entra-client-id", "client-id",
    "--entra-tenant-id", "tenant-id",
    "--entra-enterprise-app-object-id", "enterprise-object-id",
  ]);

  assert.deepEqual(parsed, {
    flags: {
      ENTRA_CLIENT_ID: "client-id",
      ENTRA_TENANT_ID: "tenant-id",
      ENTRA_ENTERPRISE_APP_OBJECT_ID: "enterprise-object-id",
    },
    paramsFile: undefined,
    help: false,
  });

  test("parseArgs accepts OAuth Key Vault certificate family overrides", () => {
    const parsed = parseArgs([
      "--oauth-signing-certificate-name", "oauth-signing-next",
      "--oauth-encryption-certificate-name=oauth-encryption-next",
    ]);
    assert.deepEqual(parsed.flags, {
      OAUTH_SIGNING_CERTIFICATE_NAME: "oauth-signing-next",
      OAUTH_ENCRYPTION_CERTIFICATE_NAME: "oauth-encryption-next",
    });
  });
});

test("runInteractiveInstaller allows the optional Entra enterprise app object ID prompt to be left blank", async () => {
  const textAnswers = [
    "sub-id",
    "agentweaver-rg",
    "westus2",
    "agentweaver-aks",
    "agentweaverregistry",
    "Standard_D4s_v6",
    "agentweaver-kv",
    "westus2",
    "agentweaver-pg",
    "westus2",
    "ZoneRedundant",
    "private",
    "client-id",
    "tenant-id",
    "",
  ];
  const prompt = {
    select: async (_label, choices) => choices[0].value,
    text: async () => textAnswers.shift(),
  };
  const az = {
    listSubscriptions: async () => [],
    showAccount: async () => null,
    listResourceGroups: async () => [],
    listLocations: async () => [],
    setActiveSubscription: async () => {},
  };
  const log = {
    banner() {},
    info() {},
    section() {},
  };

  const collected = await runInteractiveInstaller({ prompt, az, log });

  assert.equal(collected.AUTH_MODE, "Entra");
  assert.equal(collected.ENTRA_CLIENT_ID, "client-id");
  assert.equal(collected.ENTRA_TENANT_ID, "tenant-id");
  assert.equal(collected.ENTRA_ENTERPRISE_APP_OBJECT_ID, "");
});
