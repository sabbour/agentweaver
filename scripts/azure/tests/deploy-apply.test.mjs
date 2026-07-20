// deploy-apply.test.mjs -- Orchestration/ordering tests for steps/30-deploy.mjs's
// run(), using fully injected fakes (no real kubectl/az/filesystem side effects).
// Verifies: full apply ordering, the SandboxTemplate CRD conditional, the two
// gateway Programmed waits, and that a Worker rollout timeout is logged as a
// non-fatal WARNING (matching 30-deploy.sh's `... || echo WARNING`).

import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
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
  KEYVAULT_NAME: "agentweaver-kv",
  AGENTHOST_KEYVAULT_URI: "https://agentweaver-kv.vault.azure.net/",
  TENANT_ID: "66666666-7777-8888-9999-000000000000",
  IDENTITY_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  APPINSIGHTS_WORKSPACE_ID: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
};

function makeFakes({ hasSandboxCrd = true, workerRolloutFails = false, ddcExists = true } = {}) {
  const calls = [];
  const writtenFiles = new Map();
  const fsImpl = {
    mkdirSync: () => {},
    rmSync: () => {},
    writeFileSync: (p, content) => writtenFiles.set(path.basename(p), content),
    readdirSync: (dir) => {
      // Delegate to real fs for reading the actual k8s dir listing/content --
      // only writes are faked, so renderManifests() still renders the real templates.
      return realFs.readdirSync(dir);
    },
    readFileSync: (p, enc) => realFs.readFileSync(p, enc),
  };

  const execRun = async (cmd, args) => {
    calls.push({ type: "run", cmd, args });
    if (cmd === "kubectl" && args[0] === "rollout" && args[2] === "deployment/agentweaver-worker" && workerRolloutFails) {
      const err = new Error("rollout timed out");
      throw err;
    }
    return { code: 0 };
  };

  const execCapture = async (cmd, args) => {
    calls.push({ type: "capture", cmd, args });
    if (cmd === "kubectl" && args[0] === "config") return { stdout: "aks-context", stderr: "", code: 0 };
    if (cmd === "az" && args[0] === "monitor" && args[1] === "app-insights") {
      return { stdout: "", stderr: "", code: 0 }; // insights already provisioned
    }
    if (cmd === "kubectl" && args[0] === "get" && args[1] === "defaultdomaincertificate") {
      return ddcExists ? { stdout: "", stderr: "", code: 0 } : { stdout: "", stderr: "", code: 1 };
    }
    if (cmd === "kubectl" && args.includes("jsonpath={.status.domain}")) {
      return { stdout: "*.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io", stderr: "", code: 0 };
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
    info: () => {},
    section: () => {},
    field: () => {},
    ok: () => {},
    skip: (msg) => calls.push({ type: "skip", msg }),
    warn: (msg) => calls.push({ type: "warn", msg }),
    error: () => {},
  };

  const az = {
    getLogAnalyticsWorkspaceCustomerId: async () => "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  };

  return { calls, execRun, execCapture, log, az, fsImpl };
}

// Real fs, aliased so the fake fsImpl above can still read the actual k8s
// templates (only writes are faked -- we don't want tests touching disk).
import realFs from "node:fs";

function appliedFilenames(calls) {
  return calls
    .filter((c) => c.type === "run" && c.cmd === "kubectl" && c.args[0] === "apply" && c.args[1] === "-f")
    .map((c) => path.basename(c.args[2]));
}

test("run(): applies manifests in the exact order groups from 30-deploy.sh (CRD present)", async () => {
  const { calls, execRun, execCapture, log, az, fsImpl } = makeFakes({ hasSandboxCrd: true });
  await run(CFG, { run: execRun, capture: execCapture, log, az, fs: fsImpl, repoRoot: DEFAULT_REPO_ROOT });

  const applied = appliedFilenames(calls);
  const expectedOrder = [
    "namespace.yaml",
    "serviceaccount-api.yaml",
    "serviceaccount-agenthost.yaml",
    "secret-provider-class.yaml",
    ...IDENTITY_RBAC_QUOTA_PVC_MANIFESTS.slice(3),
    ...NETWORK_POLICY_MANIFESTS,
    ...SERVICES_GATEWAY_ROUTE_MANIFESTS,
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
