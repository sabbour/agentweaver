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
import {
  assertCopilotAppCallbackUrl,
  COPILOT_APP_CALLBACK_SUFFIX,
  DEFAULT_REPO_ROOT,
} from "../steps/30-deploy.mjs";

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
  ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000",
  ENTRA_ENTERPRISE_APP_OBJECT_ID: "77777777-8888-9999-0000-111111111111",
  OAUTH_TRUSTED_PROXY_NETWORKS: "10.244.0.0/16",
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

test("buildRuntimeConfigLiterals() wires canonical OpenIddict and Key Vault certificate settings", () => {
  const literals = buildRuntimeConfigLiterals(VARS);
  assert.equal(literals.KEYVAULT_URI, "https://test-kv-fixture.vault.azure.net");
  assert.equal(literals.AGENTHOST_KEYVAULT_URI, "https://test-kv-fixture.vault.azure.net/");
  assert.equal(literals.IDENTITY_CLIENT_ID, "11111111-2222-3333-4444-555555555555");
  assert.equal(literals.AGENTHOST_IDENTITY_CLIENT_ID, "99999999-8888-7777-6666-555555555555");
  assert.equal(literals.APPINSIGHTS_WORKSPACE_ID, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
  assert.equal(literals.SANDBOX_PREVIEW_ZONE_SUFFIX, "abc123def456.westus2.staging.aksapp.io");
  assert.equal(literals.OAUTH_PUBLIC_ORIGIN, "https://agentweaver.abc123def456.westus2.staging.aksapp.io");
  assert.equal(literals.OAUTH_TRUSTED_PROXY_NETWORKS, "10.244.0.0/16");
  assert.equal(literals.OAUTH_SIGNING_CERTIFICATE_NAME, "agentweaver-oauth-signing");
  assert.equal(literals.OAUTH_ENCRYPTION_CERTIFICATE_NAME, "agentweaver-oauth-encryption");
});

test("buildRuntimeConfigLiterals() derives public Entra URLs from HOST and defaults AUTH_MODE to Entra", () => {
  const literals = buildRuntimeConfigLiterals(VARS);
  assert.equal(literals.AUTH_MODE, "Entra");
  assert.equal(literals.ENTRA_CLIENT_ID, VARS.ENTRA_CLIENT_ID);
  assert.equal(literals.ENTRA_TENANT_ID, VARS.ENTRA_TENANT_ID);
  assert.equal(literals.ENTRA_ENTERPRISE_APP_OBJECT_ID, VARS.ENTRA_ENTERPRISE_APP_OBJECT_ID);
  assert.equal(
    literals.ENTRA_REDIRECT_URI,
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/entra/callback",
  );
  assert.equal(literals.ENTRA_FRONTEND_URL, "https://agentweaver.abc123def456.westus2.staging.aksapp.io");
  assert.equal(
    literals.COPILOT_APP_CALLBACK_URL,
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/github/copilot-app/callback",
  );
  assert.equal(
    literals.REPO_APP_CALLBACK_URL,
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/github/repo-app/callback",
  );
});

test("deployment contract accepts only the unified Copilot callback suffix without a trailing slash", () => {
  const callbackUrl =
    "https://agentweaver.abc123def456.westus2.staging.aksapp.io/auth/github/copilot-app/callback";
  assert.equal(assertCopilotAppCallbackUrl(callbackUrl), callbackUrl);
  assert.equal(COPILOT_APP_CALLBACK_SUFFIX, "/auth/github/copilot-app/callback");

  for (const invalidUrl of [
    `${callbackUrl}/`,
    "https://agentweaver.example.com/auth/github/platform-default-copilot/callback",
    "https://agentweaver.example.com/auth/github/copilot-app/callback?source=deploy",
  ]) {
    assert.throws(
      () => assertCopilotAppCallbackUrl(invalidUrl),
      /must end exactly with '\/auth\/github\/copilot-app\/callback' and have no trailing slash/,
    );
  }
});

test("buildRuntimeConfigLiterals() passes AUTH_MODE/ENTRA_CLIENT_ID/ENTRA_TENANT_ID through and allows an optional enterprise app object ID", () => {
  const literals = buildRuntimeConfigLiterals({
    ...VARS,
    AUTH_MODE: "Entra",
    ENTRA_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
    ENTRA_TENANT_ID: "66666666-7777-8888-9999-000000000000",
    ENTRA_ENTERPRISE_APP_OBJECT_ID: "",
  });
  assert.equal(literals.AUTH_MODE, "Entra");
  assert.equal(literals.ENTRA_CLIENT_ID, "11111111-2222-3333-4444-555555555555");
  assert.equal(literals.ENTRA_TENANT_ID, "66666666-7777-8888-9999-000000000000");
  assert.equal(literals.ENTRA_ENTERPRISE_APP_OBJECT_ID, "");
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

test("buildRuntimeConfigLiterals() requires Entra configuration", () => {
  assert.throws(
    () => buildRuntimeConfigLiterals({ ...VARS, ENTRA_CLIENT_ID: "", ENTRA_TENANT_ID: "" }),
    /ENTRA_CLIENT_ID or ENTRA_TENANT_ID is empty/,
  );
});

test("buildRuntimeConfigLiterals() requires a structurally valid public HOST for Entra", () => {
  for (const host of ["", "localhost:5000", "agentweaver.", "https://agentweaver.example.com", "agentweaver.example.com/path", "127.0.0.1"]) {
    assert.throws(
      () => buildRuntimeConfigLiterals({ ...VARS, HOST: host }),
      /requires a structurally valid public HOST/,
      `${host || "(empty)"} must not be used as an Entra origin`,
    );
  }
});

test("buildRuntimeConfigLiterals() preserves explicit local GitHubLegacy development configuration", () => {
  const literals = buildRuntimeConfigLiterals({ ...VARS, AUTH_MODE: "GitHubLegacy", HOST: "localhost:5000" });
  assert.equal(literals.ENTRA_REDIRECT_URI, "https://localhost:5000/auth/entra/callback");
  assert.equal(literals.ENTRA_FRONTEND_URL, "https://localhost:5000");
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
  assert.match(rewritten, /- "AUTH_MODE=Entra"/);
  assert.match(rewritten, /- "ENTRA_CLIENT_ID=11111111-2222-3333-4444-555555555555"/);
  assert.match(rewritten, /- "ENTRA_TENANT_ID=66666666-7777-8888-9999-000000000000"/);
  assert.match(rewritten, /- "ENTRA_ENTERPRISE_APP_OBJECT_ID=77777777-8888-9999-0000-111111111111"/);
  assert.match(
    rewritten,
    /- "ENTRA_REDIRECT_URI=https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io\/auth\/entra\/callback"/,
  );
  assert.match(
    rewritten,
    /- "ENTRA_FRONTEND_URL=https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io"/,
  );
  assert.match(
    rewritten,
    /- "COPILOT_APP_CALLBACK_URL=https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io\/auth\/github\/copilot-app\/callback"/,
  );
  assert.match(
    rewritten,
    /- "REPO_APP_CALLBACK_URL=https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io\/auth\/github\/repo-app\/callback"/,
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
  assert.match(builtYaml, /name: Auth__Entra__EnterpriseAppObjectId\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_ENTERPRISE_APP_OBJECT_ID\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__Entra__RedirectUri\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_REDIRECT_URI\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__Entra__FrontendUrl\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_FRONTEND_URL\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__OAuth__PublicOrigin\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: OAUTH_PUBLIC_ORIGIN\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__OAuth__ForwardedHeaders__TrustedNetworks\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: OAUTH_TRUSTED_PROXY_NETWORKS\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__OAuth__Certificates__SigningName\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: OAUTH_SIGNING_CERTIFICATE_NAME\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__CopilotApp__CallbackUrl\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: COPILOT_APP_CALLBACK_URL\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__RepoApp__CallbackUrl\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: REPO_APP_CALLBACK_URL\s*\n\s*name: agentweaver-runtime-config/);
  // Post-authorization browser redirect target for both GitHub Apps must reuse the same
  // production frontend URL as Entra, or the code falls back to its localhost:5173 default.
  assert.match(builtYaml, /name: Auth__CopilotApp__FrontendUrl\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_FRONTEND_URL\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__RepoApp__FrontendUrl\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: ENTRA_FRONTEND_URL\s*\n\s*name: agentweaver-runtime-config/);
  assert.match(builtYaml, /name: Auth__CopilotApp__Slug\s*\n\s*value: agentweaver-orchestrator-copilot/);
  assert.match(builtYaml, /name: Auth__RepoApp__PrivateKeySecretName\s*\n\s*value: repo-app-private-key/);
  assert.match(builtYaml, /name: Auth__CopilotApp__ClientId\s*\n\s*valueFrom:\s*\n\s*secretKeyRef:\s*\n\s*key: copilot-app-client-id\s*\n\s*name: agentweaver-secrets/);
  assert.match(builtYaml, /name: Auth__RepoApp__AppId\s*\n\s*valueFrom:\s*\n\s*secretKeyRef:\s*\n\s*key: repo-app-id\s*\n\s*name: agentweaver-secrets/);
  assert.match(builtYaml, /objectName: copilot-app-client-id[\s\S]*?objectName: copilot-app-client-secret[\s\S]*?objectName: repo-app-client-id[\s\S]*?objectName: repo-app-client-secret[\s\S]*?objectName: repo-app-id/);
  assert.doesNotMatch(builtYaml, /objectName: repo-app-private-key|objectName: copilot-app-app-id/);
  assert.doesNotMatch(builtYaml, /name: Auth__Entra__ClientSecret/);
  assert.doesNotMatch(builtYaml, /changeme/);
  assert.doesNotMatch(builtYaml, /example\.com/);
  assert.doesNotMatch(builtYaml, /mcp-oauth-signing-key|Auth__OAuth__(?:SigningKey|Issuer|Audience)|OAUTH_ISSUER|OAUTH_AUDIENCE/);

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
  // Pre-existing drift (unrelated to this branch): dev's PR #931 ("raise agentweaver-exec memory
  // limit 2Gi→4Gi to prevent preview server OOM") bumped the exec container's memory request/limit
  // to 2Gi/4Gi in k8s/base/sandbox-template-agenthost.yaml without syncing this assertion. Updating
  // the expectation here to match the shipped template rather than touching production config.
  assert.match(
    sandboxTemplate,
    /name: agentweaver-exec[\s\S]*?resources:\s*\n\s*limits:\s*\n\s*cpu: 1200m\s*\n\s*ephemeral-storage: 4Gi\s*\n\s*memory: 4Gi\s*\n\s*requests:\s*\n\s*cpu: 700m\s*\n\s*ephemeral-storage: 1Gi\s*\n\s*memory: 2Gi/,
    "The executor that runs previews must retain explicit resource reservation and limits",
  );
  const mcpDeployment = manifestForFilename(docs, "mcp-deployment.yaml");
  assert.match(
    mcpDeployment,
    /name: Auth__OAuth__PublicOrigin\s*\n\s*valueFrom:\s*\n\s*configMapKeyRef:\s*\n\s*key: OAUTH_PUBLIC_ORIGIN\s*\n\s*name: agentweaver-runtime-config/,
    "MCP must pin broker discovery, issuer, resource metadata, and challenges to the public origin",
  );
  assert.doesNotMatch(
    mcpDeployment,
    /Auth__Entra__|Auth__Mode|AllowGitHubPassthrough|AGENTWEAVER_API_KEY|AGENTWEAVER_ALLOW_SHARED_KEY/,
    "MCP must not retain direct-Entra, GitHub-token, or internal-key fallback configuration",
  );
  const apiDeployment = manifestForFilename(docs, "api-deployment.yaml");
  assert.doesNotMatch(apiDeployment, /Auth__Mcp__AllowGitHubPassthrough/);

  const mcpRoute = manifestForFilename(docs, "mcp-httproute.yaml");
  assert.match(mcpRoute, /value: \/\.well-known\/oauth-protected-resource(?:\s|$)/);
  assert.match(mcpRoute, /value: \/\.well-known\/oauth-protected-resource\/mcp(?:\s|$)/);

  const apiRoute = manifestForFilename(docs, "httproute-api.yaml");
  for (const path of [
    "/.well-known/oauth-authorization-server",
    "/.well-known/openid-configuration",
    "/oauth/authorize",
    "/oauth/token",
    "/oauth/register",
    "/oauth/resume",
    "/oauth/revoke",
    "/oauth/jwks",
  ]) {
    assert.match(apiRoute, new RegExp(`value: ${path.replaceAll("/", "\\/")}(?:\\s|$)`));
  }
  assert.doesNotMatch(
    apiRoute,
    /oauth-authorization-server\/mcp|openid-configuration\/mcp|type: PathPrefix\s*\n\s*value: \/oauth/,
    "the gateway must route only actual OpenIddict discovery, JWKS, and protocol endpoints",
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

test("active deployment sources contain no retired MCP OAuth signing artifacts", () => {
  const retired = [
    ["mcp", "oauth", "signing", "key"].join("-"),
    ["Auth", "OAuth", "SigningKey"].join("__"),
    ["Auth", "OAuth", "Issuer"].join("__"),
    ["Auth", "OAuth", "Audience"].join("__"),
    ["OAUTH", "ISSUER"].join("_"),
    ["OAUTH", "AUDIENCE"].join("_"),
    ["16", "provision", "oauth", "signing", "key"].join("-"),
  ];
  const roots = [
    path.join(DEFAULT_REPO_ROOT, "k8s", "base"),
    path.join(DEFAULT_REPO_ROOT, "k8s", "overlays", "production"),
    path.join(DEFAULT_REPO_ROOT, "scripts", "azure"),
  ];
  const files = roots.flatMap(function collect(directory) {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) =>
      entry.isDirectory()
        ? entry.name === "tests" || entry.name.startsWith(".") ? [] : collect(path.join(directory, entry.name))
        : [path.join(directory, entry.name)]);
  });

  for (const file of files) {
    const text = fs.readFileSync(file, "utf8");
    for (const artifact of retired) {
      assert.ok(!text.includes(artifact), `${path.relative(DEFAULT_REPO_ROOT, file)} must not contain retired artifact ${artifact}`);
    }
  }
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
