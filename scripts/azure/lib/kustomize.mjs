// kustomize.mjs -- Kustomize-based replacement for the old envsubst
// renderer (render.mjs) as far as k8s/*.yaml manifest rendering goes.
//
// Structural packaging lives in k8s/base/ (every manifest, generic
// placeholder values only) + k8s/overlays/production/ (images:, a
// configMapGenerator, and replacements: -- see that file's header comment
// for the full rationale). This module's job is narrower: at deploy time,
// steps/30-deploy.mjs only learns the real dynamic values (HOST,
// PREVIEW_HOSTNAME, TENANT_ID, ...) live, partway through the deploy (after
// waiting for the cluster's DefaultDomainCertificate). So:
//
//   1. writeOverlay() copies k8s/base + k8s/overlays/production into a
//      git-ignored scratch directory and rewrites ONLY the `images:` tags
//      and the `agentweaver-runtime-config` configMapGenerator literals in
//      the copied kustomization.yaml with the real resolved values -- a
//      small, targeted rewrite (same spirit as the old render.mjs, just
//      scoped to the handful of Kustomize-native dynamic fields instead of
//      scattered across 35 manifest files).
//   2. buildManifests() shells out to `kubectl kustomize <scratch overlay>`
//      (built into kubectl -- no separate `kustomize` binary needed) and
//      returns the fully-rendered multi-document YAML.
//   3. splitByFilenames() re-groups that single build output back into the
//      same named "manifest groups" steps/30-deploy.mjs has always applied
//      in staged order (identity/RBAC/quota/PVCs, network policies,
//      services/gateway/routes, sandbox template, deployments, worker) --
//      preserving the readiness-gate `kubectl wait` ordering that ships in
//      production today. This is why 30-deploy.mjs doesn't do one single
//      `kubectl apply -k` for everything: the staged rollout predates this
//      migration and is out of scope to redesign here.

import fs from "node:fs";
import path from "node:path";

export const OVERLAY_NAME = "production";

/** Base image names as they appear (as placeholders) in k8s/base/*.yaml. */
export const IMAGE_NAMES = Object.freeze({
  api: "agentweaverregistry.azurecr.io/agentweaver-api",
  frontend: "agentweaverregistry.azurecr.io/agentweaver-frontend",
  mcp: "agentweaverregistry.azurecr.io/agentweaver-mcp",
  agentHost: "agentweaverregistry.azurecr.io/agentweaver-agent-host",
});

/**
 * filename (as it exists under k8s/base/) -> the [{kind,name}] resources it
 * defines. Used to re-group `kubectl kustomize`'s single combined build
 * output back into the same per-file manifest groups steps/30-deploy.mjs
 * applies in staged order. Keep in sync with k8s/base/*.yaml.
 *
 * The one entry with no corresponding base file is the ConfigMap generated
 * by the production overlay's configMapGenerator (agentweaver-runtime-config)
 * -- it is applied as part of the identity/RBAC/quota/PVC group, before
 * anything that reads it via configMapKeyRef.
 */
export const FILE_RESOURCES = Object.freeze({
  "namespace.yaml": [{ kind: "Namespace", name: "agentweaver" }],
  "_agentweaver-runtime-config.yaml": [{ kind: "ConfigMap", name: "agentweaver-runtime-config" }],
  "serviceaccount-api.yaml": [{ kind: "ServiceAccount", name: "agentweaver-api" }],
  "serviceaccount-worker.yaml": [{ kind: "ServiceAccount", name: "agentweaver-worker" }],
  "serviceaccount-agenthost.yaml": [{ kind: "ServiceAccount", name: "agentweaver-agent-host" }],
  "secret-provider-class.yaml": [
    { kind: "SecretProviderClass", name: "agentweaver-secrets" },
    { kind: "SecretProviderClass", name: "agentweaver-user-tokens" },
  ],
  "rbac-api.yaml": [
    { kind: "Role", name: "agentweaver-api-sandbox" },
    { kind: "RoleBinding", name: "agentweaver-api-sandbox" },
    { kind: "Role", name: "agentweaver-worker-sandbox" },
    { kind: "RoleBinding", name: "agentweaver-worker-sandbox" },
  ],
  "vap-sandbox-exec.yaml": [
    { kind: "ValidatingAdmissionPolicy", name: "sandbox-exec-only" },
    { kind: "ValidatingAdmissionPolicyBinding", name: "sandbox-exec-only-binding" },
  ],
  "quota.yaml": [
    { kind: "ResourceQuota", name: "agentweaver-quota" },
    { kind: "LimitRange", name: "agentweaver-default-limits" },
    { kind: "PodDisruptionBudget", name: "agentweaver-api-pdb" },
    { kind: "PodDisruptionBudget", name: "agentweaver-mcp-pdb" },
    { kind: "PodDisruptionBudget", name: "agentweaver-frontend-pdb" },
  ],
  "storageclass-workspace.yaml": [{ kind: "StorageClass", name: "azurefile-csi-premium-uid1000" }],
  "pvc-data.yaml": [{ kind: "PersistentVolumeClaim", name: "agentweaver-data" }],
  "pvc-workspace.yaml": [{ kind: "PersistentVolumeClaim", name: "agentweaver-workspace" }],
  "networkpolicy-default-deny.yaml": [
    { kind: "NetworkPolicy", name: "default-deny-ingress" },
    { kind: "NetworkPolicy", name: "default-deny-egress-apps" },
    { kind: "NetworkPolicy", name: "allow-app-dns-egress" },
    { kind: "NetworkPolicy", name: "allow-app-internal-egress" },
    { kind: "NetworkPolicy", name: "allow-app-external-https-egress" },
    { kind: "NetworkPolicy", name: "allow-gateway-to-api" },
    { kind: "NetworkPolicy", name: "allow-mcp-to-api" },
    { kind: "NetworkPolicy", name: "allow-gateway-to-frontend" },
  ],
  "networkpolicy-mcp.yaml": [
    { kind: "NetworkPolicy", name: "allow-gateway-to-mcp" },
    { kind: "NetworkPolicy", name: "allow-api-to-mcp" },
  ],
  "networkpolicy-sandbox.yaml": [
    { kind: "NetworkPolicy", name: "sandbox-deny-ingress" },
    { kind: "NetworkPolicy", name: "sandbox-allow-preview-ingress" },
    { kind: "NetworkPolicy", name: "sandbox-egress-allowlist" },
  ],
  "networkpolicy-agenthost.yaml": [
    { kind: "NetworkPolicy", name: "allow-worker-to-agenthost-a2a" },
    { kind: "NetworkPolicy", name: "allow-agenthost-to-api" },
  ],
  "networkpolicy-agenthost-api-egress.yaml": [{ kind: "NetworkPolicy", name: "allow-api-agenthost-egress" }],
  "networkpolicy-agenthost-egress.yaml": [{ kind: "NetworkPolicy", name: "agenthost-egress-allowlist" }],
  "cilium-network-policy-sandbox.yaml": [{ kind: "CiliumNetworkPolicy", name: "sandbox-egress-fqdn-allowlist" }],
  "serviceentry-telemetry.yaml": [{ kind: "CiliumNetworkPolicy", name: "agentweaver-app-egress-fqdn-allowlist" }],
  "networkpolicy-postgres-egress.yaml": [{ kind: "NetworkPolicy", name: "allow-api-postgres-egress" }],
  "networkpolicy-worker.yaml": [
    { kind: "NetworkPolicy", name: "default-deny-egress-worker" },
    { kind: "NetworkPolicy", name: "allow-worker-dns-egress" },
    { kind: "NetworkPolicy", name: "allow-worker-internal-egress" },
    { kind: "NetworkPolicy", name: "allow-worker-external-https-egress" },
    { kind: "NetworkPolicy", name: "allow-worker-agenthost-egress" },
    { kind: "NetworkPolicy", name: "allow-worker-postgres-egress" },
    { kind: "NetworkPolicy", name: "allow-worker-otel-egress" },
  ],
  "configmap-agenthost.yaml": [{ kind: "ConfigMap", name: "agenthost-config" }],
  "api-service.yaml": [{ kind: "Service", name: "agentweaver-api" }],
  "frontend-service.yaml": [{ kind: "Service", name: "agentweaver-frontend" }],
  "mcp-service.yaml": [{ kind: "Service", name: "agentweaver-mcp" }],
  "gateway.yaml": [{ kind: "Gateway", name: "agentweaver-gateway" }],
  "gateway-preview.yaml": [{ kind: "Gateway", name: "agentweaver-preview-gateway" }],
  "httproute-api.yaml": [{ kind: "HTTPRoute", name: "agentweaver-api-route" }],
  "httproute-frontend.yaml": [{ kind: "HTTPRoute", name: "agentweaver-frontend-route" }],
  "mcp-httproute.yaml": [{ kind: "HTTPRoute", name: "agentweaver-mcp-route" }],
  "sandbox-template-agenthost.yaml": [{ kind: "SandboxTemplate", name: "agentweaver-agent-host" }],
  "sandbox-warmpool-agenthost.yaml": [{ kind: "SandboxWarmPool", name: "agentweaver-agent-host" }],
  "api-deployment.yaml": [{ kind: "Deployment", name: "agentweaver-api" }],
  "frontend-deployment.yaml": [{ kind: "Deployment", name: "agentweaver-frontend" }],
  "mcp-deployment.yaml": [{ kind: "Deployment", name: "agentweaver-mcp" }],
  "worker-deployment.yaml": [{ kind: "Deployment", name: "agentweaver-worker" }],
  "worker-hpa.yaml": [
    { kind: "HorizontalPodAutoscaler", name: "agentweaver-worker-hpa" },
    { kind: "PodDisruptionBudget", name: "agentweaver-worker-pdb" },
  ],
});

/**
 * Builds the agentweaver-runtime-config ConfigMap literal set from a
 * resolved variable set (cfg plus the live-derived HOST, PREVIEW_HOSTNAME,
 * etc. fields -- see steps/30-deploy.mjs's renderVars). Key names match the
 * `data.<KEY>` fieldPaths referenced by the overlay's `replacements` block
 * and the `configMapKeyRef.key` values in k8s/base/*.yaml.
 *
 * @param {Record<string, unknown>} vars
 * @returns {Record<string, string>}
 */
export function buildRuntimeConfigLiterals(vars) {
  const str = (v) => (v === undefined || v === null ? "" : String(v));
  const host = str(vars.HOST);
  return {
    HOST: host,
    PREVIEW_HOSTNAME: str(vars.PREVIEW_HOSTNAME),
    IDENTITY_CLIENT_ID: str(vars.IDENTITY_CLIENT_ID),
    AGENTHOST_IDENTITY_CLIENT_ID: str(vars.AGENTHOST_IDENTITY_CLIENT_ID),
    KEYVAULT_NAME: str(vars.KEYVAULT_NAME),
    TENANT_ID: str(vars.TENANT_ID),
    OAUTH_ISSUER: host ? `https://${host}` : "",
    OAUTH_AUDIENCE: host ? `https://${host}/mcp` : "",
    GITHUB_CALLBACK_URL: host ? `https://${host}/auth/github/callback` : "",
    GITHUB_FRONTEND_URL: host ? `https://${host}/` : "",
    TOKEN_STORE_KEYVAULT_URI: vars.KEYVAULT_NAME ? `https://${vars.KEYVAULT_NAME}.vault.azure.net` : "",
    AGENTHOST_KEYVAULT_URI: str(vars.AGENTHOST_KEYVAULT_URI),
    APPINSIGHTS_WORKSPACE_ID: str(vars.APPINSIGHTS_WORKSPACE_ID),
    SANDBOX_PREVIEW_ZONE_SUFFIX: str(vars.SANDBOX_PREVIEW_ZONE_SUFFIX),
  };
}

/**
 * Builds the `images:` transformer entries -- replaces the old envsubst
 * ACR_LOGIN_SERVER/IMAGE_TAG/AGENTHOST_IMAGE_TAG substitution.
 * @param {Record<string, unknown>} vars
 */
export function buildImageEntries(vars) {
  const registry = vars.ACR_LOGIN_SERVER;
  return [
    { name: IMAGE_NAMES.api, newName: `${registry}/agentweaver-api`, newTag: vars.IMAGE_TAG },
    { name: IMAGE_NAMES.frontend, newName: `${registry}/agentweaver-frontend`, newTag: vars.IMAGE_TAG },
    { name: IMAGE_NAMES.mcp, newName: `${registry}/agentweaver-mcp`, newTag: vars.IMAGE_TAG },
    { name: IMAGE_NAMES.agentHost, newName: `${registry}/agentweaver-agent-host`, newTag: vars.AGENTHOST_IMAGE_TAG },
  ];
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Rewrites the committed overlay kustomization.yaml text in place: swaps
 * each `images:` entry's `newName`/`newTag` and each configMapGenerator
 * literal's value for the real ones resolved from `vars`. Every placeholder
 * this function targets is quoted in the committed file (see
 * k8s/overlays/production/kustomization.yaml), so `JSON.stringify()`
 * produces a safe, equivalent YAML double-quoted scalar for the
 * replacement.
 *
 * @param {string} kustomizationText Committed overlay kustomization.yaml text.
 * @param {Record<string, unknown>} vars Resolved variables (cfg + live HOST/etc).
 * @returns {string}
 */
export function rewriteOverlayKustomization(kustomizationText, vars) {
  let out = kustomizationText;

  for (const image of buildImageEntries(vars)) {
    const nameRe = escapeRegExp(image.name);
    const blockRe = new RegExp(`(- name: ${nameRe}\\r?\\n\\s*newName: ).*(\\r?\\n\\s*newTag: ).*`);
    out = out.replace(blockRe, (_match, prefix1, prefix2) => `${prefix1}${image.newName}${prefix2}${JSON.stringify(String(image.newTag))}`);
  }

  const literals = buildRuntimeConfigLiterals(vars);
  for (const [key, value] of Object.entries(literals)) {
    const fullLineRe = new RegExp(`^(\\s*- )"${key}=[^"]*"\\s*$`, "m");
    out = out.replace(fullLineRe, (_match, indent) => `${indent}${JSON.stringify(`${key}=${value}`)}`);
  }

  return out;
}

/**
 * Copies k8s/base and k8s/overlays/<OVERLAY_NAME> into `scratchDir`, then
 * rewrites the copied overlay's kustomization.yaml with the real resolved
 * values. Returns the scratch overlay directory path to build/apply from.
 *
 * @param {Record<string, unknown>} vars
 * @param {{ repoRoot: string, scratchDir: string, fs?: typeof fs }} opts
 */
export function writeOverlay(vars, { repoRoot, scratchDir, fs: fsImpl = fs }) {
  const baseSrc = path.join(repoRoot, "k8s", "base");
  const overlaySrc = path.join(repoRoot, "k8s", "overlays", OVERLAY_NAME);
  const baseDst = path.join(scratchDir, "base");
  const overlayDst = path.join(scratchDir, "overlays", OVERLAY_NAME);

  fsImpl.rmSync(scratchDir, { recursive: true, force: true });
  fsImpl.mkdirSync(baseDst, { recursive: true });
  fsImpl.mkdirSync(overlayDst, { recursive: true });

  for (const fname of fsImpl.readdirSync(baseSrc)) {
    fsImpl.writeFileSync(path.join(baseDst, fname), fsImpl.readFileSync(path.join(baseSrc, fname), "utf8"));
  }
  for (const fname of fsImpl.readdirSync(overlaySrc)) {
    const content = fsImpl.readFileSync(path.join(overlaySrc, fname), "utf8");
    fsImpl.writeFileSync(
      path.join(overlayDst, fname),
      fname === "kustomization.yaml" ? rewriteOverlayKustomization(content, vars) : content,
    );
  }

  return overlayDst;
}

/**
 * Splits `kubectl kustomize`'s combined multi-document YAML output into
 * `{ kind, name, text }` docs, using each chunk's `metadata:` block to find
 * `name` regardless of kustomize's alphabetical field ordering.
 * @param {string} builtYaml
 * @returns {{kind: string, name: string, text: string}[]}
 */
export function parseBuiltDocs(builtYaml) {
  return builtYaml
    .split(/\r?\n---\r?\n/)
    .map((chunk) => chunk.trim())
    .filter(Boolean)
    .map((chunk) => {
      const kindMatch = chunk.match(/^kind:\s*(.+)$/m);
      const metadataBlockMatch = chunk.match(/^metadata:\n((?:[ \t].*\n?)*)/m);
      const nameMatch = metadataBlockMatch?.[1]?.match(/^\s*name:\s*(.+)\s*$/m);
      return {
        kind: kindMatch ? kindMatch[1].trim() : "",
        name: nameMatch ? nameMatch[1].trim().replace(/^["']|["']$/g, "") : "",
        text: chunk,
      };
    });
}

/**
 * Re-groups `docs` (from parseBuiltDocs) back into the same per-original-
 * filename manifest text steps/30-deploy.mjs has always applied, using
 * FILE_RESOURCES to know which kind/name pairs belong to each filename.
 * Throws if a filename's expected resource is missing from `docs` (fail
 * fast rather than silently apply a partial/empty manifest).
 *
 * @param {{kind: string, name: string, text: string}[]} docs
 * @param {string} filename
 * @returns {string}
 */
export function manifestForFilename(docs, filename) {
  const wanted = FILE_RESOURCES[filename];
  if (!wanted) {
    throw new Error(`kustomize.mjs: no FILE_RESOURCES entry for '${filename}'`);
  }
  const parts = wanted.map(({ kind, name }) => {
    const doc = docs.find((d) => d.kind === kind && d.name === name);
    if (!doc) {
      throw new Error(`kustomize.mjs: kustomize build did not produce ${kind}/${name} (expected for ${filename})`);
    }
    return doc.text;
  });
  return parts.join("\n---\n");
}
