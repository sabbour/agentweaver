// provisioning-steps.test.mjs -- Smoke / argument-construction tests for the
// newly ported provisioning steps: 10-create-cluster.mjs,
// 15-setup-identity.mjs, 15-provision-monitoring.mjs,
// 16-provision-oauth-signing-key.mjs, 17-provision-postgres.mjs, and
// gen-a2a-mtls-certs.mjs. All az/kubectl/openssl calls are stubbed -- no
// real Azure CLI, kubectl, or openssl invocation.

import test from "node:test";
import assert from "node:assert/strict";
import * as createCluster from "../steps/10-create-cluster.mjs";
import * as setupIdentity from "../steps/15-setup-identity.mjs";
import * as provisionMonitoring from "../steps/15-provision-monitoring.mjs";
import * as provisionOauthKey from "../steps/16-provision-oauth-signing-key.mjs";
import * as provisionPostgres from "../steps/17-provision-postgres.mjs";
import * as genA2aMtlsCerts from "../steps/gen-a2a-mtls-certs.mjs";

const CFG = Object.freeze({
  RESOURCE_GROUP: "agentweaver-rg",
  CLUSTER_NAME: "agentweaver-aks",
  ACR_NAME: "agentweaverregistry",
  LOCATION: "westus2",
  NAMESPACE: "agentweaver",
  KATA_POOL_NAME: "katapool",
  APP_POOL_NAME: "apppool",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  KEYVAULT_NAME: "test-kv-fixture",
  TENANT_ID: "66666666-7777-8888-9999-000000000000",
  IDENTITY_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
});

function noopLog() {
  const rec = () => () => {};
  return {
    info: rec(),
    section: rec(),
    field: rec(),
    ok: rec(),
    skip: rec(),
    warn: rec(),
    error: rec(),
    debug: rec(),
    command: rec(),
  };
}

function fakeExec({ captureImpl, runImpl } = {}) {
  const calls = { capture: [], run: [] };
  return {
    calls,
    isDryRun: () => false,
    async capture(cmd, args, opts) {
      calls.capture.push({ cmd, args, opts });
      if (captureImpl) {
        const result = await captureImpl(cmd, args, opts);
        if (result) return result;
      }
      return { stdout: "", stderr: "", code: 0 };
    },
    async run(cmd, args, opts) {
      calls.run.push({ cmd, args, opts });
      if (runImpl) {
        const result = await runImpl(cmd, args, opts);
        if (result) return result;
      }
      return { code: 0 };
    },
  };
}

// -------------------- 10-create-cluster.mjs --------------------

test("10-create-cluster: existence-check helpers interpret az/kubectl exit codes correctly", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "group" && args[1] === "exists") return { stdout: "true", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "acr" && args[1] === "show") return { stdout: "", stderr: "not found", code: 1 };
    },
  });
  assert.equal(await createCluster.resourceGroupExists(CFG, { exec }), true);
  assert.equal(await createCluster.acrExists(CFG, { exec }), false);
});

test("10-create-cluster: run() creates RG/ACR/cluster/node pools when absent, and skips when present", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "group" && args[1] === "exists") return { stdout: "false", stderr: "", code: 0 };
      if (cmd === "az" && args.includes("show") && args[1] === "acr") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "acr" && args[1] === "show") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "aks" && args[1] === "nodepool" && args[2] === "show") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "crd") return { stdout: "", stderr: "", code: 1 };
      if (args.includes("--query") && args[args.indexOf("--query") + 1] === "id") return { stdout: "/subscriptions/x/resourceGroups/agentweaver-rg/providers/Microsoft.ContainerRegistry/registries/agentweaverregistry", stderr: "", code: 0 };
      return null;
    },
  });
  const log = noopLog();
  await createCluster.run(CFG, { exec, log });

  const runCommands = exec.calls.run.map((c) => `${c.cmd} ${c.args.slice(0, 2).join(" ")}`);
  assert.ok(runCommands.some((c) => c.startsWith("az group create")));
  assert.ok(runCommands.some((c) => c.startsWith("az acr create")));
  assert.ok(runCommands.some((c) => c.startsWith("az aks create")));
  assert.ok(runCommands.some((c) => c === "az aks nodepool"));

  // Security: the system pool created by `az aks create` must disable SSH
  // access (not enable it via --generate-ssh-keys), matching the user pools.
  const createCall = exec.calls.run.find((c) => c.cmd === "az" && c.args[0] === "aks" && c.args[1] === "create");
  assert.ok(createCall, "expected an 'az aks create' call");
  const sshIdx = createCall.args.indexOf("--ssh-access");
  assert.ok(sshIdx !== -1 && createCall.args[sshIdx + 1] === "disabled", "az aks create must pass --ssh-access disabled");
  assert.ok(!createCall.args.includes("--generate-ssh-keys"), "az aks create must not enable SSH via --generate-ssh-keys");

  // App routing: use the Gateway API / istio path only. The managed default
  // nginx ingress controller must be skipped at create time (we route via
  // HTTPRoute on the istio gateway), otherwise app-routing provisions an
  // unused second public LoadBalancer.
  const nginxIdx = createCall.args.indexOf("--app-routing-default-nginx-controller");
  assert.ok(
    nginxIdx !== -1 && createCall.args[nginxIdx + 1] === "None",
    "az aks create must pass --app-routing-default-nginx-controller None",
  );
});

test("10-create-cluster: skips resource creation when everything already exists", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "group" && args[1] === "exists") return { stdout: "true", stderr: "", code: 0 };
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "crd") return { stdout: "", stderr: "", code: 0 };
      return { stdout: "ok", stderr: "", code: 0 }; // acr/aks/nodepool show all succeed => exist
    },
  });
  const log = noopLog();
  await createCluster.run(CFG, { exec, log });
  const runCommands = exec.calls.run.map((c) => `${c.cmd} ${c.args.slice(0, 2).join(" ")}`);
  assert.ok(!runCommands.some((c) => c.startsWith("az group create")));
  assert.ok(!runCommands.some((c) => c.startsWith("az acr create")));
  assert.ok(!runCommands.some((c) => c.startsWith("az aks create")));
});

test("10-create-cluster: existing cluster app-routing reconciliation is a no-op when already in gateway-api/nginx-none state", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args, opts) => {
      if (cmd === "az" && args[0] === "group" && args[1] === "exists") return { stdout: "true", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "acr" && args[1] === "show" && args.includes("--query") && args[args.indexOf("--query") + 1] === "id") {
        return { stdout: "/subscriptions/x/resourceGroups/agentweaver-rg/providers/Microsoft.ContainerRegistry/registries/agentweaverregistry", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "show" && args.includes("--query") && args[args.indexOf("--query") + 1] === "addonProfiles") {
        return { stdout: "", stderr: "", code: 0, json: { appRoutingIstio: { enabled: true } } };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "approuting" && args[2] === "show") {
        return {
          stdout: "",
          stderr: "",
          code: 0,
          json: { nginx: { type: "None" }, gatewayApi: { enabled: true, implementation: "istio" } },
        };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "approuting" && args[2] === "defaultdomain" && args[3] === "show") {
        return { stdout: "", stderr: "", code: 0, json: { enabled: true, fqdn: "example.eastus.aksapp.io" } };
      }
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "crd") return { stdout: "", stderr: "", code: 0 };
      return { stdout: "ok", stderr: "", code: 0, json: opts?.json ? {} : undefined };
    },
  });
  await createCluster.run(CFG, { exec, log: noopLog() });
  const reconciliationCalls = exec.calls.run.filter(
    (call) => call.cmd === "az" && call.args[0] === "aks" && call.args[1] === "approuting",
  );
  assert.equal(reconciliationCalls.length, 0, "already-reconciled clusters must not run approuting update commands");
});

test("10-create-cluster: existing cluster app-routing reconciliation performs targeted updates only when needed", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args, opts) => {
      if (cmd === "az" && args[0] === "group" && args[1] === "exists") return { stdout: "true", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "acr" && args[1] === "show" && args.includes("--query") && args[args.indexOf("--query") + 1] === "id") {
        return { stdout: "/subscriptions/x/resourceGroups/agentweaver-rg/providers/Microsoft.ContainerRegistry/registries/agentweaverregistry", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "show" && args.includes("--query") && args[args.indexOf("--query") + 1] === "addonProfiles") {
        return { stdout: "", stderr: "", code: 0, json: { httpApplicationRouting: { enabled: true } } };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "approuting" && args[2] === "show") {
        return { stdout: "", stderr: "", code: 0, json: { nginx: { type: "External" }, gatewayApi: { enabled: false } } };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "approuting" && args[2] === "defaultdomain" && args[3] === "show") {
        return { stdout: "", stderr: "not enabled", code: 1 };
      }
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "crd") return { stdout: "", stderr: "", code: 0 };
      return { stdout: "ok", stderr: "", code: 0, json: opts?.json ? {} : undefined };
    },
  });
  await createCluster.run(CFG, { exec, log: noopLog() });
  const gatewayEnable = exec.calls.run.find(
    (call) =>
      call.cmd === "az" &&
      call.args[0] === "aks" &&
      call.args[1] === "approuting" &&
      call.args[2] === "gateway" &&
      call.args[3] === "istio" &&
      call.args[4] === "enable",
  );
  assert.ok(gatewayEnable, "existing clusters missing Gateway API must enable the Istio gateway implementation");
  const appRoutingUpdate = exec.calls.run.find(
    (call) =>
      call.cmd === "az" &&
      call.args[0] === "aks" &&
      call.args[1] === "approuting" &&
      call.args[2] === "update",
  );
  assert.ok(appRoutingUpdate, "existing clusters with managed nginx/default-domain drift must run a targeted approuting update");
  assert.ok(appRoutingUpdate.args.includes("--nginx"));
  assert.ok(appRoutingUpdate.args.includes("None"));
  assert.ok(appRoutingUpdate.args.includes("--enable-default-domain"));
});

// -------------------- 15-setup-identity.mjs --------------------

test("15-setup-identity: resolveGithubCredentials throws a clear error when non-interactive and unset", async () => {
  const prompt = { isInteractive: () => false };
  await assert.rejects(() => setupIdentity.resolveGithubCredentials({}, { prompt }), /GitHub OAuth credentials are missing/);
});

test("15-setup-identity: resolveGithubCredentials passes through cfg-supplied values without prompting", async () => {
  const prompt = {
    isInteractive: () => true,
    text: async () => {
      throw new Error("should not prompt when cfg already has a value");
    },
    secret: async () => {
      throw new Error("should not prompt when cfg already has a value");
    },
  };
  const result = await setupIdentity.resolveGithubCredentials(
    { GITHUB_CLIENT_ID: "id123", GITHUB_CLIENT_SECRET: "secret456" },
    { prompt },
  );
  assert.deepEqual(result, { clientId: "id123", clientSecret: "secret456" });
});

test("15-setup-identity: setSecretWithRetry retries on RBAC-propagation Forbidden then succeeds", async () => {
  let attempts = 0;
  const exec = fakeExec({
    captureImpl: () => {
      attempts += 1;
      if (attempts < 3) return { stdout: "", stderr: "Forbidden by RBAC", code: 1 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const log = noopLog();
  const sleep = async () => {};
  await setupIdentity.setSecretWithRetry("test-kv-fixture", "github-client-id", "value", { exec, log, sleep, maxAttempts: 5 });
  assert.equal(attempts, 3);
});

test("15-setup-identity: setSecretWithRetry throws immediately on a non-RBAC failure", async () => {
  const exec = fakeExec({ captureImpl: () => ({ stdout: "", stderr: "vault not found", code: 1 }) });
  const log = noopLog();
  await assert.rejects(() => setupIdentity.setSecretWithRetry("test-kv-fixture", "github-client-id", "value", { exec, log, sleep: async () => {} }));
});

test("15-setup-identity: run() skips all federated credentials when they already exist", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "identity" && args[1] === "federated-credential" && args[2] === "show") {
        return { stdout: "", stderr: "", code: 0 }; // already exists
      }
      if (cmd === "az" && args[0] === "keyvault" && args[1] === "show") return { stdout: "", stderr: "", code: 0 };
      return { stdout: "some-value", stderr: "", code: 0 };
    },
  });
  const az = {};
  const prompt = { isInteractive: () => false };
  const log = noopLog();
  await setupIdentity.run({ ...CFG, GITHUB_CLIENT_ID: "id", GITHUB_CLIENT_SECRET: "secret" }, { exec, log, az, prompt });
  const fedCredCreateCalls = exec.calls.run.filter((c) => c.cmd === "az" && c.args[1] === "federated-credential" && c.args[2] === "create");
  assert.equal(fedCredCreateCalls.length, 0);
});

test("15-setup-identity: run() provisions a dedicated KV-less AgentHost identity (issue #471)", async () => {
  // Distinct object/client ids per identity so we can prove the agent-host identity is NEVER a KV
  // role-assignment target and that the agent-host federated credential is created on it.
  const idValue = (name, field) => {
    if (name === "agentweaver-api-identity") return field === "clientId" ? "api-client-id" : "api-object-id";
    if (name === "agentweaver-agenthost-identity") return field === "clientId" ? "agenthost-client-id" : "agenthost-object-id";
    return "";
  };
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "identity" && args[1] === "show") {
        const name = args[args.indexOf("--name") + 1];
        const field = args[args.indexOf("--query") + 1];
        return { stdout: idValue(name, field), stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "identity" && args[1] === "federated-credential" && args[2] === "show") {
        return { stdout: "", stderr: "not found", code: 1 }; // force create path
      }
      if (cmd === "az" && args[0] === "keyvault" && args[1] === "show") {
        // Return a fake KV id from the `--query id` capture, "" for the existence probe.
        return args.includes("--query")
          ? { stdout: "/subscriptions/x/kv-id", stderr: "", code: 0 }
          : { stdout: "", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "ad" && args[1] === "signed-in-user") return { stdout: "caller-oid", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "true", stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const az = {};
  const prompt = { isInteractive: () => false };
  const log = noopLog();
  const result = await setupIdentity.run(
    { ...CFG, GITHUB_CLIENT_ID: "id", GITHUB_CLIENT_SECRET: "secret" },
    { exec, log, az, prompt },
  );

  // The dedicated identity is created.
  const identityCreates = exec.calls.run.filter(
    (c) => c.cmd === "az" && c.args[0] === "identity" && c.args[1] === "create",
  );
  assert.ok(
    identityCreates.some((c) => c.args[c.args.indexOf("--name") + 1] === "agentweaver-agenthost-identity"),
    "agentweaver-agenthost-identity must be created",
  );

  // The agent-host federated credential is created on the DEDICATED identity, not the API identity.
  const fedCredCreates = exec.calls.run.filter(
    (c) => c.cmd === "az" && c.args[1] === "federated-credential" && c.args[2] === "create",
  );
  const agentHostFedCred = fedCredCreates.find(
    (c) => c.args[c.args.indexOf("--name") + 1] === "agentweaver-agenthost-fedcred",
  );
  assert.ok(agentHostFedCred, "agent-host federated credential must be created");
  assert.equal(
    agentHostFedCred.args[agentHostFedCred.args.indexOf("--identity-name") + 1],
    "agentweaver-agenthost-identity",
    "agent-host fedcred must target the dedicated identity",
  );

  // CRITICAL (issue #471): the agent-host identity object id must NEVER be a Key Vault role target.
  const roleCreates = exec.calls.capture.filter(
    (c) => c.cmd === "az" && c.args[0] === "role" && c.args[1] === "assignment" && c.args[2] === "create",
  );
  for (const c of roleCreates) {
    assert.ok(
      !c.args.includes("agenthost-object-id"),
      "the AgentHost identity must have NO Key Vault role assignments",
    );
  }

  assert.equal(result.AGENTHOST_IDENTITY_CLIENT_ID, "agenthost-client-id");
});

test("15-setup-identity: run() removes a legacy agent-host fedcred from the API identity (migration)", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "identity" && args[1] === "federated-credential" && args[2] === "show") {
        const identityName = args[args.indexOf("--identity-name") + 1];
        // Legacy fedcred still present on the API identity; absent on the dedicated identity.
        return identityName === "agentweaver-api-identity" && args.includes("agentweaver-agenthost-fedcred")
          ? { stdout: "", stderr: "", code: 0 }
          : { stdout: "", stderr: "not found", code: 1 };
      }
      if (cmd === "az" && args[0] === "keyvault" && args[1] === "show") {
        return args.includes("--query") ? { stdout: "/subscriptions/x/kv-id", stderr: "", code: 0 } : { stdout: "", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "true", stderr: "", code: 0 };
      return { stdout: "some-value", stderr: "", code: 0 };
    },
  });
  const az = {};
  const prompt = { isInteractive: () => false };
  const log = noopLog();
  await setupIdentity.run({ ...CFG, GITHUB_CLIENT_ID: "id", GITHUB_CLIENT_SECRET: "secret" }, { exec, log, az, prompt });

  const legacyDelete = exec.calls.run.find(
    (c) =>
      c.cmd === "az" &&
      c.args[1] === "federated-credential" &&
      c.args[2] === "delete" &&
      c.args[c.args.indexOf("--identity-name") + 1] === "agentweaver-api-identity",
  );
  assert.ok(legacyDelete, "legacy agent-host fedcred must be deleted from the API identity");
});

// -------------------- 15-provision-monitoring.mjs --------------------

test("15-provision-monitoring: skips workspace role assignment with a warning when IDENTITY_CLIENT_ID is absent", async () => {
  const exec = fakeExec();
  const log = noopLog();
  const warnings = [];
  log.warn = (msg) => warnings.push(msg);
  await provisionMonitoring.run({ ...CFG, IDENTITY_CLIENT_ID: undefined }, { exec, log });
  assert.ok(warnings.some((w) => /IDENTITY_CLIENT_ID is not set/.test(w)));
});

test("15-provision-monitoring: creates workspace + app insights when absent, stores connection string", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "monitor" && args[1] === "log-analytics" && args[2] === "workspace" && args[3] === "show") {
        return { stdout: "", stderr: "", code: 1 };
      }
      if (cmd === "az" && args[0] === "monitor" && args[1] === "app-insights") {
        if (args[2] === "component" && args[3] === "show") return { stdout: "", stderr: "", code: 1 };
        if (args.includes("--query") && args[args.indexOf("--query") + 1] === "connectionString") {
          return { stdout: "InstrumentationKey=abc123", stderr: "", code: 0 };
        }
      }
      if (args.includes("--query") && args[args.indexOf("--query") + 1] === "length(@)") return { stdout: "0", stderr: "", code: 0 };
      return null;
    },
  });
  const log = noopLog();
  await provisionMonitoring.run(CFG, { exec, log });
  const runCommands = exec.calls.run.map((c) => `${c.cmd} ${c.args.slice(0, 4).join(" ")}`);
  assert.ok(runCommands.some((c) => c.includes("workspace create")));
  assert.ok(runCommands.some((c) => c.includes("component create")));
});

// -------------------- 16-provision-oauth-signing-key.mjs --------------------

test("16-provision-oauth-signing-key: skips key generation when the secret already has a value", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args.includes(provisionOauthKey.SIGNING_KEY_SECRET_NAME)) return { stdout: "-----BEGIN PRIVATE KEY-----", stderr: "", code: 0 };
      if (cmd === "az" && args.includes(provisionOauthKey.API_KEY_SECRET_NAME)) return { stdout: "abc123", stderr: "", code: 0 };
      return null;
    },
  });
  const log = noopLog();
  const result = await provisionOauthKey.run(CFG, { exec, log });
  assert.equal(result.signingKeyProvisioned, false);
  assert.equal(result.apiKeyProvisioned, false);
  assert.ok(!exec.calls.run.some((c) => c.cmd === "openssl"));
});

test("16-provision-oauth-signing-key: generates and stores both secrets when absent, cleans up scratch file", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "keyvault" && args[1] === "secret" && args[2] === "show") return { stdout: "", stderr: "", code: 1 };
      return null;
    },
  });
  const writes = [];
  const removed = [];
  const fsImpl = {
    mkdirSync: () => {},
    writeFileSync: (p, content) => writes.push({ path: p, content }),
    rmSync: (p) => removed.push(p),
  };
  const log = noopLog();
  const result = await provisionOauthKey.run(CFG, { exec, log, fs: fsImpl, repoRoot: "C:\\fake\\repo" });
  assert.equal(result.signingKeyProvisioned, true);
  assert.equal(result.apiKeyProvisioned, true);
  // Uses Node's built-in crypto module (no external openssl process) --
  // assert the scratch PEM was written with real PKCS8 key material and cleaned up.
  assert.ok(!exec.calls.run.some((c) => c.cmd === "openssl"));
  assert.ok(writes.length === 1);
  assert.ok(writes[0].content.includes("-----BEGIN PRIVATE KEY-----"));
  assert.ok(removed.length >= 1); // scratch PEM file cleaned up
});

// -------------------- 17-provision-postgres.mjs --------------------

test("17-provision-postgres: generateAdminPassword strips shell-unsafe chars and caps length at 48", async () => {
  // Uses Node's built-in crypto.randomBytes (no external openssl process) --
  // no exec stubbing needed; assert the invariants the rest of the pipeline
  // depends on: length cap and absence of shell-unsafe characters.
  const password = await provisionPostgres.generateAdminPassword();
  assert.equal(password.length, 48);
  assert.ok(!/[+=/]/.test(password));
});

test("17-provision-postgres: skips server creation when it already exists", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "MC_agentweaver-rg_agentweaver-aks_westus2", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "list") return { stdout: "aks-vnet", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "account" && args[1] === "show") return { stdout: "11111111-1111-1111-1111-111111111111", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "subnet" && args[3] === "show") return { stdout: "/subnet/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "zone") return { stdout: "/zone/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "link") return { stdout: "/link/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "postgres" && args[2] === "show") return { stdout: "Ready", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "postgres" && args[1] === "flexible-server" && args[2] === "db") return { stdout: "agentweaver", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "record-set") return { stdout: "agentweaver-pg", stderr: "", code: 0 };
      if (cmd === "kubectl" && args[0] === "create" && args[1] === "namespace") return { stdout: "apiVersion: v1\nkind: Namespace\n", stderr: "", code: 0 };
      return null;
    },
  });
  const log = noopLog();
  const fsImpl = { mkdirSync: () => {}, writeFileSync: () => {}, rmSync: () => {} };
  const result = await provisionPostgres.run(CFG, { exec, log, fs: fsImpl, repoRoot: "C:\\fake\\repo" });
  assert.equal(result.created, false);
  assert.equal(result.serverState, "Ready");
  assert.ok(!exec.calls.run.some((c) => c.cmd === "az" && c.args[0] === "postgres" && c.args[2] === "create"));
});

test("17-provision-postgres: PG_SERVER_NAME override flows through to az calls and result metadata", async () => {
  const cfg = { ...CFG, PG_SERVER_NAME: "custom-pg" };
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "MC_agentweaver-rg_agentweaver-aks_westus2", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "list") return { stdout: "aks-vnet", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "account" && args[1] === "show") return { stdout: "11111111-1111-1111-1111-111111111111", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "subnet" && args[3] === "show") return { stdout: "/subnet/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "zone") return { stdout: "/zone/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "link") return { stdout: "/link/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "postgres" && args[2] === "show") {
        assert.equal(args[args.indexOf("--name") + 1], "custom-pg");
        return { stdout: "Ready", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "postgres" && args[1] === "flexible-server" && args[2] === "db") {
        assert.equal(args[args.indexOf("--server-name") + 1], "custom-pg");
        return { stdout: "agentweaver", stderr: "", code: 0 };
      }
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "record-set") {
        assert.equal(args[args.indexOf("--name") + 1], "custom-pg");
        return { stdout: "custom-pg", stderr: "", code: 0 };
      }
      if (cmd === "kubectl" && args[0] === "create" && args[1] === "namespace") return { stdout: "apiVersion: v1\nkind: Namespace\n", stderr: "", code: 0 };
      return null;
    },
  });
  const fsImpl = { mkdirSync: () => {}, writeFileSync: () => {}, rmSync: () => {} };
  const result = await provisionPostgres.run(cfg, { exec, log: noopLog(), fs: fsImpl, repoRoot: "C:\\fake\\repo" });
  assert.equal(result.PG_SERVER_NAME, "custom-pg");
  assert.equal(result.PG_FQDN, "custom-pg.postgres.database.azure.com");
});

test("17-provision-postgres: PG_HA_MODE override controls zonal resiliency flags", async () => {
  const cfg = { ...CFG, PG_HA_MODE: "Disabled" };
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "az" && args[0] === "aks" && args[1] === "show") return { stdout: "MC_agentweaver-rg_agentweaver-aks_westus2", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "list") return { stdout: "aks-vnet", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "account" && args[1] === "show") return { stdout: "11111111-1111-1111-1111-111111111111", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "vnet" && args[2] === "subnet" && args[3] === "show") return { stdout: "/subnet/id", stderr: "", code: 0 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "zone") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "link") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "postgres" && args[1] === "flexible-server" && args[2] === "show") {
        if (args.includes("--query") && args[args.indexOf("--query") + 1] === "state") return { stdout: "", stderr: "", code: 1 };
      }
      if (cmd === "az" && args[0] === "postgres" && args[1] === "flexible-server" && args[2] === "db") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "record-set" && args[3] === "a" && args[4] === "show") return { stdout: "", stderr: "", code: 1 };
      if (cmd === "az" && args[0] === "network" && args[1] === "private-dns" && args[2] === "record-set" && args[3] === "a" && args[4] === "list") return { stdout: "10.0.0.4", stderr: "", code: 0 };
      if (cmd === "kubectl" && args[0] === "create" && args[1] === "namespace") return { stdout: "apiVersion: v1\nkind: Namespace\n", stderr: "", code: 0 };
      if (cmd === "kubectl" && args[0] === "create" && args[1] === "secret") return { stdout: "apiVersion: v1\nkind: Secret\n", stderr: "", code: 0 };
      return null;
    },
  });
  const fsImpl = { mkdirSync: () => {}, writeFileSync: () => {}, rmSync: () => {} };
  await provisionPostgres.run(cfg, { exec, log: noopLog(), fs: fsImpl, repoRoot: "C:\\fake\\repo" });

  const createCall = exec.calls.run.find((c) => c.cmd === "az" && c.args[0] === "postgres" && c.args[1] === "flexible-server" && c.args[2] === "create");
  assert.ok(createCall, "expected an 'az postgres flexible-server create' call");
  assert.ok(!createCall.args.includes("--zonal-resiliency"), "Disabled HA mode must not request zonal resiliency");
});

// -------------------- gen-a2a-mtls-certs.mjs --------------------

test("gen-a2a-mtls-certs: skips generation when all three secrets already exist and force is not set", async () => {
  const exec = fakeExec({ captureImpl: () => ({ stdout: "", stderr: "", code: 0 }) }); // all secretExists() checks succeed
  const log = noopLog();
  const result = await genA2aMtlsCerts.run(CFG, { exec, log });
  assert.equal(result.skipped, true);
  assert.ok(!exec.calls.run.some((c) => c.cmd === "openssl"));
});

test("gen-a2a-mtls-certs: throws on partial secret presence without force (refuses inconsistent state)", async () => {
  let call = 0;
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "secret") {
        call += 1;
        return { stdout: "", stderr: "", code: call === 1 ? 0 : 1 }; // first secret exists, others don't
      }
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const log = noopLog();
  await assert.rejects(() => genA2aMtlsCerts.run(CFG, { exec, log }));
});

test("gen-a2a-mtls-certs: generates CA/server/client certs and applies 3 secrets when none exist", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "secret") return { stdout: "", stderr: "", code: 1 }; // none exist
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const writtenFiles = new Map();
  const fsImpl = {
    rmSync: () => {},
    mkdirSync: () => {},
    writeFileSync: (p, content) => writtenFiles.set(p, content),
    readFileSync: () => Buffer.from("fake-cert-bytes"),
  };
  const log = noopLog();
  const result = await genA2aMtlsCerts.run(CFG, { exec, log, fs: fsImpl, repoRoot: "C:\\fake\\repo" });
  assert.equal(result.skipped, false);

  const opensslCalls = exec.calls.run.filter((c) => c.cmd === "openssl");
  assert.ok(opensslCalls.some((c) => c.args[0] === "genrsa"));
  assert.ok(opensslCalls.some((c) => c.args[0] === "req" && c.args.includes("-x509"))); // CA self-sign
  assert.ok(opensslCalls.some((c) => c.args[0] === "x509" && c.args.includes("-req"))); // server/client signing

  const applyCalls = exec.calls.run.filter((c) => c.cmd === "kubectl" && c.args[0] === "apply");
  assert.equal(applyCalls.length, 3);
});

test("gen-a2a-mtls-certs: force=true deletes existing secrets before regenerating", async () => {
  const exec = fakeExec({
    captureImpl: (cmd, args) => {
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "secret") return { stdout: "", stderr: "", code: 0 }; // all exist
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const fsImpl = {
    rmSync: () => {},
    mkdirSync: () => {},
    writeFileSync: () => {},
    readFileSync: () => Buffer.from("fake-cert-bytes"),
  };
  const log = noopLog();
  const result = await genA2aMtlsCerts.run(CFG, { exec, log, fs: fsImpl, repoRoot: "C:\\fake\\repo", force: true });
  assert.equal(result.skipped, false);
  const deleteCalls = exec.calls.run.filter((c) => c.cmd === "kubectl" && c.args[0] === "delete");
  assert.equal(deleteCalls.length, 3);
});
