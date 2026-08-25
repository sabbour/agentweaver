// deploy-render.test.mjs -- Unit tests for lib/kustomize.mjs, the Kustomize-
// based replacement for the old envsubst renderer as far as k8s manifest
// rendering goes (see that module's header comment for the full pipeline).
//
// Covers: buildImageEntries()/buildRuntimeConfigLiterals() derive the right
// values from a resolved variable set; rewriteOverlayKustomization() safely
// rewrites the committed overlay's images:/configMapGenerator placeholders;
// writeOverlay() produces a buildable scratch overlay; parseBuiltDocs() and
// manifestForFilename() correctly re-group a real `kubectl kustomize` build
// back into the same per-file manifests steps/30-deploy.mjs applies.
//
// These tests shell out to the real `kubectl kustomize` (kubectl's built-in
// Kustomize support -- no separate `kustomize` binary required) against the
// real k8s/base + k8s/overlays/production directories, so they double as a
// "does the checked-in overlay still build" regression check.

import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import * as execDefault from "../lib/exec.mjs";
import {
  FILE_RESOURCES,
  IMAGE_NAMES,
  buildImageEntries,
  buildRuntimeConfigLiterals,
  rewriteOverlayKustomization,
  writeOverlay,
  parseBuiltDocs,
  manifestForFilename,
  isPublicPostgresAccess,
  postgresFqdn,
  buildPostgresFqdnPolicy,
} from "../lib/kustomize.mjs";
import { DEFAULT_REPO_ROOT } from "../steps/30-deploy.mjs";

// Fixed, realistic input variables -- distinct values for every field so a
// successful build actually PROVES replacement/substitution fired, rather
// than merely producing structurally-valid YAML that happens to match a
// placeholder by coincidence.
const VARS = {
  HOST: "agentweaver.abc123def456.westus2.staging.aksapp.io",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  IMAGE_TAG: "v0.9.71",
  AGENTHOST_IMAGE_TAG: "v0.9.71-agenthost",
  IDENTITY_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  AGENTHOST_IDENTITY_CLIENT_ID: "99999999-8888-7777-6666-555555555555",
  KEYVAULT_NAME: "test-kv-fixture",
  AGENTHOST_KEYVAULT_URI: "https://test-kv-fixture.vault.azure.net/",
  TENANT_ID: "66666666-7777-8888-9999-000000000000",
  PREVIEW_HOSTNAME: "*.abc123def456.westus2.staging.aksapp.io",
  PREVIEW_TLS_SECRET: "agentweaver-tls",
  SANDBOX_PREVIEW_ENABLED: "true",
  SANDBOX_PREVIEW_ZONE_SUFFIX: "abc123def456.westus2.staging.aksapp.io",
  APPINSIGHTS_WORKSPACE_ID: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
};

test("buildImageEntries() derives the 4 images: entries from ACR_LOGIN_SERVER/IMAGE_TAG/AGENTHOST_IMAGE_TAG", () => {
  const entries = buildImageEntries(VARS);
  assert.equal(entries.length, 4);
  assert.deepEqual(
    entries.map((e) => e.name),
    [IMAGE_NAMES.api, IMAGE_NAMES.frontend, IMAGE_NAMES.mcp, IMAGE_NAMES.agentHost],
  );
  assert.deepEqual(
    entries.map((e) => e.newName),
    [
      "agentweaverregistry.azurecr.io/agentweaver-api",
      "agentweaverregistry.azurecr.io/agentweaver-frontend",
      "agentweaverregistry.azurecr.io/agentweaver-mcp",
      "agentweaverregistry.azurecr.io/agentweaver-agent-host",
    ],
  );
  assert.deepEqual(
    entries.map((e) => e.newTag),
    ["v0.9.71", "v0.9.71", "v0.9.71", "v0.9.71-agenthost"],
  );
});

test("buildRuntimeConfigLiterals() composites full URLs from HOST and passes through the rest", () => {
  const literals = buildRuntimeConfigLiterals(VARS);
  assert.equal(literals.OAUTH_ISSUER, "https://agentweaver.abc123def456.westus2.staging.aksapp.io");
  assert.equal(literals.OAUTH_AUDIENCE, "https://agentweaver.abc123def456.westus2.staging.aksapp.io/mcp");
  assert.equal(
    literals.GITHUB_CALLBACK_URL,
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/github/callback",
  );
  assert.equal(literals.TOKEN_STORE_KEYVAULT_URI, "https://test-kv-fixture.vault.azure.net");
  assert.equal(literals.AGENTHOST_KEYVAULT_URI, "https://test-kv-fixture.vault.azure.net/");
  assert.equal(literals.IDENTITY_CLIENT_ID, "11111111-2222-3333-4444-555555555555");
  assert.equal(literals.AGENTHOST_IDENTITY_CLIENT_ID, "99999999-8888-7777-6666-555555555555");
  assert.equal(literals.APPINSIGHTS_WORKSPACE_ID, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
  assert.equal(literals.SANDBOX_PREVIEW_ZONE_SUFFIX, "abc123def456.westus2.staging.aksapp.io");
});

test("buildRuntimeConfigLiterals() passes GITHUB_ALLOWED_ORG through, defaulting to microsoft", () => {
  // Config-driven, non-committed value: falls back to the committed default when unset...
  assert.equal(buildRuntimeConfigLiterals(VARS).GITHUB_ALLOWED_ORG, "microsoft");
  // ...and passes a supplied (possibly multi-org) value through verbatim.
  assert.equal(
    buildRuntimeConfigLiterals({ ...VARS, GITHUB_ALLOWED_ORG: "microsoft,contoso" }).GITHUB_ALLOWED_ORG,
    "microsoft,contoso",
  );
});

test("buildRuntimeConfigLiterals() derives ENTRA_REDIRECT_URI from HOST and defaults AUTH_MODE to GitHubLegacy", () => {
  const literals = buildRuntimeConfigLiterals(VARS);
  assert.equal(literals.AUTH_MODE, "GitHubLegacy");
  assert.equal(literals.ENTRA_CLIENT_ID, "");
  assert.equal(literals.ENTRA_TENANT_ID, "");
  assert.equal(
    literals.ENTRA_REDIRECT_URI,
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/entra/callback",
  );
});

test("buildRuntimeConfigLiterals() passes AUTH_MODE/ENTRA_CLIENT_ID/ENTRA_TENANT_ID through when set", () => {
  const literals = buildRuntimeConfigLiterals({
    ...VARS,
    AUTH_MODE: "Entra",
    ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
    ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000",
  });
  assert.equal(literals.AUTH_MODE, "Entra");
  assert.equal(literals.ENTRA_CLIENT_ID, "11111111-2222-3333-4444-555555555555");
  assert.equal(literals.ENTRA_TENANT_ID, "66666666-7777-8888-9999-000000000000");
});

test("buildRuntimeConfigLiterals() throws when AUTH_MODE=Entra but ENTRA_CLIENT_ID is missing", () => {
  assert.throws(
    () => buildRuntimeConfigLiterals({ ...VARS, AUTH_MODE: "Entra", ENTRA_CLIENT_ID: "", ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000" }),
    /ENTRA_CLIENT_ID or ENTRA_TENANT_ID is empty/,
  );
});

test("buildRuntimeConfigLiterals() throws when AUTH_MODE=Entra but ENTRA_TENANT_ID is missing", () => {
  assert.throws(
    () => buildRuntimeConfigLiterals({ ...VARS, AUTH_MODE: "Entra", ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555", ENTRA_TENANT_ID: "" }),
    /ENTRA_CLIENT_ID or ENTRA_TENANT_ID is empty/,
  );
});

test("buildRuntimeConfigLiterals() does NOT throw when AUTH_MODE=GitHubLegacy with empty Entra fields", () => {
  assert.doesNotThrow(
    () => buildRuntimeConfigLiterals({ ...VARS, AUTH_MODE: "GitHubLegacy", ENTRA_CLIENT_ID: "", ENTRA_TENANT_ID: "" }),
  );
});

test("rewriteOverlayKustomization() rewrites every images: entry and configMapGenerator literal, leaving structure intact", () => {
  const overlayPath = path.join(DEFAULT_REPO_ROOT, "k8s", "overlays", "production", "kustomization.yaml");
  const original = fs.readFileSync(overlayPath, "utf8");
  const rewritten = rewriteOverlayKustomization(original, VARS);

  assert.match(rewritten, /newName: agentweaverregistry\.azurecr\.io\/agentweaver-api\s*\n\s*newTag: "v0\.9\.71"/);
  assert.match(rewritten, /newName: agentweaverregistry\.azurecr\.io\/agentweaver-agent-host\s*\n\s*newTag: "v0\.9\.71-agenthost"/);
  assert.match(rewritten, /- "HOST=agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io"/);
  assert.match(rewritten, /- "PREVIEW_HOSTNAME=\*\.abc123def456\.westus2\.staging\.aksapp\.io"/);
  assert.match(rewritten, /- "IDENTITY_CLIENT_ID=11111111-2222-3333-4444-555555555555"/);
  assert.match(rewritten, /- "AGENTHOST_IDENTITY_CLIENT_ID=99999999-8888-7777-6666-555555555555"/);
  assert.match(rewritten, /- "TENANT_ID=66666666-7777-8888-9999-000000000000"/);
  assert.match(rewritten, /- "AUTH_MODE=GitHubLegacy"/);
  assert.match(rewritten, /- "ENTRA_CLIENT_ID="/);
  assert.match(rewritten, /- "ENTRA_TENANT_ID="/);
  assert.match(
    rewritten,
    /- "ENTRA_REDIRECT_URI=https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io\/auth\/entra\/callback"/,
  );
  // Untouched structural content (resources:/replacements: blocks) should survive verbatim.
  assert.match(rewritten, /resources:\s*\n\s*- \.\.\/\.\.\/base/);
  assert.match(rewritten, /replacements:/);
  // No leftover "latest"/"changeme" placeholders for the fields we targeted.
  assert.doesNotMatch(rewritten, /newTag: "latest"/);
});

test("writeOverlay() + kubectl kustomize builds cleanly and every resource resolves to real (not placeholder) values", async (t) => {
  const scratchDir = path.join(DEFAULT_REPO_ROOT, "scripts", "azure", "tests", ".scratch-deploy-render");
  t.after(() => fs.rmSync(scratchDir, { recursive: true, force: true }));

  const overlayDir = writeOverlay(VARS, { repoRoot: DEFAULT_REPO_ROOT, scratchDir });
  const { stdout: builtYaml, code } = await execDefault.capture("kubectl", ["kustomize", overlayDir]);
  assert.equal(code, 0);

  assert.match(builtYaml, /image: agentweaverregistry\.azurecr\.io\/agentweaver-api:v0\.9\.71\b/);
  assert.match(builtYaml, /image: agentweaverregistry\.azurecr\.io\/agentweaver-agent-host:v0\.9\.71-agenthost/);
  assert.match(builtYaml, /hostname: agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io/);
  assert.match(builtYaml, /hostname: '\*\.abc123def456\.westus2\.staging\.aksapp\.io'/);
  assert.match(builtYaml, /clientID: 11111111-2222-3333-4444-555555555555/);
  assert.match(builtYaml, /keyvaultName: test-kv-fixture/);
  assert.match(builtYaml, /tenantId: 66666666-7777-8888-9999-000000000000/);
  assert.match(builtYaml, /azure\.workload\.identity\/client-id: 11111111-2222-3333-4444-555555555555/);
  assert.match(builtYaml, /azure\.workload\.identity\/tenant-id: 66666666-7777-8888-9999-000000000000/);
  // Auth__Mode/Auth__Entra__* env vars reference the ConfigMap keys correctly (configMapKeyRef).
  // kubectl kustomize serializes valueFrom.configMapKeyRef fields alphabetically (key: before name:).
  assert.match(builtYaml, /name: Auth__Mode\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: AUTH_MODE\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__Entra__ClientId\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_CLIENT_ID\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__Entra__TenantId\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_TENANT_ID\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__Entra__RedirectUri\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_REDIRECT_URI\s*\n\s*name: agentweaver-runtime-config/);
  assert.doesNotMatch(builtYaml, /name: Auth__Entra__ClientSecret/);
  assert.doesNotMatch(builtYaml, /changeme/);
  assert.doesNotMatch(builtYaml, /example\.com/);

  const docs = parseBuiltDocs(builtYaml);
  // issue #471: the AgentHost ServiceAccount must be wired to the DEDICATED KV-less identity, while
  // the API/MCP ServiceAccounts keep the KV-privileged API identity.
  const agentHostSaManifest = manifestForFilename(docs, "serviceaccount-agenthost.yaml");
  assert.match(
    agentHostSaManifest,
    /azure\.workload\.identity\/client-id: 99999999-8888-7777-6666-555555555555/,
    "agent-host ServiceAccount must use the dedicated AGENTHOST_IDENTITY_CLIENT_ID",
  );
  assert.doesNotMatch(
    agentHostSaManifest,
    /azure\.workload\.identity\/client-id: 11111111-2222-3333-4444-555555555555/,
    "agent-host ServiceAccount must NOT use the KV-privileged API identity",
  );
  const sandboxTemplate = manifestForFilename(docs, "sandbox-template-agenthost.yaml");
  assert.match(
    sandboxTemplate,
    /name: NODE_OPTIONS\s*\n\s*value: --max-old-space-size=1024[\s\S]*?name: agentweaver-agent-host[\s\S]*?resources:\s*\n\s*limits:\s*\n\s*cpu: 800m\s*\n\s*ephemeral-storage: 4Gi\s*\n\s*memory: 2Gi\s*\n\s*requests:\s*\n\s*cpu: 300m\s*\n\s*ephemeral-storage: 1Gi\s*\n\s*memory: 1Gi/,
    "AgentHost must pass the preview Node heap cap and retain its explicit resource reservation",
  );
  assert.match(
    sandboxTemplate,
    /name: agentweaver-exec[\s\S]*?resources:\s*\n\s*limits:\s*\n\s*cpu: 1200m\s*\n\s*ephemeral-storage: 4Gi\s*\n\s*memory: 2Gi\s*\n\s*requests:\s*\n\s*cpu: 700m\s*\n\s*ephemeral-storage: 1Gi\s*\n\s*memory: 1Gi/,
    "The executor that runs previews must retain explicit resource reservation and limits",
  );
  const mcpDeployment = manifestForFilename(docs, "mcp-deployment.yaml");
  assert.match(
    mcpDeployment,
    /name: Auth__Mode\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: AUTH_MODE\s*\n\s*name: agentweaver-runtime-config/,
    "MCP must receive the deployment auth mode so Entra bearer validation is enabled only in Entra mode",
  );
  assert.match(
    mcpDeployment,
    /name: Auth__Entra__ClientId\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_CLIENT_ID\s*\n\s*name: agentweaver-runtime-config/,
  );
  assert.match(
    mcpDeployment,
    /name: Auth__Entra__TenantId\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_TENANT_ID\s*\n\s*name: agentweaver-runtime-config/,
  );
  const apiSaManifest = manifestForFilename(docs, "serviceaccount-api.yaml");
  assert.match(
    apiSaManifest,
    /azure\.workload\.identity\/client-id: 11111111-2222-3333-4444-555555555555/,
    "api ServiceAccount must keep the API identity",
  );

  // Every FILE_RESOURCES entry must resolve to a real document in the build
  // -- proves no resource was lost in the base/overlay restructuring.
  for (const [filename, wanted] of Object.entries(FILE_RESOURCES)) {
    const manifest = manifestForFilename(docs, filename);
    for (const { kind, name } of wanted) {
      assert.match(manifest, new RegExp(`kind: ${kind}\\b`), `${filename} should contain ${kind}/${name}`);
      assert.match(manifest, new RegExp(`name: ${name}\\b`), `${filename} should contain ${kind}/${name}`);
    }
  }

  // Regression: the overlay's configMapGenerator-produced ConfigMap (and any
  // other namespace-scoped resource) must carry `namespace: agentweaver`.
  // This was previously missing because the overlay's own kustomization.yaml
  // had no top-level `namespace:` transformer -- base's transformer only
  // applies to resources pulled in via `resources: - ../../base`, not to
  // generators declared directly in the overlay. Left unset, kubectl apply
  // falls back to whatever namespace the current context defaults to
  // (typically "default"), silently breaking every configMapKeyRef that
  // expects this ConfigMap to live in the agentweaver namespace.
  const CLUSTER_SCOPED_KINDS = new Set(["Namespace", "StorageClass"]);
  for (const doc of docs) {
    if (CLUSTER_SCOPED_KINDS.has(doc.kind) || !doc.kind) continue;
    assert.match(
      doc.text,
      /^\s*namespace: agentweaver\s*$/m,
      `${doc.kind}/${doc.name} must be namespaced to 'agentweaver'`,
    );
  }

  // Regression for #580: every built namespaced/cluster-scoped document must
  // belong to some FILE_RESOURCES group, or deploy-from-local/release can
  // silently omit it even though kustomize produced it.
  const accountedFor = new Set(
    Object.values(FILE_RESOURCES)
      .flat()
      .map(({ kind, name }) => `${kind}/${name}`),
  );
  for (const doc of docs) {
    assert.ok(
      accountedFor.has(`${doc.kind}/${doc.name}`),
      `kustomize build produced ungrouped resource ${doc.kind}/${doc.name}; add it to FILE_RESOURCES`,
    );
  }
});

test("manifestForFilename() throws a clear error for an unknown filename (fail-fast, no silent partial applies)", () => {
  assert.throws(() => manifestForFilename([], "not-a-real-file.yaml"), /no FILE_RESOURCES entry/);
});

test("manifestForFilename() throws when a resource is missing from the build (fail-fast)", () => {
  const docs = [{ kind: "Namespace", name: "wrong-name", text: "kind: Namespace\nmetadata:\n  name: wrong-name\n" }];
  assert.throws(() => manifestForFilename(docs, "namespace.yaml"), /did not produce Namespace\/agentweaver/);
});

// --- Postgres egress policy access-mode branching (bug found live in v0.16.0) ---
//
// PR #683 added --postgres-access-mode public, but the generated Postgres egress
// NetworkPolicies kept templating the PRIVATE delegated-subnet CIDR, which blocked
// every pod -> Postgres connection in public mode (Npgsql timeouts) even with the
// Azure-side firewall correctly configured. Private mode must keep the ipBlock
// policies unchanged; public mode must emit FQDN-based CiliumNetworkPolicies.

const PG_DOCS = [
  { kind: "NetworkPolicy", name: "allow-api-postgres-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-api-postgres-egress\n  namespace: agentweaver\nspec:\n  egress:\n  - to:\n    - ipBlock:\n        cidr: 10.225.0.0/28\n" },
  { kind: "NetworkPolicy", name: "default-deny-egress-worker", text: "kind: NetworkPolicy\nmetadata:\n  name: default-deny-egress-worker\n" },
  { kind: "NetworkPolicy", name: "allow-worker-dns-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-dns-egress\n" },
  { kind: "NetworkPolicy", name: "allow-worker-internal-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-internal-egress\n" },
  { kind: "NetworkPolicy", name: "allow-worker-external-https-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-external-https-egress\n" },
  { kind: "NetworkPolicy", name: "allow-worker-agenthost-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-agenthost-egress\n" },
  { kind: "NetworkPolicy", name: "allow-worker-postgres-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-postgres-egress\n  namespace: agentweaver\nspec:\n  egress:\n  - to:\n    - ipBlock:\n        cidr: 10.225.0.0/28\n" },
  { kind: "NetworkPolicy", name: "allow-worker-otel-egress", text: "kind: NetworkPolicy\nmetadata:\n  name: allow-worker-otel-egress\n" },
];

const PG_VARS_PRIVATE = { PG_ACCESS_MODE: "private", PG_SERVER_NAME: "agentweaver-pg" };
const PG_VARS_PUBLIC = { PG_ACCESS_MODE: "public", PG_SERVER_NAME: "agentweaver-pg-eastus2" };

test("isPublicPostgresAccess(): only 'public' (case/space tolerant) opts into FQDN egress", () => {
  assert.equal(isPublicPostgresAccess({ PG_ACCESS_MODE: "public" }), true);
  assert.equal(isPublicPostgresAccess({ PG_ACCESS_MODE: " Public " }), true);
  assert.equal(isPublicPostgresAccess({ PG_ACCESS_MODE: "private" }), false);
  assert.equal(isPublicPostgresAccess({}), false);
  assert.equal(isPublicPostgresAccess(), false);
});

test("postgresFqdn(): derives <server>.postgres.database.azure.com and fails loudly when unset", () => {
  assert.equal(postgresFqdn({ PG_SERVER_NAME: "agentweaver-pg" }), "agentweaver-pg.postgres.database.azure.com");
  assert.throws(() => postgresFqdn({}), /PG_SERVER_NAME is required/);
});

test("manifestForFilename(): private mode keeps the ipBlock Postgres egress policies unchanged", () => {
  const api = manifestForFilename(PG_DOCS, "networkpolicy-postgres-egress.yaml", { vars: PG_VARS_PRIVATE });
  assert.match(api, /name: allow-api-postgres-egress\b/);
  assert.match(api, /cidr: 10\.225\.0\.0\/28/);
  assert.doesNotMatch(api, /CiliumNetworkPolicy/);

  const worker = manifestForFilename(PG_DOCS, "networkpolicy-worker.yaml", { vars: PG_VARS_PRIVATE });
  assert.match(worker, /name: allow-worker-postgres-egress\b/);
  assert.match(worker, /cidr: 10\.225\.0\.0\/28/);
  assert.doesNotMatch(worker, /CiliumNetworkPolicy/);

  // No vars at all behaves exactly like private mode (default PG_ACCESS_MODE).
  assert.equal(manifestForFilename(PG_DOCS, "networkpolicy-postgres-egress.yaml"), api);
});

test("manifestForFilename(): public mode swaps the ipBlock policy for a toFQDNs CiliumNetworkPolicy (api)", () => {
  const api = manifestForFilename(PG_DOCS, "networkpolicy-postgres-egress.yaml", { vars: PG_VARS_PUBLIC });
  assert.doesNotMatch(api, /cidr: 10\.225\.0\.0\/28/, "private subnet CIDR must not survive into public mode");
  assert.doesNotMatch(api, /name: allow-api-postgres-egress\n/, "the ipBlock NetworkPolicy must be dropped");
  assert.match(api, /kind: CiliumNetworkPolicy/);
  assert.match(api, /name: allow-api-postgres-egress-fqdn/);
  assert.match(api, /namespace: agentweaver/);
  assert.match(api, /app: agentweaver-api/);
  assert.match(api, /toFQDNs:\s*\n\s*- matchName: "agentweaver-pg-eastus2\.postgres\.database\.azure\.com"/);
  assert.match(api, /- port: "5432"\s*\n\s*protocol: TCP/);
});

test("manifestForFilename(): public mode swaps the worker ipBlock policy but keeps the other worker policies", () => {
  const worker = manifestForFilename(PG_DOCS, "networkpolicy-worker.yaml", { vars: PG_VARS_PUBLIC });
  assert.doesNotMatch(worker, /cidr: 10\.225\.0\.0\/28/);
  assert.doesNotMatch(worker, /name: allow-worker-postgres-egress\n/);
  assert.match(worker, /kind: CiliumNetworkPolicy/);
  assert.match(worker, /name: allow-worker-postgres-egress-fqdn/);
  assert.match(worker, /app: agentweaver-worker/);
  assert.match(worker, /toFQDNs:\s*\n\s*- matchName: "agentweaver-pg-eastus2\.postgres\.database\.azure\.com"/);
  assert.match(worker, /- port: "5432"\s*\n\s*protocol: TCP/);
  // The unrelated worker policies are untouched.
  for (const name of [
    "default-deny-egress-worker",
    "allow-worker-dns-egress",
    "allow-worker-internal-egress",
    "allow-worker-external-https-egress",
    "allow-worker-agenthost-egress",
    "allow-worker-otel-egress",
  ]) {
    assert.match(worker, new RegExp(`name: ${name}\\b`), `${name} must still be applied in public mode`);
  }
});

test("buildPostgresFqdnPolicy(): mirrors the existing FQDN allowlist style (kube-dns visibility rule)", () => {
  const yaml = buildPostgresFqdnPolicy({ name: "allow-api-postgres-egress-fqdn", app: "agentweaver-api" }, PG_VARS_PUBLIC);
  assert.match(yaml, /^apiVersion: cilium\.io\/v2$/m);
  assert.match(yaml, /app\.kubernetes\.io\/part-of: agentweaver/);
  // Cilium's FQDN proxy only learns addresses it sees resolved, so the policy
  // must carry the same kube-dns rule agentweaver-app-egress-fqdn-allowlist has.
  assert.match(yaml, /k8s-app: kube-dns/);
  assert.match(yaml, /rules:\s*\n\s*dns:\s*\n\s*- matchPattern: "\*"/);
});

test("manifestForFilename(): public mode leaves non-Postgres manifest groups untouched", () => {
  const docs = [{ kind: "Namespace", name: "agentweaver", text: "kind: Namespace\nmetadata:\n  name: agentweaver\n" }];
  assert.equal(
    manifestForFilename(docs, "namespace.yaml", { vars: PG_VARS_PUBLIC }),
    manifestForFilename(docs, "namespace.yaml", { vars: PG_VARS_PRIVATE }),
  );
});
