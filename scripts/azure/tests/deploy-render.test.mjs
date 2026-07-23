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
  KEYVAULT_NAME: "agentweaver-kv",
  AGENTHOST_KEYVAULT_URI: "https://agentweaver-kv.vault.azure.net/",
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
  assert.equal(literals.TOKEN_STORE_KEYVAULT_URI, "https://agentweaver-kv.vault.azure.net");
  assert.equal(literals.AGENTHOST_KEYVAULT_URI, "https://agentweaver-kv.vault.azure.net/");
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
  assert.match(builtYaml, /keyvaultName: agentweaver-kv/);
  assert.match(builtYaml, /tenantId: 66666666-7777-8888-9999-000000000000/);
  assert.match(builtYaml, /azure\.workload\.identity\/client-id: 11111111-2222-3333-4444-555555555555/);
  assert.match(builtYaml, /azure\.workload\.identity\/tenant-id: 66666666-7777-8888-9999-000000000000/);
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
});

test("manifestForFilename() throws a clear error for an unknown filename (fail-fast, no silent partial applies)", () => {
  assert.throws(() => manifestForFilename([], "not-a-real-file.yaml"), /no FILE_RESOURCES entry/);
});

test("manifestForFilename() throws when a resource is missing from the build (fail-fast)", () => {
  const docs = [{ kind: "Namespace", name: "wrong-name", text: "kind: Namespace\nmetadata:\n  name: wrong-name\n" }];
  assert.throws(() => manifestForFilename(docs, "namespace.yaml"), /did not produce Namespace\/agentweaver/);
});
