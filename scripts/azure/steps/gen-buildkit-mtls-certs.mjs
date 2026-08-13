// gen-buildkit-mtls-certs.mjs -- mTLS material for the BuildKit broker (#757 capability scope).
//
// BuildKit's only authentication mechanism is mutual TLS. The broker executes attacker-influenced
// Dockerfiles, so "reachable on the network" must not be sufficient to drive it: a caller also has
// to present a client certificate signed by a CA the broker was started with. This mirrors
// gen-a2a-mtls-certs.mjs deliberately -- same openssl flow, same Secret shape, same idempotency
// rules -- so operators have one pattern to learn and reviewers one shape to audit.
//
// Produces:
//   agentweaver-buildkit-server-tls  (namespace agentweaver-build) -> mounted by buildkitd
//   agentweaver-buildkit-client-tls  (namespace agentweaver)       -> mounted by the sandbox pod
//
// The two CAs are separate on purpose: the A2A CA authenticates Agentweaver's own control traffic,
// this one authorises build submission. Sharing them would let a stolen build client certificate
// impersonate the worker.

import fs from "node:fs";
import path from "node:path";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

export const BUILD_NAMESPACE = "agentweaver-build";
export const SERVER_SECRET = "agentweaver-buildkit-server-tls";
export const CLIENT_SECRET = "agentweaver-buildkit-client-tls";

/** True if a K8s Secret already exists in the namespace. */
export async function secretExists(name, namespace, { exec = execDefault } = {}) {
  const { code } = await exec.capture("kubectl", ["get", "secret", name, "--namespace", namespace], { allowFailure: true });
  return code === 0;
}

function secretYaml({ name, namespace, data }) {
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
type: kubernetes.io/tls
data:
${dataLines}
`;
}

/**
 * Generates and applies the BuildKit broker mTLS secrets.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs (NAMESPACE).
 * @param {object} [opts] Injectable collaborators. `opts.force` regenerates even if both exist.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, fs: fsImpl = fs, repoRoot = DEFAULT_REPO_ROOT, force = false } = opts;
  const namespace = cfg.NAMESPACE || "agentweaver";
  const scratchRoot = cfg.AGENTWEAVER_TMP_DIR || path.join(repoRoot, ".agentweaver", "tmp");
  const workDir = path.join(scratchRoot, `buildkit-mtls-${process.pid}`);
  const base64OfFile = (filePath) => fsImpl.readFileSync(filePath).toString("base64");

  log.info("");
  log.section("BuildKit broker mTLS certificate generation");
  log.field("Client namespace", namespace);
  log.field("Broker namespace", BUILD_NAMESPACE);
  log.field("Force regen", String(force));
  log.info("");

  if (!force) {
    const present = [];
    if (await secretExists(SERVER_SECRET, BUILD_NAMESPACE, { exec })) present.push(SERVER_SECRET);
    if (await secretExists(CLIENT_SECRET, namespace, { exec })) present.push(CLIENT_SECRET);
    if (present.length === 2) {
      log.ok("Both BuildKit mTLS secrets already exist -- skipping generation.");
      log.info("     Pass force: true to regenerate.");
      return { skipped: true };
    }
    if (present.length === 1) {
      // A half-generated pair cannot authenticate: the surviving certificate was signed by a CA
      // whose key we just discarded. Fail loudly rather than emit a mismatched pair.
      throw new Error(`Partial BuildKit mTLS secrets found: ${present.join(", ")}. Re-run with force: true to regenerate both consistently.`);
    }
  }

  fsImpl.rmSync(workDir, { recursive: true, force: true });
  fsImpl.mkdirSync(workDir, { recursive: true });

  try {
    const caKey = path.join(workDir, "ca.key");
    const caCrt = path.join(workDir, "ca.crt");
    log.info("Generating internal BuildKit CA...");
    await exec.run("openssl", ["genrsa", "-out", caKey, "4096"]);
    await exec.run("openssl", [
      "req", "-new", "-x509", "-key", caKey, "-out", caCrt, "-days", "730",
      "-subj", "/CN=agentweaver-buildkit-ca/O=agentweaver",
      "-addext", "keyUsage=critical,keyCertSign,cRLSign",
      "-addext", "basicConstraints=critical,CA:TRUE",
    ]);

    const issue = async (kind, commonName, extensions) => {
      const key = path.join(workDir, `${kind}.key`);
      const csr = path.join(workDir, `${kind}.csr`);
      const crt = path.join(workDir, `${kind}.crt`);
      const cnf = path.join(workDir, `${kind}-ext.cnf`);
      await exec.run("openssl", ["genrsa", "-out", key, "2048"]);
      await exec.run("openssl", ["req", "-new", "-key", key, "-out", csr, "-subj", `/CN=${commonName}/O=agentweaver`]);
      fsImpl.writeFileSync(cnf, extensions);
      await exec.run("openssl", [
        "x509", "-req", "-in", csr, "-CA", caCrt, "-CAkey", caKey, "-CAcreateserial",
        "-out", crt, "-days", "365", "-extensions", "req_ext", "-extfile", cnf,
      ]);
      return { key, crt };
    };

    log.info("Generating buildkitd server certificate...");
    const server = await issue(
      "server",
      "agentweaver-buildkit",
      "[req_ext]\nkeyUsage = critical, digitalSignature, keyEncipherment\nextendedKeyUsage = serverAuth\n"
        + "subjectAltName = @alt_names\n\n[alt_names]\nDNS.1 = agentweaver-buildkit\n"
        + `DNS.2 = agentweaver-buildkit.${BUILD_NAMESPACE}.svc.cluster.local\nIP.1 = 127.0.0.1\n`,
    );

    log.info("Generating sandbox client certificate...");
    const client = await issue(
      "client",
      "agentweaver-sandbox-build-client",
      "[req_ext]\nkeyUsage = critical, digitalSignature\nextendedKeyUsage = clientAuth\n",
    );

    // buildkitd's only health signal is a real mTLS gRPC call, so the readiness probe has to
    // present a client certificate too. It cannot reuse the server certificate: that one is
    // extendedKeyUsage=serverAuth, and buildkitd rejects it with "tls: bad certificate", leaving the
    // pod permanently 0/1. Rather than widen the server certificate's EKU, the broker gets its own
    // clientAuth certificate, so the key that proves the broker's identity to sandboxes still cannot
    // be used to submit builds.
    log.info("Generating broker readiness-probe client certificate...");
    const probe = await issue(
      "probe",
      "agentweaver-buildkit-probe",
      "[req_ext]\nkeyUsage = critical, digitalSignature\nextendedKeyUsage = clientAuth\n",
    );

    log.info("");
    log.info("Applying K8s Secrets...");
    const applySecret = async (name, ns, manifest) => {
      if (force && (await secretExists(name, ns, { exec }))) {
        await exec.run("kubectl", ["delete", "secret", name, "--namespace", ns]);
      }
      const manifestPath = path.join(workDir, `secret-${name}.yaml`);
      fsImpl.writeFileSync(manifestPath, manifest);
      await exec.run("kubectl", ["apply", "-f", manifestPath]);
      log.info(`  [applied] ${ns}/${name}`);
    };

    await applySecret(SERVER_SECRET, BUILD_NAMESPACE, secretYaml({
      name: SERVER_SECRET,
      namespace: BUILD_NAMESPACE,
      data: {
        "tls.crt": base64OfFile(server.crt),
        "tls.key": base64OfFile(server.key),
        "ca.crt": base64OfFile(caCrt),
        "probe.crt": base64OfFile(probe.crt),
        "probe.key": base64OfFile(probe.key),
      },
    }));

    await applySecret(CLIENT_SECRET, namespace, secretYaml({
      name: CLIENT_SECRET,
      namespace,
      data: { "tls.crt": base64OfFile(client.crt), "tls.key": base64OfFile(client.key), "ca.crt": base64OfFile(caCrt) },
    }));

    log.info("");
    log.section("BuildKit broker mTLS certificate generation complete");
    log.info(`  ${BUILD_NAMESPACE}/${SERVER_SECRET} -> mounted by buildkitd at /mnt/buildkit-tls/`);
    log.info(`  ${namespace}/${CLIENT_SECRET}       -> mounted by the sandbox pod at /mnt/buildkit-client-tls/`);
    log.info("");
    log.info("  NOTE: the client certificate is reachable by sandboxed code by design -- it authorises");
    log.info("        build submission and nothing else. The broker holds no registry credentials, so a");
    log.info("        stolen client certificate buys an attacker a build, not a push.");
    log.info("  REMINDER: rotate before expiry (365 days) by re-running with force: true.");

    return { skipped: false };
  } finally {
    fsImpl.rmSync(workDir, { recursive: true, force: true });
  }
}
