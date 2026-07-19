// deploy-render.test.mjs -- Rendered-YAML parity tests for
// steps/30-deploy.mjs's renderManifests(), covering every k8s/*.yaml
// template against a fixed, realistic variable set.
//
// PARITY PROOF (see Tank's Phase 2 summary for full details): this fixed
// variable set was also run through the REAL bash 30-deploy.sh envsubst
// invocation inside WSL (GNU gettext envsubst 0.21) and diffed byte-for-byte
// against this module's renderManifests() output for all 40 k8s/*.yaml
// files -- zero differences. That one-off diff run is not checked in here
// (it depends on WSL/envsubst, which is not guaranteed to exist in every
// dev/CI environment); these node:test assertions are the repeatable,
// environment-independent half of that parity proof.

import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { renderManifests, ALLOW_LIST, DEFAULT_REPO_ROOT } from "../steps/30-deploy.mjs";
import { renderTemplate } from "../lib/render.mjs";

const K8S_DIR = path.join(DEFAULT_REPO_ROOT, "k8s");

// Fixed, realistic input variables (values are consistent, well-formed
// strings -- not real Azure resources, matching the task's parity-test
// requirement). Same values used for the WSL/envsubst diff run.
const VARS = {
  HOST: "agentweaver.abc123def456.westus2.staging.aksapp.io",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  IMAGE_TAG: "v0.9.71",
  AGENTHOST_IMAGE_TAG: "v0.9.71-agenthost",
  IDENTITY_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
  KEYVAULT_NAME: "agentweaver-kv",
  AGENTHOST_KEYVAULT_URI: "https://agentweaver-kv.vault.azure.net/",
  TENANT_ID: "66666666-7777-8888-9999-000000000000",
  PREVIEW_HOSTNAME: "*.abc123def456.westus2.staging.aksapp.io",
  PREVIEW_TLS_SECRET: "agentweaver-tls",
  SANDBOX_PREVIEW_ENABLED: "true",
  SANDBOX_PREVIEW_ZONE_SUFFIX: "abc123def456.westus2.staging.aksapp.io",
  APPINSIGHTS_WORKSPACE_ID: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
};

test("ALLOW_LIST matches the exact envsubst whitelist from 30-deploy.sh/.ps1", () => {
  assert.deepEqual(ALLOW_LIST, [
    "HOST",
    "ACR_LOGIN_SERVER",
    "IMAGE_TAG",
    "AGENTHOST_IMAGE_TAG",
    "IDENTITY_CLIENT_ID",
    "KEYVAULT_NAME",
    "AGENTHOST_KEYVAULT_URI",
    "TENANT_ID",
    "PREVIEW_HOSTNAME",
    "PREVIEW_TLS_SECRET",
    "SANDBOX_PREVIEW_ENABLED",
    "SANDBOX_PREVIEW_ZONE_SUFFIX",
    "APPINSIGHTS_WORKSPACE_ID",
  ]);
});

test("renderManifests renders every k8s/*.yaml template and is self-consistent with renderTemplate", () => {
  const rendered = renderManifests(VARS, { repoRoot: DEFAULT_REPO_ROOT });
  const yamlFiles = fs.readdirSync(K8S_DIR).filter((n) => n.endsWith(".yaml"));
  assert.equal(rendered.size, yamlFiles.length);
  for (const fname of yamlFiles) {
    const raw = fs.readFileSync(path.join(K8S_DIR, fname), "utf8");
    const expected = renderTemplate(raw, VARS, ALLOW_LIST);
    assert.equal(rendered.get(fname), expected, `mismatch rendering ${fname}`);
  }
});

test("gateway.yaml: HOST is substituted into the listener hostname", () => {
  const rendered = renderManifests(VARS, { repoRoot: DEFAULT_REPO_ROOT });
  const out = rendered.get("gateway.yaml");
  assert.match(out, /hostname: agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io/);
  assert.doesNotMatch(out, /\$\{HOST\}/);
});

test("gateway-preview.yaml: PREVIEW_HOSTNAME and PREVIEW_TLS_SECRET are substituted", () => {
  const rendered = renderManifests(VARS, { repoRoot: DEFAULT_REPO_ROOT });
  const out = rendered.get("gateway-preview.yaml");
  assert.match(out, /hostname: "\*\.abc123def456\.westus2\.staging\.aksapp\.io"/);
  assert.match(out, /name: agentweaver-tls/);
  assert.doesNotMatch(out, /\$\{PREVIEW_HOSTNAME\}|\$\{PREVIEW_TLS_SECRET\}/);
});

test("secret-provider-class.yaml: IDENTITY_CLIENT_ID, KEYVAULT_NAME, TENANT_ID are substituted everywhere they appear", () => {
  const rendered = renderManifests(VARS, { repoRoot: DEFAULT_REPO_ROOT });
  const out = rendered.get("secret-provider-class.yaml");
  const clientIdOccurrences = out.split(`clientID: "${VARS.IDENTITY_CLIENT_ID}"`).length - 1;
  const keyvaultOccurrences = out.split(`keyvaultName: "${VARS.KEYVAULT_NAME}"`).length - 1;
  const tenantOccurrences = out.split(`tenantId: "${VARS.TENANT_ID}"`).length - 1;
  assert.equal(clientIdOccurrences, 2);
  assert.equal(keyvaultOccurrences, 2);
  assert.equal(tenantOccurrences, 2);
  assert.doesNotMatch(out, /\$\{IDENTITY_CLIENT_ID\}|\$\{KEYVAULT_NAME\}|\$\{TENANT_ID\}/);
});

test("api-deployment.yaml: allow-listed vars substituted; non-allow-listed GitHub__ClientId/Secret left literal", () => {
  const rendered = renderManifests(VARS, { repoRoot: DEFAULT_REPO_ROOT });
  const out = rendered.get("api-deployment.yaml");
  assert.match(
    out,
    /image: agentweaverregistry\.azurecr\.io\/agentweaver-api:v0\.9\.71/,
  );
  assert.match(out, /value: https:\/\/agentweaver\.abc123def456\.westus2\.staging\.aksapp\.io\/mcp/);
  assert.match(out, /value: https:\/\/agentweaver-kv\.vault\.azure\.net/);
  assert.match(out, /value: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"/);
  // Not on the allow-list -- must remain byte-for-byte literal (envsubst semantics).
  assert.match(out, /export Auth__GitHub__ClientId="\$\{GitHub__ClientId\}"/);
  assert.match(out, /export Auth__GitHub__ClientSecret="\$\{GitHub__ClientSecret\}"/);
});

test("allow-listed variable left unset by the caller renders as empty string, never removed or left literal", () => {
  const partialVars = { ...VARS };
  delete partialVars.APPINSIGHTS_WORKSPACE_ID;
  const rendered = renderManifests(partialVars, { repoRoot: DEFAULT_REPO_ROOT });
  const out = rendered.get("api-deployment.yaml");
  assert.match(out, /value: ""/);
  assert.doesNotMatch(out, /\$\{APPINSIGHTS_WORKSPACE_ID\}/);
});

test("stripWildcardPrefix strips a leading '*.' the same way bash's ${DOMAIN#\\*.} does", async () => {
  const { stripWildcardPrefix } = await import("../steps/30-deploy.mjs");
  assert.equal(stripWildcardPrefix("*.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io"), "6a3de4fe60529400010f3fba.westus2.staging.aksapp.io");
  assert.equal(stripWildcardPrefix("agentweaver.example.com"), "agentweaver.example.com");
});
