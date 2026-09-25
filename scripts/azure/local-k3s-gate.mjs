#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import { parseManifestDocuments, assertSharedAiExecutionSigningSecret } from "./lib/ai-execution-signing-contract.mjs";
import { buildManifests } from "./steps/30-deploy.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const DEFAULT_REPO_ROOT = path.resolve(__dirname, "..", "..");

function localBearer() {
  return process.env.AGENTWEAVER_LOCAL_TEST_BEARER || "agentweaver-local-k3s-test-bearer";
}

function isLoopbackTarget(target) {
  let url;
  try {
    url = new URL(target);
  } catch {
    return false;
  }
  const host = url.hostname.toLowerCase();
  return host === "localhost"
    || host.endsWith(".localhost")
    || host === "::1"
    || /^127(?:\.\d{1,3}){0,3}$/.test(host);
}

export const HELP_TEXT = `Agentweaver local k3s release gate

Usage:
  node scripts/azure/cli.mjs local-k3s-gate [options]

Options:
  --target <url>                       Existing loopback target to smoke instead of deploying.
  --image-tag <tag>                    Candidate image tag to deploy/build (default: current short SHA).
  --skip-build                         Do not build local container images before deploy.
  --skip-smoke                         Deploy and validate manifests only.
  --negative-omit-worker-signing-key   Prove the gate fails when the worker omits AiExecution__ProviderKeySigningKey.
  --timeout <seconds>                  Readiness/smoke timeout (default: 900).

The gate targets Ubuntu-24.04 WSL k3s. If k3s is absent it prints the reproducible
install command and stops before touching any AKS context.
`;

export function parseArgs(argv = []) {
  const args = {
    target: null,
    imageTag: null,
    skipBuild: false,
    skipSmoke: false,
    negativeOmitWorkerSigningKey: false,
    timeoutSeconds: 900,
    help: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const readValue = (name) => {
      const inline = arg.startsWith(`${name}=`);
      const value = inline ? arg.slice(name.length + 1) : argv[++i];
      if (!value) throw new Error(`${name} requires a value`);
      return value;
    };
    if (arg === "-h" || arg === "--help") args.help = true;
    else if (arg === "--target" || arg.startsWith("--target=")) args.target = readValue("--target");
    else if (arg === "--image-tag" || arg.startsWith("--image-tag=")) args.imageTag = readValue("--image-tag");
    else if (arg === "--skip-build") args.skipBuild = true;
    else if (arg === "--skip-smoke") args.skipSmoke = true;
    else if (arg === "--negative-omit-worker-signing-key") args.negativeOmitWorkerSigningKey = true;
    else if (arg === "--timeout" || arg.startsWith("--timeout=")) args.timeoutSeconds = Number(readValue("--timeout"));
    else throw new Error(`Unknown option: ${arg}`);
  }

  if (!Number.isFinite(args.timeoutSeconds) || args.timeoutSeconds <= 0) {
    throw new Error("--timeout must be a positive number of seconds");
  }
  if (args.target && !isLoopbackTarget(args.target)) {
    throw new Error("--target must be a loopback URL for the local k3s gate.");
  }
  return args;
}

async function shortSha(exec, repoRoot) {
  const { stdout } = await exec.capture("git", ["rev-parse", "--short=12", "HEAD"], { cwd: repoRoot });
  return stdout.trim();
}

export async function probeWslK3s({ exec = execDefault } = {}) {
  const distro = "Ubuntu-24.04";
  const script = [
    "set -eu",
    "command -v k3s >/dev/null 2>&1 && echo k3s_binary=present || echo k3s_binary=absent",
    "pgrep -a k3s >/dev/null 2>&1 && echo k3s_process=present || echo k3s_process=absent",
    "test -f /etc/rancher/k3s/k3s.yaml && echo k3s_kubeconfig=present || echo k3s_kubeconfig=absent",
    "kubectl config current-context 2>/dev/null | sed 's/^/current_context=/' || true",
    "KUBECONFIG=/etc/rancher/k3s/k3s.yaml kubectl config current-context 2>/dev/null | sed 's/^/k3s_context=/' || true",
  ].join("; ");
  const result = await exec.capture("wsl", ["-d", distro, "--", "sh", "-lc", script], { allowFailure: true });
  return {
    distro,
    available: result.code === 0
      && /k3s_binary=present/.test(result.stdout)
      && /k3s_process=present/.test(result.stdout)
      && /k3s_kubeconfig=present/.test(result.stdout),
    stdout: result.stdout,
    stderr: result.stderr,
  };
}

export function k3sInstallGuidance(distro = "Ubuntu-24.04") {
  return [
    `Local k3s is not installed or not initialized in WSL distro ${distro}.`,
    "Provision it reproducibly with:",
    `  wsl -d ${distro} -- sh -lc "curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC='--write-kubeconfig-mode 644 --disable traefik' sh -"`,
    "Then rerun:",
    "  npm run release:local-k3s-gate",
  ].join("\n");
}

export async function ensureWslK3s({ exec = execDefault, log = logDefault } = {}) {
  let probe = await probeWslK3s({ exec });
  if (probe.available) return probe;

  log.warn(k3sInstallGuidance(probe.distro));
  if (/k3s_binary=present/.test(probe.stdout) && /k3s_kubeconfig=present/.test(probe.stdout)) {
    log.info("k3s is installed but not running; attempting non-interactive service start in WSL...");
    await exec.capture("wsl", [
      "-d", probe.distro, "--", "sh", "-lc",
      "sudo -n systemctl start k3s 2>/dev/null || sudo -n service k3s start 2>/dev/null || true",
    ], { allowFailure: true, timeoutMs: 60_000 });
    probe = await probeWslK3s({ exec });
    if (probe.available) return probe;
  }
  log.info("Attempting non-interactive k3s installation in WSL...");
  const installScript = [
    "set -eu",
    "if command -v k3s >/dev/null 2>&1 && test -f /etc/rancher/k3s/k3s.yaml; then exit 0; fi",
    "install='curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC=\"--write-kubeconfig-mode 644 --disable traefik\" sh -'",
    "if [ \"$(id -u)\" = 0 ]; then sh -lc \"$install\"; elif command -v sudo >/dev/null 2>&1; then sh -lc \"curl -sfL https://get.k3s.io | sudo -n INSTALL_K3S_EXEC=\\\"--write-kubeconfig-mode 644 --disable traefik\\\" sh -\"; else sh -lc \"$install\"; fi",
  ].join("; ");
  const install = await exec.capture("wsl", ["-d", probe.distro, "--", "sh", "-lc", installScript], {
    allowFailure: true,
    timeoutMs: 10 * 60_000,
  });
  if (install.code !== 0) {
    throw new Error(
      `Unable to provision local k3s automatically in ${probe.distro}.\n`
      + `${install.stderr || install.stdout}\n\n${k3sInstallGuidance(probe.distro)}`,
    );
  }

  probe = await probeWslK3s({ exec });
  if (!probe.available) {
    throw new Error(`k3s installation completed but kubeconfig is still unavailable.\n\n${probe.stdout}`);
  }
  return probe;
}

function removeSigningKeyFromWorkerDoc(doc) {
  return doc.text.replace(
    /(\n\s*- name: AiExecution__ProviderKeySigningKey\s*\n\s*valueFrom:\s*\n\s*secretKeyRef:\s*\n\s*key: ai-execution-provider-key-signing-key\s*\n\s*name: agentweaver-secrets)/,
    "",
  );
}

export async function renderAndValidateCandidateManifests({
  repoRoot = DEFAULT_REPO_ROOT,
  imageTag,
  negativeOmitWorkerSigningKey = false,
  exec = execDefault,
} = {}) {
  const scratchDir = path.join(repoRoot, "scripts", "azure", ".local-k3s-rendered");
  const tag = imageTag ?? await shortSha(exec, repoRoot);
  const vars = {
    IMAGE_TAG: tag,
    AGENTHOST_IMAGE_TAG: tag,
    ACR_LOGIN_SERVER: "local.registry.invalid",
    HOST: "agentweaver.localtest.invalid",
    PREVIEW_HOSTNAME: "preview.agentweaver.localtest.invalid",
    PREVIEW_TLS_SECRET: "agentweaver-preview-tls",
    SANDBOX_PREVIEW_ENABLED: "false",
    SANDBOX_PREVIEW_ZONE_SUFFIX: "preview.agentweaver.localtest.invalid",
    TENANT_ID: "local-test-tenant",
    AUTH_MODE: "LocalTest",
    ENTRA_CLIENT_ID: "00000000-0000-0000-0000-000000000001",
    ENTRA_TENANT_ID: "00000000-0000-0000-0000-000000000002",
    ENTRA_ENTERPRISE_APP_OBJECT_ID: "00000000-0000-0000-0000-000000000003",
    IDENTITY_CLIENT_ID: "local-test-identity",
    APPINSIGHTS_WORKSPACE_ID: "local-test-workspace",
    KEYVAULT_NAME: "local-test-keyvault",
    AGENTHOST_KEYVAULT_URI: "https://local-test-keyvault.vault.azure.net/",
    NAMESPACE: "agentweaver",
    PG_ACCESS_MODE: "private",
    PG_SERVER_NAME: "agentweaver-local",
  };
  const docs = await buildManifests(vars, {
    repoRoot,
    scratchDir,
    capture: exec.capture,
    fs,
  });
  let yamlText = docs.map((doc) =>
    negativeOmitWorkerSigningKey
      && doc.kind === "Deployment"
      && doc.name === "agentweaver-worker"
      ? removeSigningKeyFromWorkerDoc(doc)
      : doc.text).join("\n---\n");
  const parsed = parseManifestDocuments(yamlText);
  const signing = assertSharedAiExecutionSigningSecret(parsed);
  return { yamlText, docs: parsed, signing, imageTag: tag };
}

function upsertEnv(env, entry) {
  const index = env.findIndex((item) => item.name === entry.name);
  if (index >= 0) env[index] = entry;
  else env.push(entry);
}

function removeEnv(env, name) {
  const index = env.findIndex((item) => item.name === name);
  if (index >= 0) env.splice(index, 1);
}

function upsertVolumeMount(mounts, entry) {
  const index = mounts.findIndex((item) => item.name === entry.name);
  if (index >= 0) mounts[index] = entry;
  else mounts.push(entry);
}

function localizeDeployment(deployment, { role, imageTag }) {
  const copy = structuredClone(deployment);
  copy.metadata.namespace = "agentweaver";
  copy.metadata.annotations = {};
  copy.spec.replicas = 1;
  copy.spec.strategy = { type: "Recreate" };
  const pod = copy.spec.template.spec;
  pod.serviceAccountName = "default";
  pod.initContainers = (pod.initContainers ?? []).map((container) => ({
    ...container,
    image: `agentweaver-api:${imageTag}`,
    imagePullPolicy: "IfNotPresent",
  }));
  for (const container of pod.containers ?? []) {
    container.image = `agentweaver-api:${imageTag}`;
    container.imagePullPolicy = "IfNotPresent";
    const env = container.env ??= [];
    upsertEnv(env, { name: "ASPNETCORE_ENVIRONMENT", value: role === "api" ? "Development" : "Production" });
    upsertEnv(env, { name: "Auth__Mode", value: role === "api" ? "LocalTest" : "Entra" });
    if (role === "api") {
      upsertEnv(env, { name: "Testing__BypassGitHubTokenAuth", value: "true" });
      upsertEnv(env, { name: "Testing__BypassGitHubOrgAuthorization", value: "false" });
      upsertEnv(env, { name: "Auth__Keys__0__Token", value: localBearer() });
      upsertEnv(env, { name: "Auth__Keys__0__User", value: "local-k3s-gate" });
      upsertEnv(env, { name: "Auth__Keys__0__PlatformRoles", value: "PlatformAdmin" });
    }
    upsertEnv(env, { name: "Auth__ApiKey", valueFrom: { secretKeyRef: { name: "agentweaver-secrets", key: "mcp-api-key" } } });
    upsertEnv(env, { name: "Auth__FileSecretStore__Path", value: "/var/agentweaver/local-secrets" });
    upsertEnv(env, { name: "Auth__FileSecretStore__AllowOutsideDevelopment", value: role === "worker" ? "true" : "false" });
    upsertEnv(env, { name: "AiExecution__ProviderKeySigningKey", valueFrom: { secretKeyRef: { name: "agentweaver-secrets", key: "ai-execution-provider-key-signing-key" } } });
    removeEnv(env, "Auth__KeyVault__Uri");
    removeEnv(env, "DataProtection__KeyVault__VaultUri");
    removeEnv(env, "APPLICATIONINSIGHTS_CONNECTION_STRING");
    removeEnv(env, "APPLICATIONINSIGHTS_WORKSPACE_ID");
    const mounts = container.volumeMounts ??= [];
    upsertVolumeMount(mounts, { name: "local-secret-store", mountPath: "/var/agentweaver/local-secrets" });
  }
  pod.volumes = [
    { name: "workspace", emptyDir: {} },
    { name: "tmp", emptyDir: {} },
    { name: "logs", emptyDir: {} },
    { name: "local-secret-store", hostPath: { path: "/var/lib/rancher/k3s/agentweaver-local-secrets", type: "DirectoryOrCreate" } },
    { name: "secrets-store", secret: { secretName: "agentweaver-secrets" } },
    { name: "a2a-client-tls", secret: { secretName: "agentweaver-a2a-client-tls", defaultMode: 0o400 } },
  ];
  return copy;
}

export function buildLocalK3sManifest(docs, { imageTag }) {
  const deployment = (name) => {
    const doc = docs.find((item) => item.kind === "Deployment" && item.metadata?.name === name);
    if (!doc) throw new Error(`Rendered manifests did not include Deployment/${name}`);
    return doc;
  };
  const api = localizeDeployment(deployment("agentweaver-api"), { role: "api", imageTag });
  const worker = localizeDeployment(deployment("agentweaver-worker"), { role: "worker", imageTag });
  const resources = [
    { apiVersion: "v1", kind: "Namespace", metadata: { name: "agentweaver" } },
    {
      apiVersion: "v1",
      kind: "Secret",
      metadata: { name: "agentweaver-secrets", namespace: "agentweaver" },
      stringData: {
        "mcp-api-key": "local-k3s-api-key",
        "ai-execution-provider-key-signing-key": "local-k3s-independent-ai-execution-signing-key",
        "copilot-app-client-id": "local",
        "copilot-app-client-secret": "local",
        "repo-app-client-id": "local",
        "repo-app-client-secret": "local",
        "repo-app-id": "1",
      },
    },
    {
      apiVersion: "v1",
      kind: "Secret",
      metadata: { name: "agentweaver-a2a-client-tls", namespace: "agentweaver" },
      stringData: { "tls.crt": "local", "tls.key": "local", "ca.crt": "local" },
    },
    {
      apiVersion: "v1",
      kind: "Secret",
      metadata: { name: "agentweaver-postgres", namespace: "agentweaver" },
      stringData: {
        connectionstring: "Host=agentweaver-postgres;Port=5432;Database=agentweaver;Username=agentweaver;Password=agentweaver",
      },
    },
    {
      apiVersion: "apps/v1",
      kind: "Deployment",
      metadata: { name: "agentweaver-postgres", namespace: "agentweaver" },
      spec: {
        replicas: 1,
        selector: { matchLabels: { app: "agentweaver-postgres" } },
        template: {
          metadata: { labels: { app: "agentweaver-postgres" } },
          spec: {
            containers: [{
              name: "postgres",
              image: "postgres:16-alpine",
              ports: [{ name: "postgres", containerPort: 5432 }],
              env: [
                { name: "POSTGRES_DB", value: "agentweaver" },
                { name: "POSTGRES_USER", value: "agentweaver" },
                { name: "POSTGRES_PASSWORD", value: "agentweaver" },
              ],
            }],
          },
        },
      },
    },
    {
      apiVersion: "v1",
      kind: "Service",
      metadata: { name: "agentweaver-postgres", namespace: "agentweaver" },
      spec: { selector: { app: "agentweaver-postgres" }, ports: [{ name: "postgres", port: 5432, targetPort: 5432 }] },
    },
    {
      apiVersion: "v1",
      kind: "Service",
      metadata: { name: "agentweaver-api", namespace: "agentweaver" },
      spec: { selector: { app: "agentweaver-api" }, ports: [{ name: "http", port: 8080, targetPort: 8080 }] },
    },
    api,
    worker,
  ];
  return JSON.stringify({ apiVersion: "v1", kind: "List", items: resources }, null, 2);
}

async function wsl(exec, distro, script, opts = {}) {
  return exec.capture("wsl", ["-d", distro, "--", "sh", "-lc", script], opts);
}

async function buildAndLoadApiImage({ exec, repoRoot, imageTag, distro, skipBuild }) {
  if (skipBuild) return;
  await exec.run("docker", ["build", "-f", "apps/Agentweaver.Api/Dockerfile", "-t", `agentweaver-api:${imageTag}`, "."], {
    cwd: repoRoot,
    timeoutMs: 30 * 60_000,
  });
  const tarPath = path.join(repoRoot, "scripts", "azure", `.local-k3s-agentweaver-api-${imageTag}.tar`);
  try {
    await exec.run("docker", ["save", `agentweaver-api:${imageTag}`, "-o", tarPath], { cwd: repoRoot, timeoutMs: 10 * 60_000 });
    const wslPath = tarPath.replace(/^([A-Za-z]):\\/, (_, drive) => `/mnt/${drive.toLowerCase()}/`).replaceAll("\\", "/");
    await wsl(exec, distro, `sudo -n k3s ctr images import ${JSON.stringify(wslPath)}`, { timeoutMs: 10 * 60_000 });
  } finally {
    fs.rmSync(tarPath, { force: true });
  }
}

async function applyAndWaitLocalK3s({ exec, distro, manifestYaml, timeoutSeconds }) {
  const kubectl = "KUBECONFIG=/etc/rancher/k3s/k3s.yaml kubectl";
  await wsl(exec, distro, `${kubectl} apply -f -`, { input: manifestYaml, timeoutMs: 5 * 60_000 });
  for (const name of ["agentweaver-postgres", "agentweaver-api", "agentweaver-worker"]) {
    await wsl(
      exec,
      distro,
      `${kubectl} -n agentweaver rollout status deployment/${name} --timeout=${Math.floor(timeoutSeconds)}s`,
      { timeoutMs: timeoutSeconds * 1000 },
    );
  }
}

function startPortForward({ distro }) {
  const child = spawn("wsl", [
    "-d", distro, "--", "sh", "-lc",
    "KUBECONFIG=/etc/rancher/k3s/k3s.yaml kubectl -n agentweaver port-forward svc/agentweaver-api 18080:8080",
  ], { stdio: "ignore", windowsHide: true });
  return {
    target: "http://127.0.0.1:18080",
    stop() {
      if (!child.killed) child.kill();
    },
  };
}

async function waitForHttpReady(target, { timeoutSeconds }) {
  const deadline = Date.now() + Math.min(timeoutSeconds * 1000, 60_000);
  let lastError = null;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(new URL("/api/version", target), { redirect: "error" });
      if (response.ok) return;
      lastError = new Error(`status ${response.status}`);
    } catch (error) {
      lastError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`Timed out waiting for local API port-forward at ${target}: ${lastError?.message ?? "not ready"}`);
}

async function runSmoke(target, { repoRoot, timeoutSeconds, exec }) {
  const out = path.join(
    repoRoot,
    "scripts",
    "api-harness",
    "verdicts",
    `local-k3s-generated-artifacts-seam-${Date.now()}.json`,
  );
  await exec.run(process.execPath, [
    "scripts/api-harness/run-persona.mjs",
    "--scenario", "generated-artifacts-seam",
    "--target", target,
    "--auth-provider", "local-test",
    "--timeout", String(timeoutSeconds),
    "--out", out,
  ], {
    cwd: repoRoot,
    timeoutMs: timeoutSeconds * 1000,
    env: { AGENTWEAVER_LOCAL_TEST_BEARER: localBearer() },
  });
  return out;
}

export async function run(input = [], opts = {}) {
  const argv = Array.isArray(input) ? input : input.argv ?? [];
  const {
    exec = execDefault,
    log = input.log ?? opts.log ?? logDefault,
    repoRoot = DEFAULT_REPO_ROOT,
  } = opts;
  const args = parseArgs(argv);
  if (args.help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  const rendered = await renderAndValidateCandidateManifests({
    repoRoot,
    imageTag: args.imageTag,
    negativeOmitWorkerSigningKey: args.negativeOmitWorkerSigningKey,
    exec,
  });
  log.ok(
    `AI execution signing-key contract: ${rendered.signing.deployments.join(", ")} share `
    + `${rendered.signing.secretName}/${rendered.signing.secretKey}`,
  );

  if (args.negativeOmitWorkerSigningKey) {
    throw new Error("Negative proof unexpectedly reached deploy stage; manifest contract should have failed first.");
  }

  let portForward = null;
  let target = args.target;
  if (!target) {
    const probe = await ensureWslK3s({ exec, log });
    log.section("Local WSL k3s probe");
    log.info(probe.stdout.trim() || "(no probe output)");
    const localManifest = buildLocalK3sManifest(rendered.docs, { imageTag: rendered.imageTag });
    await buildAndLoadApiImage({
      exec,
      repoRoot,
      imageTag: rendered.imageTag,
      distro: probe.distro,
      skipBuild: args.skipBuild,
    });
    await applyAndWaitLocalK3s({
      exec,
      distro: probe.distro,
      manifestYaml: localManifest,
      timeoutSeconds: args.timeoutSeconds,
    });
    portForward = startPortForward({ distro: probe.distro });
    target = portForward.target;
    await waitForHttpReady(target, { timeoutSeconds: args.timeoutSeconds });
  }

  if (args.skipSmoke) return { ok: true, smoke: "skipped", imageTag: rendered.imageTag };

  try {
    const verdict = await runSmoke(target, {
      repoRoot,
      timeoutSeconds: args.timeoutSeconds,
      exec,
    });
    log.ok(`Local k3s API smoke completed: ${verdict}`);
    return { ok: true, verdict, imageTag: rendered.imageTag };
  } finally {
    portForward?.stop();
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  run(process.argv.slice(2)).catch((error) => {
    logDefault.error(error?.message ?? String(error));
    process.exitCode = 1;
  });
}
