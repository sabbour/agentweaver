// 16-provision-oauth-signing-key.mjs -- Faithful Node port of
// scripts/aks/16-provision-oauth-signing-key.sh (cross-checked against
// 16-provision-oauth-signing-key.ps1). Read both before changing this file;
// they must stay in lockstep with this port's behavior.
//
// Provisions the MCP OAuth 2.1 RSA-2048 signing key ('mcp-oauth-signing-key')
// and the internal service-to-service bearer token ('mcp-api-key') in Key
// Vault. Idempotent: skips creation if a secret already exists with a
// non-empty value. Intentionally NOT called from 30-deploy.mjs -- this is a
// one-time operator action, exactly like the legacy scripts document.
//
// cfg is the resolved variables.mjs output: KEYVAULT_NAME. Optional:
// AGENTWEAVER_TMP_DIR (scratch directory for the transient PEM file, default
// '<repoRoot>/.agentweaver/tmp'), repoRoot.

import fs from "node:fs";
import path from "node:path";
import { generateKeyPairSync, randomBytes } from "node:crypto";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as secretDefault from "../lib/secret.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

export const SIGNING_KEY_SECRET_NAME = "mcp-oauth-signing-key";
export const API_KEY_SECRET_NAME = "mcp-api-key";

/** Reads an existing Key Vault secret's value, or '' if absent/unreadable. Never throws. */
export async function existingSecretValue(keyvaultName, name, { exec = execDefault } = {}) {
  const result = await exec.capture(
    "az",
    ["keyvault", "secret", "show", "--vault-name", keyvaultName, "--name", name, "--query", "value", "--output", "tsv"],
    { allowFailure: true },
  );
  return result.code === 0 ? result.stdout.trim() : "";
}

/**
 * Provisions the MCP OAuth signing key + internal API key: faithful port of
 * 16-provision-oauth-signing-key.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, fs: fsImpl = fs, repoRoot = DEFAULT_REPO_ROOT, secret = secretDefault } = opts;

  log.info("");
  log.section("MCP OAuth signing key provisioning");
  log.field("Key Vault", cfg.KEYVAULT_NAME);
  log.field("Secret name", SIGNING_KEY_SECRET_NAME);
  log.info("");

  const existingValue = await existingSecretValue(cfg.KEYVAULT_NAME, SIGNING_KEY_SECRET_NAME, { exec });
  if (existingValue) {
    log.skip(`Secret '${SIGNING_KEY_SECRET_NAME}' already exists in Key Vault '${cfg.KEYVAULT_NAME}'.`);
    log.info("         To rotate, delete the secret version and re-run this step.");
  } else {
    log.info("  Generating RSA-2048 private key...");
    const scratchDir = cfg.AGENTWEAVER_TMP_DIR || path.join(repoRoot, ".agentweaver", "tmp");
    fsImpl.mkdirSync(scratchDir, { recursive: true });
    const tmpKeyFile = path.join(scratchDir, `mcp-oauth-signing-key-${process.pid}.pem`);
    try {
      // Uses Node's built-in crypto module (no external 'openssl' process) so
      // this works identically on every platform, including Windows machines
      // without openssl on PATH. PKCS8 PEM output matches what 'openssl
      // genpkey' produced -- both start with '-----BEGIN PRIVATE KEY-----'.
      const { privateKey } = generateKeyPairSync("rsa", {
        modulusLength: 2048,
        publicKeyEncoding: { type: "spki", format: "pem" },
        privateKeyEncoding: { type: "pkcs8", format: "pem" },
      });
      fsImpl.writeFileSync(tmpKeyFile, privateKey);
      log.info(`  Storing private key as Key Vault secret '${SIGNING_KEY_SECRET_NAME}'...`);
      await exec.capture("az", [
        "keyvault",
        "secret",
        "set",
        "--vault-name",
        cfg.KEYVAULT_NAME,
        "--name",
        SIGNING_KEY_SECRET_NAME,
        "--file",
        tmpKeyFile,
        "--content-type",
        "application/x-pem-file",
        "--output",
        "none",
      ]);
      log.ok(`Secret '${SIGNING_KEY_SECRET_NAME}' created successfully.`);
    } finally {
      fsImpl.rmSync(tmpKeyFile, { force: true });
    }
  }

  log.info("");
  log.section("Internal API key provisioning");
  log.field("Key Vault", cfg.KEYVAULT_NAME);
  log.field("Secret name", API_KEY_SECRET_NAME);
  log.info("");

  const existingApiKey = await existingSecretValue(cfg.KEYVAULT_NAME, API_KEY_SECRET_NAME, { exec });
  if (existingApiKey) {
    log.skip(`Secret '${API_KEY_SECRET_NAME}' already exists in Key Vault '${cfg.KEYVAULT_NAME}'.`);
  } else {
    log.info("  Generating 32-byte random hex key...");
    const generatedApiKey = randomBytes(32).toString("hex");
    const apiKeyScratchDir = cfg.AGENTWEAVER_TMP_DIR || path.join(repoRoot, ".agentweaver", "tmp");
    await secret.withSecretFile(apiKeyScratchDir, "mcp-api-key", generatedApiKey, (filePath) =>
      exec.capture("az", [
        "keyvault",
        "secret",
        "set",
        "--vault-name",
        cfg.KEYVAULT_NAME,
        "--name",
        API_KEY_SECRET_NAME,
        "--file",
        filePath,
        "--content-type",
        "text/plain",
        "--output",
        "none",
      ]),
    );
    log.ok(`Secret '${API_KEY_SECRET_NAME}' created successfully.`);
  }

  log.info("");
  log.info("  Next steps:");
  log.info("    1. Run the deploy step to apply the updated manifests.");
  log.info(
    "    2. Verify: kubectl get secret agentweaver-secrets -n agentweaver -o jsonpath='{.data.mcp-oauth-signing-key}' | base64 -d | head -1",
  );
  log.info("       Expected: -----BEGIN PRIVATE KEY-----");

  return { signingKeyProvisioned: !existingValue, apiKeyProvisioned: !existingApiKey };
}
