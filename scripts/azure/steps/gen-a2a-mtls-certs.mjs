// gen-a2a-mtls-certs.mjs -- Faithful Node port of
// scripts/aks/gen-a2a-mtls-certs.sh (cross-checked against
// gen-a2a-mtls-certs.ps1). Read both before changing this file; they must
// stay in lockstep with this port's behavior.
//
// Generates workload-bound mTLS certificates for the A2A transport
// (spec-018 H1): an internal self-signed CA, an AgentHost server cert, and a
// worker client cert, applied as three K8s Secrets (agentweaver-a2a-ca,
// agentweaver-a2a-server-tls, agentweaver-a2a-client-tls). Idempotent unless
// `force: true` is passed.
//
// INTEGRATION NOTE (P2->P4 handoff): steps/30-deploy.mjs calls this module's
// run() directly (no more shelling out via run-os-script.mjs to the legacy
// bash/PowerShell version) -- see that file's "Ensuring A2A mTLS
// certificates are present" step.
//
// cfg is the resolved variables.mjs output: NAMESPACE. Optional:
// AGENTWEAVER_TMP_DIR (scratch directory), repoRoot.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import { withRetry } from "../lib/retry.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

const SECRET_NAMES = Object.freeze(["agentweaver-a2a-ca", "agentweaver-a2a-server-tls", "agentweaver-a2a-client-tls"]);
const SECRET_READ_ATTEMPTS = 3;
const SECRET_READ_TIMEOUT_MS = 30_000;

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function firstLine(value) {
  return String(value ?? "").split(/\r?\n/).map((line) => line.trim()).find(Boolean) || "";
}

function kubectlSecretReadFailure(name, namespace, result) {
  const reason = firstLine(result.stderr) || firstLine(result.stdout) || `exit code ${result.code}`;
  const error = new Error(`Kubernetes Secret read failed for ${name} in namespace ${namespace}: ${reason}`);
  error.stderr = result.stderr;
  error.stdout = result.stdout;
  error.code = result.code;
  return error;
}

function kubectlSecretReadUnknown(name, namespace, error) {
  const reason = firstLine(error?.message) || firstLine(error?.stderr) || String(error);
  return {
    state: "unknown",
    reason: `Kubernetes Secret read failed for ${name} in namespace ${namespace}: ${reason}`,
  };
}

function isKubectlSecretNotFound({ stdout, stderr }) {
  const haystack = `${stdout ?? ""}\n${stderr ?? ""}`.toLowerCase();
  return /\bnotfound\b/.test(haystack) || /\bsecrets?\b.*\bnot\s+found\b/.test(haystack);
}

function a2aSecretReadError(result) {
  return new Error(`${result.reason}. State could not be determined. Aborting A2A mTLS secret reconciliation. No absent or partial result was inferred. Resolve the read failure and rerun the deployment.`);
}

function logSecretReadRetry(log, { attempt, attempts, delay, error, label }) {
  const reason = (error?.message || String(error)).split("\n")[0];
  log.warn?.(`  ${label}: attempt ${attempt}/${attempts} failed (${reason}); retrying in ${delay}ms`);
}

/** Reads a K8s Secret state as present, absent, or unknown. */
export async function secretReadState(name, namespace, { exec = execDefault, log = logDefault, sleep = defaultSleep } = {}) {
  try {
    return await withRetry(
      async () => {
        const { stdout, stderr, code } = await exec.capture("kubectl", ["get", "secret", name, "--namespace", namespace], {
          allowFailure: true,
          timeoutMs: SECRET_READ_TIMEOUT_MS,
        });
        if (code === 0) return { state: "present" };
        if (isKubectlSecretNotFound({ stdout, stderr })) return { state: "absent" };
        throw kubectlSecretReadFailure(name, namespace, { stdout, stderr, code });
      },
      {
        attempts: SECRET_READ_ATTEMPTS,
        baseDelayMs: 250,
        maxDelayMs: 1_000,
        label: `Kubernetes Secret read ${name}`,
        isTransient: () => true,
        onRetry: (details) => logSecretReadRetry(log, details),
        sleep,
      },
    );
  } catch (error) {
    return kubectlSecretReadUnknown(name, namespace, error);
  }
}

/** Base64-encodes file content with no line wrapping, matching `base64 | tr -d '\n'`. */
function base64OfFile(fsImpl, filePath) {
  return fsImpl.readFileSync(filePath).toString("base64");
}

function secretYaml({ name, namespace, type, data }) {
  const dataLines = Object.entries(data)
    .map(([key, value]) => `  ${key}: ${value}`)
    .join("\n");
  return `apiVersion: v1
kind: Secret
metadata:
  name: ${name}
  namespace: ${namespace}
  labels:
    app.kubernetes.io/part-of: agentweaver
type: ${type}
data:
${dataLines}
`;
}

/**
 * Generates + applies the A2A mTLS certificate secrets: faithful port of
 * gen-a2a-mtls-certs.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing. `opts.force` (bool) regenerates even if all three secrets exist.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, fs: fsImpl = fs, repoRoot = DEFAULT_REPO_ROOT, force = false, sleep = defaultSleep } = opts;
  const namespace = cfg.NAMESPACE || "agentweaver";
  const scratchRoot = cfg.AGENTWEAVER_TMP_DIR || path.join(repoRoot, ".agentweaver", "tmp");
  const workDir = path.join(scratchRoot, `a2a-mtls-${process.pid}`);

  log.info("");
  log.section("A2A mTLS certificate generation (spec-018 H1)");
  log.field("Namespace", namespace);
  log.field("Work dir", workDir);
  log.field("Force regen", String(force));
  log.info("");

  if (!force) {
    const existing = [];
    for (const name of SECRET_NAMES) {
      const result = await secretReadState(name, namespace, { exec, log, sleep });
      if (result.state === "unknown") throw a2aSecretReadError(result);
      if (result.state === "present") existing.push(name);
    }
    if (existing.length === SECRET_NAMES.length) {
      log.ok("All three A2A mTLS secrets already exist -- skipping generation.");
      log.info("     Pass force: true to regenerate.");
      return { skipped: true };
    }
    if (existing.length > 0) {
      throw new Error(`Partial A2A mTLS secrets found: ${existing.join(", ")}. Re-run with force: true to regenerate all three consistently.`);
    }
  }

  fsImpl.rmSync(workDir, { recursive: true, force: true });
  fsImpl.mkdirSync(workDir, { recursive: true });

  try {
    // -- 1. CA --
    log.info("Generating internal A2A CA...");
    const caKey = path.join(workDir, "ca.key");
    const caCrt = path.join(workDir, "ca.crt");
    await exec.run("openssl", ["genrsa", "-out", caKey, "4096"]);
    await exec.run("openssl", [
      "req",
      "-new",
      "-x509",
      "-key",
      caKey,
      "-out",
      caCrt,
      "-days",
      "730",
      "-subj",
      "/CN=agentweaver-a2a-ca/O=agentweaver",
      "-addext",
      "keyUsage=critical,keyCertSign,cRLSign",
      "-addext",
      "basicConstraints=critical,CA:TRUE",
    ]);
    log.info("  CA certificate generated.");

    // -- 2. Server cert (AgentHost) --
    log.info("Generating AgentHost server certificate...");
    const serverKey = path.join(workDir, "server.key");
    const serverCsr = path.join(workDir, "server.csr");
    const serverCrt = path.join(workDir, "server.crt");
    const serverExtCnf = path.join(workDir, "server-ext.cnf");
    await exec.run("openssl", ["genrsa", "-out", serverKey, "2048"]);
    await exec.run("openssl", ["req", "-new", "-key", serverKey, "-out", serverCsr, "-subj", "/CN=agentweaver-agenthost/O=agentweaver"]);
    fsImpl.writeFileSync(
      serverExtCnf,
      `[req_ext]\nkeyUsage = critical, digitalSignature, keyEncipherment\nextendedKeyUsage = serverAuth\nsubjectAltName = @alt_names\n\n[alt_names]\nDNS.1 = agentweaver-agenthost\nDNS.2 = agentweaver-agent-host.agentweaver.svc.cluster.local\n`,
    );
    await exec.run("openssl", [
      "x509",
      "-req",
      "-in",
      serverCsr,
      "-CA",
      caCrt,
      "-CAkey",
      caKey,
      "-CAcreateserial",
      "-out",
      serverCrt,
      "-days",
      "365",
      "-extensions",
      "req_ext",
      "-extfile",
      serverExtCnf,
    ]);
    log.info("  Server certificate generated.");

    // -- 3. Client cert (worker) --
    log.info("Generating worker client certificate...");
    const clientKey = path.join(workDir, "client.key");
    const clientCsr = path.join(workDir, "client.csr");
    const clientCrt = path.join(workDir, "client.crt");
    const clientExtCnf = path.join(workDir, "client-ext.cnf");
    await exec.run("openssl", ["genrsa", "-out", clientKey, "2048"]);
    await exec.run("openssl", ["req", "-new", "-key", clientKey, "-out", clientCsr, "-subj", "/CN=agentweaver-worker/O=agentweaver"]);
    fsImpl.writeFileSync(clientExtCnf, `[req_ext]\nkeyUsage = critical, digitalSignature\nextendedKeyUsage = clientAuth\n`);
    await exec.run("openssl", [
      "x509",
      "-req",
      "-in",
      clientCsr,
      "-CA",
      caCrt,
      "-CAkey",
      caKey,
      "-CAcreateserial",
      "-out",
      clientCrt,
      "-days",
      "365",
      "-extensions",
      "req_ext",
      "-extfile",
      clientExtCnf,
    ]);
    log.info("  Client certificate generated.");

    // -- 4. Apply secrets --
    log.info("");
    log.info("Applying K8s Secrets...");

    const applySecret = async (name, manifest) => {
      if (force) {
        const result = await secretReadState(name, namespace, { exec, log, sleep });
        if (result.state === "unknown") throw a2aSecretReadError(result);
        if (result.state === "present") {
          await exec.run("kubectl", ["delete", "secret", name, "--namespace", namespace]);
        }
      }
      const manifestPath = path.join(workDir, `secret-${name}.yaml`);
      fsImpl.writeFileSync(manifestPath, manifest);
      await exec.run("kubectl", ["apply", "-f", manifestPath]);
      log.info(`  [applied] ${name}`);
    };

    await applySecret(
      "agentweaver-a2a-ca",
      secretYaml({
        name: "agentweaver-a2a-ca",
        namespace,
        type: "Opaque",
        data: { "ca.crt": base64OfFile(fsImpl, caCrt) },
      }),
    );

    await applySecret(
      "agentweaver-a2a-server-tls",
      secretYaml({
        name: "agentweaver-a2a-server-tls",
        namespace,
        type: "kubernetes.io/tls",
        data: {
          "tls.crt": base64OfFile(fsImpl, serverCrt),
          "tls.key": base64OfFile(fsImpl, serverKey),
          "ca.crt": base64OfFile(fsImpl, caCrt),
        },
      }),
    );

    await applySecret(
      "agentweaver-a2a-client-tls",
      secretYaml({
        name: "agentweaver-a2a-client-tls",
        namespace,
        type: "kubernetes.io/tls",
        data: {
          "tls.crt": base64OfFile(fsImpl, clientCrt),
          "tls.key": base64OfFile(fsImpl, clientKey),
          "ca.crt": base64OfFile(fsImpl, caCrt),
        },
      }),
    );

    log.info("");
    log.section("A2A mTLS certificate generation complete");
    log.info("  Secret agentweaver-a2a-server-tls  -> mounted in sandbox pod at /mnt/a2a-tls/");
    log.info("  Secret agentweaver-a2a-client-tls  -> mounted in api/worker pod at /mnt/a2a-client-tls/");
    log.info("  CA cert (ca.crt) included in both mounts for mutual validation.");
    log.info("");
    log.info("  REMINDER: Rotate certs before expiry (365 days) by re-running with force: true.");

    return { skipped: false };
  } finally {
    fsImpl.rmSync(workDir, { recursive: true, force: true });
  }
}
