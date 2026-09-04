// deploy-apply.test.mjs -- Orchestration/ordering tests for steps/30-deploy.mjs's
// run(), using injected fakes for kubectl side effects (apply/wait/rollout),
// but a REAL `kubectl kustomize` build against the real k8s/base + k8s/overlays
// production overlay on disk (kubectl has built-in Kustomize support -- no
// separate `kustomize` binary is required, matching this repo's decision not
// to add one as a new prerequisite). This gives us confidence the actual
// manifest content 30-deploy.mjs applies is real, kustomize-built YAML (not a
// hand-rolled fake), while still keeping the test hermetic for anything that
// would otherwise touch a real cluster.
//
// Verifies: full apply ordering (including the new synthetic
// _agentweaver-runtime-config.yaml ConfigMap), the SandboxTemplate CRD
// conditional, the two gateway Programmed waits, that a Worker rollout
// timeout is logged as a non-fatal WARNING (matching 30-deploy.sh's
// `... || echo WARNING`), and that the manifests actually applied carry
// real kustomize-resolved dynamic values (image tag, HOST, workload
// identity IDs) rather than the committed overlay's placeholders.

import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import realFs from "node:fs";
import {
  run,
  DEFAULT_REPO_ROOT,
  IDENTITY_RBAC_QUOTA_PVC_MANIFESTS,
  NETWORK_POLICY_MANIFESTS,
  SERVICES_GATEWAY_ROUTE_MANIFESTS,
  SANDBOX_MANIFESTS,
  DEPLOYMENT_MANIFESTS,
  WORKER_MANIFESTS,
} from "../steps/30-deploy.mjs";
import * as execDefault from "../lib/exec.mjs";

const TEST_KEYVAULT_NAME = "test-kv-fixture";

const CFG = {
  RESOURCE_GROUP: "agentweaver-rg",
  CLUSTER_NAME: "agentweaver-aks",
  ACR_NAME: "agentweaverregistry",
  LOCATION: "westus2",
  NAMESPACE: "agentweaver",
  KATA_POOL_NAME: "katapool",
  APP_POOL_NAME: "apppool",
  IMAGE_TAG: "v0.9.71",
  AGENTHOST_IMAGE_TAG: "v0.9.71-agenthost",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  KEYVAULT_NAME: TEST_KEYVAULT_NAME,
  AGENTHOST_KEYVAULT_URI: `https://${TEST_KEYVAULT_NAME}.vault.azure.net/`,
  TENANT_ID: "66666666-7777-8888-9999-000000000000",
  IDENTITY_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000",
  APPINSIGHTS_WORKSPACE_ID: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  OAUTH_SIGNING_CERTIFICATE_NAME: "oauth-signing-custom",
  OAUTH_ENCRYPTION_CERTIFICATE_NAME: "oauth-encryption-custom",
};

function makeFakes({
  hasSandboxCrd = true,
  workerRolloutFails = false,
  ddcExists = true,
  keyvaultFound = true,
  domain = "*.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io",
  podCidrs = "10.244.0.0/16",
} = {}) {
  const calls = [];
  const writtenFiles = new Map();

  // Real fs underneath -- writeOverlay() must actually write the scratch
  // overlay to disk so the real `kubectl kustomize` child process (see
  // execCapture below) can read it back. We wrap (not fake) writeFileSync so
  // assertions can inspect exactly what content was applied, in addition to
  // the real write still happening (run()'s own finally block removes the
  // whole .rendered scratch dir afterwards).
  const fsImpl = {
    ...realFs,
    writeFileSync: (p, content, ...rest) => {
      writtenFiles.set(path.basename(p), content);
      return realFs.writeFileSync(p, content, ...rest);
    },
  };

  const execRun = async (cmd, args) => {
    calls.push({ type: "run", cmd, args });
    if (cmd === "kubectl" && args[0] === "rollout" && args[2] === "deployment/agentweaver-worker" && workerRolloutFails) {
      throw new Error("rollout timed out");
    }
    return { code: 0 };
  };

  const execCapture = async (cmd, args) => {
    calls.push({ type: "capture", cmd, args });
    if (cmd === "kubectl" && args[0] === "kustomize") {
      // The one real, unfaked shell-out: builds the actual overlay written
      // to disk by writeOverlay(), matching what `run()` will apply.
      return execDefault.capture(cmd, args);
    }
    if (cmd === "kubectl" && args[0] === "config") return { stdout: "aks-context", stderr: "", code: 0 };
    if (cmd === "az" && args[0] === "monitor" && args[1] === "app-insights") {
      return { stdout: "", stderr: "", code: 0 }; // insights already provisioned
    }
    if (cmd === "az" && args[0] === "aks" && args[1] === "show") {
      return { stdout: podCidrs, stderr: "", code: 0 };
    }
    if (cmd === "kubectl" && args.includes("jsonpath={.status.domain}")) {
      return { stdout: domain, stderr: "", code: 0 };
    }
    if (cmd === "kubectl" && args[0] === "get" && args[1] === "defaultdomaincertificate") {
      return ddcExists ? { stdout: "", stderr: "", code: 0 } : { stdout: "", stderr: "", code: 1 };
    }
    if (cmd === "kubectl" && args[0] === "api-resources") {
      return { stdout: hasSandboxCrd ? "sandboxtemplates  extensions.agents.x-k8s.io  true  SandboxTemplate" : "", stderr: "", code: 0 };
    }
    if (cmd === "kubectl" && args[0] === "get" && args[1] === "gateway") {
      return { stdout: "10.0.0.5", stderr: "", code: 0 };
    }
    return { stdout: "", stderr: "", code: 0 };
  };

  const log = {
    info: (msg) => calls.push({ type: "info", msg }),
    section: () => {},
    field: () => {},
    ok: () => {},
    skip: (msg) => calls.push({ type: "skip", msg }),
    warn: (msg) => calls.push({ type: "warn", msg }),
    error: () => {},
  };

  const az = {
    getLogAnalyticsWorkspaceCustomerId: async () => "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    keyvaultExists: async () => keyvaultFound,
  };

  return { calls, writtenFiles, execRun, execCapture, log, az, fsImpl };
}

function appliedFilenames(calls) {
  return calls
    .filter((c) => c.type === "run" && c.cmd === "kubectl" && c.args[0] === "apply" && c.args[1] === "-f")
    .map((c) => path.basename(c.args[2]));
}

test("run(): applies manifests in the exact order groups (CRD present)", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ hasSandboxCrd: true });
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const applied = appliedFilenames(calls);
  const expectedOrder = [
    "namespace.yaml",
    "serviceaccount-api.yaml",
    "serviceaccount-worker.yaml",
    "serviceaccount-agenthost.yaml",
    "serviceaccount-mcp.yaml",
    "secret-provider-class.yaml",
    ...IDENTITY_RBAC_QUOTA_PVC_MANIFESTS.slice(5),
    ...NETWORK_POLICY_MANIFESTS,
    ...SERVICES_GATEWAY_ROUTE_MANIFESTS.map((f) => f.replace(/^_/, "")),
    ...SANDBOX_MANIFESTS,
    ...DEPLOYMENT_MANIFESTS,
    ...WORKER_MANIFESTS,
  ];
  assert.deepEqual(applied, expectedOrder);
});

test("run(): skips SandboxTemplate/SandboxWarmPool when the CRD is not installed", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ hasSandboxCrd: false });
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const applied = appliedFilenames(calls);
  for (const fname of SANDBOX_MANIFESTS) {
    assert.ok(!applied.includes(fname), `${fname} should not have been applied`);
  }
  const skipLog = calls.find((c) => c.type === "skip");
  assert.ok(skipLog, "expected a [SKIP] log for the missing CRD");
  assert.match(skipLog.msg, /agent-sandbox CRD not installed/);
});

test("run(): both gateways are waited on for condition=Programmed with a 180s timeout", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const waits = calls.filter((c) => c.type === "run" && c.cmd === "kubectl" && c.args[0] === "wait" && c.args[1] === "--for=condition=Programmed");
  assert.equal(waits.length, 2);
  assert.equal(waits[0].args[2], "gateway/agentweaver-gateway");
  assert.equal(waits[1].args[2], "gateway/agentweaver-preview-gateway");
  for (const w of waits) {
    assert.ok(w.args.includes("--timeout=180s"));
  }
});

test("run(): rollout status waits use api=180s, frontend=120s, mcp=120s, worker=300s", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const rollouts = calls.filter((c) => c.type === "run" && c.cmd === "kubectl" && c.args[0] === "rollout");
  const byDeployment = Object.fromEntries(rollouts.map((c) => [c.args[2], c.args.find((a) => a.startsWith("--timeout="))]));
  assert.equal(byDeployment["deployment/agentweaver-api"], "--timeout=180s");
  assert.equal(byDeployment["deployment/agentweaver-frontend"], "--timeout=120s");
  assert.equal(byDeployment["deployment/agentweaver-mcp"], "--timeout=120s");
  assert.equal(byDeployment["deployment/agentweaver-worker"], "--timeout=300s");
});

test("run(): a Worker rollout timeout is logged as a non-fatal WARNING, not thrown", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ workerRolloutFails: true });
  await assert.doesNotReject(() =>
    run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT }),
  );
  const warning = calls.find((c) => c.type === "warn");
  assert.ok(warning, "expected a WARNING log for the failed worker rollout");
  assert.match(warning.msg, /Worker rollout did not complete within 300s/);
});

test("run(): throws when IDENTITY_CLIENT_ID/KEYVAULT_NAME/TENANT_ID are missing (matches bash's fatal missing-vars check)", async () => {
  const { execRun, execCapture, log, az, fsImpl } = makeFakes();
  const badCfg = { ...CFG, IDENTITY_CLIENT_ID: "", KEYVAULT_NAME: "", TENANT_ID: "" };
  await assert.rejects(
    () => run(badCfg, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT }),
    /IDENTITY_CLIENT_ID[\s\S]*KEYVAULT_NAME[\s\S]*TENANT_ID/,
  );
});

test("run(): throws loudly (before applying any manifest) when KEYVAULT_NAME does not exist in the subscription -- catches typo'd-but-real vault names", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ keyvaultFound: false });
  const typoCfg = { ...CFG, KEYVAULT_NAME: "akwvkv" };
  await assert.rejects(
    () => run(typoCfg, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT }),
    /KEYVAULT_NAME='akwvkv' was not found/,
  );
  const applied = appliedFilenames(calls);
  assert.equal(applied.length, 0, "must not apply any manifest before the Key Vault existence check passes");
});

test("run(): rejects an empty managed domain before rendering or applying manifests", async () => {
  const { calls, writtenFiles, execRun, execCapture, log, az, fsImpl } = makeFakes({ domain: "" });
  await assert.rejects(
    () => run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT }),
    /DefaultDomainCertificate status\.domain must be a DNS hostname/,
  );
  assert.equal(appliedFilenames(calls).length, 0, "must not apply manifests when the managed domain is absent");
  assert.equal(
    calls.filter((c) => c.type === "capture" && c.cmd === "kubectl" && c.args[0] === "kustomize").length,
    0,
    "must not render manifests when the managed domain is absent",
  );
  assert.equal(writtenFiles.size, 0, "must not write rendered manifests when the managed domain is absent");
});

test("run(): refuses deployment when AKS cannot provide a bounded proxy CIDR", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ podCidrs: "" });
  await assert.rejects(
    run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT }),
    /refusing to trust unbounded forwarded headers/,
  );
  assert.equal(
    calls.some((call) => call.type === "run" && call.cmd === "kubectl" && call.args.includes("api-deployment.yaml")),
    false,
  );
});

test("run(): accepts a valid wildcard managed domain and renders its public hostname", async () => {
  const domain = "*.valid-zone.westus2.staging.aksapp.io";
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ domain });
  const result = await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  assert.equal(result.HOST, "agentweaver.valid-zone.westus2.staging.aksapp.io");
  assert.equal(result.ZONE_SUFFIX, "valid-zone.westus2.staging.aksapp.io");
  assert.equal(
    calls.filter((c) => c.type === "capture" && c.cmd === "kubectl" && c.args[0] === "kustomize").length,
    1,
    "a valid managed domain must proceed to manifest rendering",
  );
});

test("run(): prints the exact non-secret Copilot callback registration guidance", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });
  const infoMessages = calls.filter((call) => call.type === "info").map((call) => call.msg);
  const callbackUrl =
    "https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io/auth/github/copilot-app/callback";

  assert.ok(infoMessages.includes(`  Copilot callback to register: ${callbackUrl}`));
  assert.ok(infoMessages.includes("  GitHub App callback matching: exact URL; wildcard matching disabled."));
  const callbackGuidance = infoMessages.filter((message) => /callback/i.test(message ?? ""));
  assert.equal(callbackGuidance.some((message) => /client.?id|secret|token|credential/i.test(message)), false);
});

test("run(): applied manifests carry real kustomize-resolved values, not the committed overlay's placeholders", async () => {
  const { writtenFiles, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const apiDeployment = writtenFiles.get("api-deployment.yaml");
  assert.ok(apiDeployment, "expected api-deployment.yaml to have been written before apply");
  assert.match(apiDeployment, /image: agentweaverregistry\.azurecr\.io\/agentweaver-api:v0\.9\.71/);
  assert.doesNotMatch(apiDeployment, /:latest/);

  const runtimeConfig = writtenFiles.get("agentweaver-runtime-config.yaml");
  assert.ok(runtimeConfig, "expected the synthetic runtime-config ConfigMap to have been written before apply");
  assert.match(
    runtimeConfig,
    /OAUTH_PUBLIC_ORIGIN: https:\/\/agentweaver\.6a6f0602b81a5700010708e7\.eastus2euap\.aksapp\.io/,
  );
  assert.match(runtimeConfig, /OAUTH_TRUSTED_PROXY_NETWORKS.*10\.244\.0\.0\/16/);
  assert.match(runtimeConfig, /OAUTH_SIGNING_CERTIFICATE_NAME/);
  assert.match(runtimeConfig, /oauth-signing-custom/);
  assert.match(runtimeConfig, /oauth-encryption-custom/);
  const checksum = runtimeConfig.match(/OAUTH_RUNTIME_CONFIG_CHECKSUM:\s*([a-f0-9]{64})/)?.[1];
  assert.ok(checksum, "runtime ConfigMap must carry the canonical OAuth configuration checksum");
  assert.match(apiDeployment, new RegExp(`agentweaver\\.io/oauth-runtime-config-checksum: ${checksum}`));
  const mcpDeployment = writtenFiles.get("mcp-deployment.yaml");
  assert.ok(mcpDeployment, "expected mcp-deployment.yaml to have been written before apply");
  assert.match(mcpDeployment, new RegExp(`agentweaver\\.io/oauth-runtime-config-checksum: ${checksum}`));
  assert.doesNotMatch(runtimeConfig, /agentweaver\.example\.com|placeholder/);
  assert.doesNotMatch(runtimeConfig, /mcp-oauth-signing-key|Auth__OAuth__(?:SigningKey|Issuer|Audience)|OAUTH_ISSUER|OAUTH_AUDIENCE/);

  const secretProviderClass = writtenFiles.get("secret-provider-class.yaml");
  assert.ok(secretProviderClass);
  assert.match(secretProviderClass, /clientID: 11111111-2222-3333-4444-555555555555/);
  assert.doesNotMatch(secretProviderClass, /changeme/);
});

// --- Postgres access-mode branching, end-to-end through a real kustomize build ---
// Regression for the live v0.16.0 bug: with --postgres-access-mode public the
// applied Postgres egress policies still carried the private delegated-subnet
// ipBlock, so pods could never reach the public Flexible Server.

test("run(): private mode (default) applies the ipBlock Postgres egress NetworkPolicies unchanged", async () => {
  const { writtenFiles, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run({ ...CFG, PG_ACCESS_MODE: "private", PG_SERVER_NAME: "agentweaver-pg" }, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const apiPolicy = writtenFiles.get("networkpolicy-postgres-egress.yaml");
  assert.match(apiPolicy, /name: allow-api-postgres-egress\b/);
  assert.match(apiPolicy, /cidr: 10\.225\.0\.0\/28/);
  assert.doesNotMatch(apiPolicy, /CiliumNetworkPolicy/);

  const workerPolicies = writtenFiles.get("networkpolicy-worker.yaml");
  assert.match(workerPolicies, /name: allow-worker-postgres-egress\b/);
  assert.match(workerPolicies, /cidr: 10\.225\.0\.0\/28/);
  assert.doesNotMatch(workerPolicies, /CiliumNetworkPolicy/);
});

test("run(): public mode applies toFQDNs CiliumNetworkPolicies instead of the private-CIDR ipBlock rules", async () => {
  const { writtenFiles, execRun, execCapture, log, az, fsImpl } = makeFakes();
  await run(
    { ...CFG, PG_ACCESS_MODE: "public", PG_SERVER_NAME: "agentweaver-pg-eastus2" },
    { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT },
  );

  const apiPolicy = writtenFiles.get("networkpolicy-postgres-egress.yaml");
  assert.doesNotMatch(apiPolicy, /cidr: 10\.225\.0\.0\/28/);
  assert.match(apiPolicy, /kind: CiliumNetworkPolicy/);
  assert.match(apiPolicy, /name: allow-api-postgres-egress-fqdn/);
  assert.match(apiPolicy, /matchName: "agentweaver-pg-eastus2\.postgres\.database\.azure\.com"/);
  assert.match(apiPolicy, /- port: "5432"/);

  const workerPolicies = writtenFiles.get("networkpolicy-worker.yaml");
  assert.doesNotMatch(workerPolicies, /cidr: 10\.225\.0\.0\/28/);
  assert.match(workerPolicies, /name: allow-worker-postgres-egress-fqdn/);
  assert.match(workerPolicies, /matchName: "agentweaver-pg-eastus2\.postgres\.database\.azure\.com"/);
  // Non-Postgres worker policies are unaffected.
  assert.match(workerPolicies, /name: allow-worker-otel-egress\b/);
  assert.match(workerPolicies, /name: allow-worker-dns-egress\b/);
});
