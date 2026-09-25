import test from "node:test";
import assert from "node:assert/strict";

import {
  parseManifestDocuments,
  assertSharedAiExecutionSigningSecret,
} from "../lib/ai-execution-signing-contract.mjs";
import {
  parseArgs,
  probeWslK3s,
  ensureWslK3s,
  buildLocalK3sManifest,
  k3sInstallGuidance,
} from "../local-k3s-gate.mjs";

const GOOD_MANIFEST = `apiVersion: apps/v1
kind: Deployment
metadata:
  name: agentweaver-api
spec:
  template:
    spec:
      containers:
        - name: api
          env:
            - name: AiExecution__ProviderKeySigningKey
              valueFrom:
                secretKeyRef:
                  name: agentweaver-secrets
                  key: ai-execution-provider-key-signing-key
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: agentweaver-worker
spec:
  template:
    spec:
      containers:
        - name: worker
          env:
            - name: AiExecution__ProviderKeySigningKey
              valueFrom:
                secretKeyRef:
                  name: agentweaver-secrets
                  key: ai-execution-provider-key-signing-key
`;

test("AI execution signing contract accepts matching API and worker secret refs", () => {
  const result = assertSharedAiExecutionSigningSecret(parseManifestDocuments(GOOD_MANIFEST));
  assert.deepEqual(result, {
    secretName: "agentweaver-secrets",
    secretKey: "ai-execution-provider-key-signing-key",
    deployments: ["agentweaver-api", "agentweaver-worker"],
  });
});

test("AI execution signing contract fails closed when worker ref is omitted", () => {
  const broken = GOOD_MANIFEST.replace(/            - name: AiExecution__ProviderKeySigningKey[\s\S]*?key: ai-execution-provider-key-signing-key\n$/, "");
  assert.throws(
    () => assertSharedAiExecutionSigningSecret(parseManifestDocuments(broken)),
    /agentweaver-worker is missing AiExecution__ProviderKeySigningKey/,
  );
});

test("AI execution signing contract rejects mismatched refs", () => {
  const broken = GOOD_MANIFEST.replace("key: ai-execution-provider-key-signing-key", "key: other-key");
  assert.throws(
    () => assertSharedAiExecutionSigningSecret(parseManifestDocuments(broken)),
    /expected agentweaver-secrets\/other-key/,
  );
});

test("local k3s gate parses negative proof and target options", () => {
  assert.deepEqual(
    parseArgs(["--target", "http://127.0.0.1:18080", "--negative-omit-worker-signing-key", "--skip-smoke", "--timeout", "12"]),
    {
      target: "http://127.0.0.1:18080",
      imageTag: null,
      skipBuild: false,
      skipSmoke: true,
      negativeOmitWorkerSigningKey: true,
      timeoutSeconds: 12,
      help: false,
    },
  );
});

test("local k3s gate rejects non-loopback --target values", () => {
  assert.throws(
    () => parseArgs(["--target", "https://agentweaver.example.aksapp.io"]),
    /--target must be a loopback URL/,
  );
  assert.equal(parseArgs(["--target", "http://localhost:18080"]).target, "http://localhost:18080");
});

test("probeWslK3s reports unavailable when binary or kubeconfig is absent", async () => {
  const exec = {
    capture: async () => ({
      code: 0,
      stdout: "k3s_binary=absent\nk3s_process=absent\nk3s_kubeconfig=absent\ncurrent_context=agentweaver-aks-2\n",
      stderr: "",
    }),
  };
  const probe = await probeWslK3s({ exec });
  assert.equal(probe.available, false);
  assert.match(k3sInstallGuidance(probe.distro), /curl -sfL https:\/\/get\.k3s\.io/);
});

test("ensureWslK3s attempts reproducible install when probe is absent", async () => {
  const calls = [];
  const exec = {
    capture: async (cmd, args) => {
      calls.push([cmd, args]);
      if (calls.length === 1) {
        return { code: 0, stdout: "k3s_binary=absent\nk3s_kubeconfig=absent\n", stderr: "" };
      }
      if (calls.length === 2) {
        assert.match(args.at(-1), /https:\/\/get\.k3s\.io/);
        return { code: 0, stdout: "", stderr: "" };
      }
      return { code: 0, stdout: "k3s_binary=present\nk3s_process=present\nk3s_kubeconfig=present\n", stderr: "" };
    },
  };
  const log = { warn() {}, info() {} };
  const probe = await ensureWslK3s({ exec, log });
  assert.equal(probe.available, true);
  assert.equal(calls.length, 3);
});

test("local k3s manifest keeps API LocalTest development auth separate from Production worker", () => {
  const docs = parseManifestDocuments(GOOD_MANIFEST);
  const yaml = buildLocalK3sManifest(docs, { imageTag: "testtag" });
  const localDocs = parseManifestDocuments(yaml);
  const deployment = (name) => localDocs.find((doc) => doc.kind === "Deployment" && doc.metadata.name === name);
  const env = (name) => Object.fromEntries(
    deployment(name).spec.template.spec.containers[0].env.map((entry) => [entry.name, entry.value ?? entry.valueFrom]),
  );
  assert.equal(env("agentweaver-api").ASPNETCORE_ENVIRONMENT, "Development");
  assert.equal(env("agentweaver-api").Auth__Mode, "LocalTest");
  assert.equal(env("agentweaver-api").Testing__BypassGitHubTokenAuth, "true");
  assert.equal(env("agentweaver-worker").ASPNETCORE_ENVIRONMENT, "Production");
  assert.equal(env("agentweaver-worker").Auth__Mode, "Entra");
  assert.equal(env("agentweaver-api").Auth__FileSecretStore__Path, "/var/agentweaver/local-secrets");
  assert.equal(env("agentweaver-worker").Auth__FileSecretStore__Path, "/var/agentweaver/local-secrets");
  assert.equal(env("agentweaver-worker").Auth__FileSecretStore__AllowOutsideDevelopment, "true");
  assert.equal(env("agentweaver-worker").Testing__BypassGitHubTokenAuth, undefined);
  const workerVolumes = deployment("agentweaver-worker").spec.template.spec.volumes;
  assert.equal(
    workerVolumes.find((volume) => volume.name === "local-secret-store")?.hostPath?.path,
    "/var/lib/rancher/k3s/agentweaver-local-secrets",
  );
});
