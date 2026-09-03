// 40-verify.mjs -- Faithful Node port of scripts/aks/40-verify.sh
// (cross-checked against 40-verify.ps1). Read both before changing this
// file; they must stay in lockstep with this port's behavior.
//
// Post-deploy health verification: pod running counts per tier, Gateway/
// HTTPRoute programming status, an optional authenticated HTTP feature probe
// (unauthenticated 401 + authenticated 200s using an explicitly supplied
// AGENTWEAVER_VALIDATION_TOKEN), SecretProviderClass sync,
// API RBAC can-i checks, sandbox CRDs/resources, and storage prerequisites.
//
// Every check is recorded as {ok, message} in `results` and tallied into
// PASS/FAIL, matching the legacy scripts' running counters -- run() never
// throws for an individual failed check (that mirrors `set -euo pipefail`
// NOT being violated by the many `|| fail ...` guards in 40-verify.sh); it
// only returns { pass, fail, results, ok } and callers (cli.mjs) decide
// whether to exit non-zero.
//
// cfg is the resolved variables.mjs output: NAMESPACE. Optional:
// VALIDATION_TOKEN (falls back to env.AGENTWEAVER_VALIDATION_TOKEN).

import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import { createPrivateKey, createPublicKey, X509Certificate } from "node:crypto";

function certificatePem(text) {
  return String(text).match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/)?.[0] ?? null;
}

function timestamp(value) {
  if (typeof value === "number") return value < 10_000_000_000 ? value * 1000 : value;
  const parsed = Date.parse(value ?? 0);
  return Number.isFinite(parsed) ? parsed : 0;
}

export async function certificateValueUsability(value, { exec = execDefault, now = new Date() } = {}) {
  let pem = String(value ?? "").trim();
  if (!pem) return { usable: false, reason: "empty or private-key-less certificate secret" };
  if (!pem.includes("-----BEGIN")) {
    if (!/^[A-Za-z0-9+/=\r\n]+$/.test(pem)) return { usable: false, reason: "malformed certificate encoding" };
    const pfx = Buffer.from(pem.replace(/\s/g, ""), "base64");
    if (!pfx.length) return { usable: false, reason: "malformed certificate encoding" };
    const extracted = await exec.capture(
      "openssl",
      ["pkcs12", "-in", "-", "-nodes", "-passin", "pass:"],
      { input: pfx, allowFailure: true },
    );
    if (extracted.code !== 0) return { usable: false, reason: "malformed PKCS#12 certificate secret" };
    pem = extracted.stdout;
  }

  const certPem = certificatePem(pem);
  if (!certPem || !/-----BEGIN (?:RSA )?PRIVATE KEY-----/.test(pem)) {
    return { usable: false, reason: "private-key-less certificate secret" };
  }
  try {
    const certificate = new X509Certificate(certPem);
    const privateKey = createPrivateKey(pem);
    if (privateKey.asymmetricKeyType !== "rsa" || certificate.publicKey.asymmetricKeyType !== "rsa") {
      return { usable: false, reason: "certificate algorithm is not RSA" };
    }
    const privatePublic = createPublicKey(privateKey).export({ type: "spki", format: "der" });
    const certificatePublic = certificate.publicKey.export({ type: "spki", format: "der" });
    if (!privatePublic.equals(certificatePublic)) {
      return { usable: false, reason: "certificate private key does not match its public key" };
    }
    const keySize = privateKey.asymmetricKeyDetails?.modulusLength ?? 0;
    if (keySize < 2048) return { usable: false, reason: "RSA private key is smaller than 2048 bits" };
    const instant = now.getTime();
    if (Date.parse(certificate.validFrom) > instant) return { usable: false, reason: "certificate is not yet valid" };
    if (Date.parse(certificate.validTo) <= instant) return { usable: false, reason: "certificate is expired" };
    return { usable: true, reason: "usable RSA certificate with private key" };
  } catch {
    return { usable: false, reason: "malformed certificate or private-key material" };
  }
}

export async function verifyOAuthCertificateFamily({
  vaultName,
  name,
  exec = execDefault,
  now = new Date(),
  inspectValue = certificateValueUsability,
}) {
  const listed = await exec.capture("az", [
    "keyvault", "secret", "list-versions",
    "--vault-name", vaultName,
    "--name", name,
    "--output", "json",
  ], { allowFailure: true });
  if (listed.code !== 0) return { usable: 0, reason: "certificate versions could not be listed" };
  let versions;
  try {
    versions = JSON.parse(listed.stdout);
  } catch {
    return { usable: 0, reason: "certificate version metadata was malformed" };
  }
  const instant = now.getTime();
  const candidates = (Array.isArray(versions) ? versions : [])
    .filter((version) => version?.attributes?.enabled !== false)
    .filter((version) => !version?.attributes?.nbf || timestamp(version.attributes.nbf) <= instant)
    .filter((version) => !version?.attributes?.exp || timestamp(version.attributes.exp) > instant)
    .sort((a, b) => timestamp(b?.attributes?.created) - timestamp(a?.attributes?.created))
    .slice(0, 2);

  const failures = [];
  let usable = 0;
  for (const version of candidates) {
    const versionId = String(version?.id ?? "").split("/").at(-1);
    if (!versionId) {
      failures.push("version metadata had no version id");
      continue;
    }
    const secret = await exec.capture("az", [
      "keyvault", "secret", "show",
      "--vault-name", vaultName,
      "--name", name,
      "--version", versionId,
      "--query", "value",
      "--output", "tsv",
    ], { allowFailure: true });
    if (secret.code !== 0) {
      failures.push("certificate secret version could not be read");
      continue;
    }
    const inspected = await inspectValue(secret.stdout, { exec, now });
    if (inspected.usable) usable += 1;
    else failures.push(inspected.reason);
  }
  return {
    usable,
    reason: usable ? `${usable} runtime-usable active/previous version(s)` : failures[0] ?? "no enabled version in its valid time window",
  };
}

const RUNNING_POD_SELECTORS = [
  { label: "API", selector: "app=agentweaver-api" },
  { label: "Frontend", selector: "app=agentweaver-frontend" },
  { label: "MCP", selector: "app=agentweaver-mcp" },
  { label: "Worker", selector: "app=agentweaver-worker" },
  { label: "AgentHost warm-pool", selector: "app.kubernetes.io/component=agent-host" },
];

const HTTP_ROUTES = ["agentweaver-api-route", "agentweaver-frontend-route", "agentweaver-mcp-route"];

const SECRET_PROVIDER_CLASSES = ["agentweaver-secrets", "agentweaver-user-tokens"];

// Resources the API/worker ServiceAccounts must be able to `create` for the
// pod-per-run sandbox model. `pods/exec` is expressed as a resource+subresource
// pair, NOT the legacy "pods/exec" slash string: modern kubectl (>=1.33) stopped
// resolving the slash form in `kubectl auth can-i`, so it returns a false "no"
// even though RBAC grants create on the exec subresource. That grant is
// deliberately retained (and natively scoped to agentweaver-agent-host- pods by
// vap-sandbox-exec.yaml, issue #473 / security review Alert 4), so the probe must
// use `--subresource=exec` to reflect the real, hardened authorization.
const SANDBOX_CREATE_RESOURCES = [
  { resource: "sandboxclaims.extensions.agents.x-k8s.io" },
  { resource: "pods", subresource: "exec" },
];

function canICreateArgs({ resource, subresource }, serviceAccount, namespace) {
  const args = ["auth", "can-i", "create", resource, `--as=${serviceAccount}`, "--namespace", namespace];
  if (subresource) args.push(`--subresource=${subresource}`);
  return args;
}

/** Counts Running pods matching a label selector. Mirrors the bash/ps1 `Get-RunningPodCount`. */
export async function runningPodCount(namespace, selector, { exec = execDefault } = {}) {
  const { stdout, code } = await exec.capture(
    "kubectl",
    ["get", "pods", "--namespace", namespace, "--selector", selector, "--field-selector=status.phase=Running", "--no-headers"],
    { allowFailure: true },
  );
  if (code !== 0) return 0;
  return stdout.split("\n").map((l) => l.trim()).filter(Boolean).length;
}

/** True if a `kubectl <args>` invocation exits 0. Mirrors Test-Kubectl/`kubectl ... >/dev/null 2>&1`. */
export async function kubectlOk(args, { exec = execDefault } = {}) {
  const { code } = await exec.capture("kubectl", args, { allowFailure: true });
  return code === 0;
}

/** Reads a jsonpath field from a resource, or '' if unavailable. */
async function jsonpath(namespace, resourceArgs, path, { exec = execDefault } = {}) {
  const { stdout, code } = await exec.capture(
    "kubectl",
    ["get", ...resourceArgs, "--namespace", namespace, "--output", `jsonpath=${path}`],
    { allowFailure: true },
  );
  return code === 0 ? stdout.trim() : "";
}

/** Fetches an HTTP status code (never throws; returns "000" on any network failure). Mirrors Get-HttpStatus/curl. */
export async function httpStatus(url, { bearerToken, fetchImpl = fetch, timeoutMs = 10_000 } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const headers = bearerToken ? { Authorization: `Bearer ${bearerToken}` } : {};
    const resp = await fetchImpl(url, {
      method: "GET",
      headers,
      signal: controller.signal,
      ...(bearerToken ? { redirect: "error" } : {}),
    });
    return resp.status;
  } catch {
    return "000";
  } finally {
    clearTimeout(timer);
  }
}

/** Fetches JSON from an authenticated endpoint, returning `[]` on any failure. Mirrors Get-HttpJson. */
export async function httpJson(url, bearerToken, { fetchImpl = fetch, timeoutMs = 10_000 } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetchImpl(url, {
      method: "GET",
      headers: { Authorization: `Bearer ${bearerToken}` },
      signal: controller.signal,
      redirect: "error",
    });
    if (!resp.ok) return [];
    return await resp.json();
  } catch {
    return [];
  } finally {
    clearTimeout(timer);
  }
}

/** Fetches an anonymous discovery document, returning null on any failure. */
export async function httpDiscoveryJson(url, { fetchImpl = fetch, timeoutMs = 10_000 } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetchImpl(url, { method: "GET", signal: controller.signal });
    if (!resp.ok) return null;
    return await resp.json();
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

/** Extracts the first project's id from any of the shapes the legacy scripts tolerate. */
export function firstProjectId(projectsJson) {
  let list;
  if (Array.isArray(projectsJson)) list = projectsJson;
  else if (Array.isArray(projectsJson?.projects)) list = projectsJson.projects;
  else if (Array.isArray(projectsJson?.items)) list = projectsJson.items;
  else return "";
  const first = list[0];
  if (!first) return "";
  return first.id || first.projectId || "";
}

/**
 * Runs the full post-deploy verification suite. Never throws for an
 * individual check failure -- returns `{ ok, pass, fail, results }` where
 * `ok` is `fail === 0`, mirroring the legacy scripts' `[[ "${FAIL}" -eq 0 ]]`
 * final exit code.
 *
 * @param {Record<string, unknown>} cfg Resolved variables.mjs output (NAMESPACE required). Optional: VALIDATION_TOKEN.
 * @param {object} [opts]
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {typeof fetch} [opts.fetchImpl] Injectable for tests.
 * @param {Record<string,string>} [opts.env] Defaults to process.env.
 */
export async function run(cfg, opts = {}) {
  const {
    exec = execDefault,
    log = logDefault,
    fetchImpl = fetch,
    env = process.env,
    certificateInspector = certificateValueUsability,
  } = opts;
  const NAMESPACE = cfg.NAMESPACE;

  const results = [];
  const record = (ok, message) => {
    results.push({ ok, message });
    if (ok) log.ok(message);
    else log.error(message);
    return ok;
  };
  const info = (message) => {
    results.push({ ok: null, message });
    log.info(`  [INFO] ${message}`);
  };

  log.info("");
  log.section("Agentweaver AKS deployment verification");
  log.field("Namespace", NAMESPACE);
  log.info("");

  log.info("--- Pod status ---");
  await exec.run("kubectl", ["get", "pods", "--namespace", NAMESPACE, "-o", "wide"], { allowFailure: true }).catch(() => {});
  log.info("");

  for (const { label, selector } of RUNNING_POD_SELECTORS) {
    const count = await runningPodCount(NAMESPACE, selector, { exec });
    record(count >= 1, count >= 1 ? `${label} pod(s) running (${count})` : `No ${label} pods in Running state`);
  }

  log.info("");
  log.info("--- Gateway status ---");
  const programmed = await jsonpath(NAMESPACE, ["gateway", "agentweaver-gateway"], '{.status.conditions[?(@.type=="Programmed")].status}', { exec });
  const gatewayIp = await jsonpath(NAMESPACE, ["gateway", "agentweaver-gateway"], "{.status.addresses[0].value}", { exec });
  const previewProgrammed = await jsonpath(NAMESPACE, ["gateway", "agentweaver-preview-gateway"], '{.status.conditions[?(@.type=="Programmed")].status}', { exec });

  record(programmed === "True", programmed === "True" ? "Gateway Programmed=True" : `Gateway not yet Programmed (status=${programmed})`);
  record(Boolean(gatewayIp), gatewayIp ? `Gateway address: ${gatewayIp}` : "Gateway has no address yet");
  record(
    previewProgrammed === "True",
    previewProgrammed === "True" ? "Preview Gateway Programmed=True" : `Preview Gateway not yet Programmed (status=${previewProgrammed})`,
  );

  log.info("");
  log.info("--- HTTPRoute status ---");
  for (const route of HTTP_ROUTES) {
    const accepted = await jsonpath(NAMESPACE, ["httproute", route], '{.status.parents[0].conditions[?(@.type=="Accepted")].status}', { exec });
    const resolved = await jsonpath(NAMESPACE, ["httproute", route], '{.status.parents[0].conditions[?(@.type=="ResolvedRefs")].status}', { exec });
    record(
      accepted === "True" && resolved === "True",
      accepted === "True" && resolved === "True"
        ? `HTTPRoute ${route}: Accepted=True, ResolvedRefs=True`
        : `HTTPRoute ${route}: Accepted=${accepted}, ResolvedRefs=${resolved}`,
    );
  }

  log.info("");
  const domain = await jsonpath(NAMESPACE, ["defaultdomaincertificate", "cert"], "{.status.domain}", { exec });
  let host = "";
  if (domain) {
    host = `agentweaver.${domain.replace(/^\*\./, "")}`;
    info(`Ingress host: ${host}`);
  } else {
    info("Could not derive HOST from DefaultDomainCertificate -- skipping HTTP checks");
  }

  if (host) {
    log.info("");
    log.info("--- Authenticated feature validation ---");
    const origin = `https://${host}`;
    const resource = `${origin}/mcp`;
    const configuredOrigin = await jsonpath(
      NAMESPACE,
      ["configmap", "agentweaver-runtime-config"],
      "{.data.OAUTH_PUBLIC_ORIGIN}",
      { exec },
    );
    const configuredSigningName = await jsonpath(
      NAMESPACE,
      ["configmap", "agentweaver-runtime-config"],
      "{.data.OAUTH_SIGNING_CERTIFICATE_NAME}",
      { exec },
    );
    const configuredEncryptionName = await jsonpath(
      NAMESPACE,
      ["configmap", "agentweaver-runtime-config"],
      "{.data.OAUTH_ENCRYPTION_CERTIFICATE_NAME}",
      { exec },
    );
    const expectedSigningName = cfg.OAUTH_SIGNING_CERTIFICATE_NAME || "agentweaver-oauth-signing";
    const expectedEncryptionName = cfg.OAUTH_ENCRYPTION_CERTIFICATE_NAME || "agentweaver-oauth-encryption";
    record(
      configuredOrigin === origin,
      configuredOrigin === origin
        ? "OAuth public origin ConfigMap value matches the canonical ingress origin"
        : `OAuth public origin ConfigMap mismatch (expected ${origin})`,
    );
    record(
      configuredSigningName === expectedSigningName && configuredEncryptionName === expectedEncryptionName,
      configuredSigningName === expectedSigningName && configuredEncryptionName === expectedEncryptionName
        ? "OAuth Key Vault certificate families are wired to the runtime ConfigMap"
        : "OAuth Key Vault certificate family ConfigMap values are missing or inconsistent",
    );

    if (cfg.KEYVAULT_NAME) {
      for (const [usage, name] of [
        ["signing", expectedSigningName],
        ["encryption", expectedEncryptionName],
      ]) {
        const certificate = await exec.capture("az", [
          "keyvault", "certificate", "show",
          "--vault-name", cfg.KEYVAULT_NAME,
          "--name", name,
          "--output", "none",
        ], { allowFailure: true });
        record(
          certificate.code === 0,
          certificate.code === 0
            ? `OAuth ${usage} certificate '${name}' exists in Key Vault`
            : `OAuth ${usage} certificate '${name}' is missing from Key Vault`,
        );
        const versions = await verifyOAuthCertificateFamily({
          vaultName: cfg.KEYVAULT_NAME,
          name,
          exec,
          inspectValue: certificateInspector,
        });
        record(
          versions.usable >= 1,
          versions.usable >= 1
            ? `OAuth ${usage} certificate has ${versions.reason}`
            : `OAuth ${usage} certificate is not runtime-usable: ${versions.reason}`,
        );
      }
    } else {
      info("KEYVAULT_NAME is unavailable; skipping OAuth certificate version verification");
    }
    const resourceMetadataPaths = [
      "/.well-known/oauth-protected-resource",
      "/.well-known/oauth-protected-resource/mcp",
    ];
    for (const path of resourceMetadataPaths) {
      const document = await httpDiscoveryJson(`${origin}${path}`, { fetchImpl });
      const valid = document?.resource === resource
        && Array.isArray(document.authorization_servers)
        && document.authorization_servers.length === 1
        && document.authorization_servers[0]?.replace(/\/$/, "") === origin
        && Array.isArray(document.scopes_supported)
        && document.scopes_supported.includes("mcp:invoke");
      record(valid, valid
        ? `MCP protected-resource metadata ${path} is canonical`
        : `MCP protected-resource metadata ${path} is missing or inconsistent`);
    }

    const asMetadata = await httpDiscoveryJson(
      `${origin}/.well-known/oauth-authorization-server`,
      { fetchImpl },
    );
    const oidcMetadata = await httpDiscoveryJson(
      `${origin}/.well-known/openid-configuration`,
      { fetchImpl },
    );
    const metadataValid = [asMetadata, oidcMetadata].every((document) =>
      document?.issuer?.replace(/\/$/, "") === origin
      && document?.jwks_uri === `${origin}/oauth/jwks`);
    record(metadataValid, metadataValid
      ? "OAuth AS and OIDC metadata advertise the canonical issuer and JWKS"
      : "OAuth AS or OIDC metadata is missing or inconsistent");

    const jwks = await httpDiscoveryJson(`${origin}/oauth/jwks`, { fetchImpl });
    const jwksValid = Array.isArray(jwks?.keys)
      && jwks.keys.some((key) => key?.use === "sig"
        && key?.alg === "RS256"
        && typeof key?.kid === "string"
        && key.kid.length > 0);
    record(jwksValid, jwksValid
      ? "OAuth JWKS exposes a keyed RS256 signing key"
      : "OAuth JWKS has no keyed RS256 signing key");

    const unauthProjectsStatus = await httpStatus(`https://${host}/api/projects`, { fetchImpl });
    record(
      unauthProjectsStatus === 401,
      unauthProjectsStatus === 401
        ? `Unauthenticated /api/projects rejected -> HTTP ${unauthProjectsStatus}`
        : `Unauthenticated /api/projects -> HTTP ${unauthProjectsStatus} (expected 401)`,
    );

    const validationToken = cfg.VALIDATION_TOKEN || env.AGENTWEAVER_VALIDATION_TOKEN || "";
    if (!validationToken) {
      info("Set AGENTWEAVER_VALIDATION_TOKEN to validate signed-in identity plus project memory/decision APIs");
    } else {
      const authStatus = await httpStatus(`https://${host}/api/auth/github`, { bearerToken: validationToken, fetchImpl });
      const projectsStatus = await httpStatus(`https://${host}/api/projects`, { bearerToken: validationToken, fetchImpl });
      record(authStatus === 200, authStatus === 200 ? `Authenticated /api/auth/github -> HTTP ${authStatus}` : `Authenticated /api/auth/github -> HTTP ${authStatus} (expected 200)`);
      record(projectsStatus === 200, projectsStatus === 200 ? `Authenticated /api/projects -> HTTP ${projectsStatus}` : `Authenticated /api/projects -> HTTP ${projectsStatus} (expected 200)`);

      const projectsJson = await httpJson(`https://${host}/api/projects`, validationToken, { fetchImpl });
      const projectId = firstProjectId(projectsJson);
      if (projectId) {
        for (const path of [`/api/projects/${projectId}/memory`, `/api/projects/${projectId}/decisions/inbox`, `/api/projects/${projectId}/decisions`]) {
          const status = await httpStatus(`https://${host}${path}`, { bearerToken: validationToken, fetchImpl });
          record(status === 200, status === 200 ? `Authenticated ${path} -> HTTP ${status}` : `Authenticated ${path} -> HTTP ${status} (expected 200)`);
        }
      } else {
        info("Authenticated account has no project id to validate memory/decision APIs");
      }
    }
  }

  log.info("");
  log.info("--- SecretProviderClass sync ---");
  for (const spc of SECRET_PROVIDER_CLASSES) {
    const exists = await kubectlOk(["get", "secretproviderclass", spc, "--namespace", NAMESPACE], { exec });
    record(exists, exists ? `SecretProviderClass ${spc} exists` : `SecretProviderClass ${spc} missing`);
  }
  const { stdout: spcStatusRaw, code: spcStatusCode } = await exec.capture(
    "kubectl",
    ["get", "secretproviderclasspodstatus", "--namespace", NAMESPACE, "--no-headers"],
    { allowFailure: true },
  );
  const spcStatusCount = spcStatusCode === 0 ? spcStatusRaw.split("\n").map((l) => l.trim()).filter(Boolean).length : 0;
  record(spcStatusCount >= 1, spcStatusCount >= 1 ? `SecretProviderClassPodStatus objects present (${spcStatusCount})` : "No SecretProviderClassPodStatus objects found");
  info("agentweaver-user-tokens is installation-only; run-scoped agentweaver-user-token-* SPCs appear only while AgentHost pods are running");

  log.info("");
  log.info("--- API RBAC ---");
  const roleOk = await kubectlOk(["get", "role", "agentweaver-api-sandbox", "--namespace", NAMESPACE], { exec });
  const roleBindingOk = await kubectlOk(["get", "rolebinding", "agentweaver-api-sandbox", "--namespace", NAMESPACE], { exec });
  record(roleOk && roleBindingOk, roleOk && roleBindingOk ? "API sandbox Role and RoleBinding exist" : "API sandbox Role/RoleBinding missing");

  const apiServiceAccount = `system:serviceaccount:${NAMESPACE}:agentweaver-api`;
  let canCreateAll = true;
  for (const resource of SANDBOX_CREATE_RESOURCES) {
    const canCreate = await kubectlOk(canICreateArgs(resource, apiServiceAccount, NAMESPACE), { exec });
    if (!canCreate) canCreateAll = false;
  }
  record(
    canCreateAll,
    canCreateAll
      ? "API ServiceAccount can create SandboxClaims and use legacy pods/exec"
      : "API ServiceAccount lacks required sandbox permissions",
  );

  const workerRoleOk = await kubectlOk(["get", "role", "agentweaver-worker-sandbox", "--namespace", NAMESPACE], { exec });
  const workerRoleBindingOk = await kubectlOk(["get", "rolebinding", "agentweaver-worker-sandbox", "--namespace", NAMESPACE], { exec });
  record(workerRoleOk && workerRoleBindingOk, workerRoleOk && workerRoleBindingOk ? "Worker sandbox Role and RoleBinding exist" : "Worker sandbox Role/RoleBinding missing");

  const workerServiceAccount = `system:serviceaccount:${NAMESPACE}:agentweaver-worker`;
  let workerCanCreateAll = true;
  for (const resource of SANDBOX_CREATE_RESOURCES) {
    const canCreate = await kubectlOk(canICreateArgs(resource, workerServiceAccount, NAMESPACE), { exec });
    if (!canCreate) workerCanCreateAll = false;
  }
  record(
    workerCanCreateAll,
    workerCanCreateAll
      ? "Worker ServiceAccount can create SandboxClaims and use legacy pods/exec"
      : "Worker ServiceAccount lacks required sandbox permissions",
  );

  log.info("");
  log.info("--- Sandbox CRDs/resources ---");
  const kataRuntimeClassOk = await kubectlOk(["get", "runtimeclass", "kata-vm-isolation"], { exec });
  record(kataRuntimeClassOk, kataRuntimeClassOk ? "kata-vm-isolation RuntimeClass present" : "kata-vm-isolation RuntimeClass missing");
  const sandboxTemplateOk = await kubectlOk(["get", "sandboxtemplate", "agentweaver-agent-host", "--namespace", NAMESPACE], { exec });
  record(sandboxTemplateOk, sandboxTemplateOk ? "SandboxTemplate agentweaver-agent-host exists" : "SandboxTemplate agentweaver-agent-host missing");
  const sandboxWarmPoolOk = await kubectlOk(["get", "sandboxwarmpool", "agentweaver-agent-host", "--namespace", NAMESPACE], { exec });
  record(sandboxWarmPoolOk, sandboxWarmPoolOk ? "SandboxWarmPool agentweaver-agent-host exists" : "SandboxWarmPool agentweaver-agent-host missing");

  const legacyTemplate = await kubectlOk(["get", "sandboxtemplate", "agentweaver-sandbox", "--namespace", NAMESPACE], { exec });
  const legacyWarmPool = await kubectlOk(["get", "sandboxwarmpool", "agentweaver-sandbox", "--namespace", NAMESPACE], { exec });
  const legacyPresent = legacyTemplate || legacyWarmPool;
  record(!legacyPresent, legacyPresent ? "Legacy agentweaver-sandbox template/warm pool still exists; remove it before verifying" : "Legacy agentweaver-sandbox template/warm pool absent");

  log.info("");
  log.info("--- Storage ---");
  const storageClassOk = await kubectlOk(["get", "storageclass", "azurefile-csi-premium-uid1000"], { exec });
  record(storageClassOk, storageClassOk ? "Workspace StorageClass exists" : "Workspace StorageClass missing");
  const pvcOk = await kubectlOk(["get", "pvc", "agentweaver-workspace", "--namespace", NAMESPACE], { exec });
  record(pvcOk, pvcOk ? "Workspace PVC exists" : "Workspace PVC missing");

  const pass = results.filter((r) => r.ok === true).length;
  const fail = results.filter((r) => r.ok === false).length;

  log.info("");
  log.info("===================================================");
  log.info(` VERIFICATION SUMMARY: ${pass} passed, ${fail} failed`);
  log.info("===================================================");
  log.info(fail === 0 ? " ALL CHECKS PASSED" : " SOME CHECKS FAILED -- see output above");
  log.info("");

  return { ok: fail === 0, pass, fail, results };
}
