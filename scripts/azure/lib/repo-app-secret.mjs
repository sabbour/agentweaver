// Repo App private-key contract shared by Azure provisioning, deployment,
// rendering, and verification. The application receives the logical name and
// KeyVaultSecretStore resolves it to the canonical physical Key Vault name.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import * as execDefault from "./exec.mjs";
import * as logDefault from "./log.mjs";
import * as secretDefault from "./secret.mjs";

export const REPO_APP_PRIVATE_KEY_SECRET = Object.freeze({
  // Keep the explicit pair aligned with KeyVaultSecretStore.SanitizeKey().
  // Do not duplicate the application's general prefix/sanitization algorithm here.
  logicalName: "repo-app-private-key",
  physicalName: "ghtok-repo-app-private-key",
  legacyPhysicalName: "repo-app-private-key",
});

const SECRET_NOT_FOUND = /SecretNotFound|was not found in this key vault/i;
const RBAC_PROPAGATING = /Forbidden|ForbiddenByRbac|not authorized/i;

export async function inspectKeyVaultSecret(vaultName, name, { exec = execDefault } = {}) {
  const result = await exec.capture(
    "az",
    [
      "keyvault",
      "secret",
      "show",
      "--vault-name",
      vaultName,
      "--name",
      name,
      "--query",
      "id",
      "--output",
      "tsv",
    ],
    { allowFailure: true },
  );
  if (result.code === 0) return { status: "available" };
  if (SECRET_NOT_FOUND.test(result.stderr || "")) return { status: "missing" };
  return { status: "inaccessible" };
}

export async function setSecretFileWithRetry(
  keyvaultName,
  name,
  filePath,
  {
    exec = execDefault,
    log = logDefault,
    maxAttempts = 12,
    sleep = defaultSleep,
    fsImpl = fs,
  } = {},
) {
  let stat;
  try {
    stat = fsImpl.statSync(filePath);
  } catch {
    throw new Error(`Repo App private-key file '${filePath}' could not be read.`);
  }
  if (!stat.isFile() || stat.size === 0) {
    throw new Error(`Repo App private-key file '${filePath}' must be a non-empty file.`);
  }

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const result = await exec.capture(
      "az",
      [
        "keyvault",
        "secret",
        "set",
        "--vault-name",
        keyvaultName,
        "--name",
        name,
        "--file",
        filePath,
        "--output",
        "none",
      ],
      { allowFailure: true },
    );
    if (result.code === 0) return;
    if (RBAC_PROPAGATING.test(result.stderr || "") && attempt < maxAttempts) {
      log.info(`  [retry ${attempt}/${maxAttempts}] Key Vault access for '${name}' is still propagating; waiting 15s...`);
      await sleep(15000);
      continue;
    }
    throw new Error(`Failed to set Key Vault secret '${name}': ${result.stderr || "unknown Azure CLI error"}`);
  }
}

export async function setSecretWithRetry(
  keyvaultName,
  name,
  value,
  {
    exec = execDefault,
    log = logDefault,
    maxAttempts = 12,
    sleep = defaultSleep,
    secret = secretDefault,
    scratchDir = os.tmpdir(),
  } = {},
) {
  return secret.withSecretFile(scratchDir, `kv-secret-${name}`, value, (filePath) =>
    setSecretFileWithRetry(keyvaultName, name, filePath, {
      exec,
      log,
      maxAttempts,
      sleep,
    }),
  );
}

export async function ensureRepoAppPrivateKeySecret(
  {
    vaultName,
    sourceFile = "",
  },
  {
    exec = execDefault,
    log = logDefault,
    fsImpl = fs,
    scratchRoot = os.tmpdir(),
    sleep = defaultSleep,
  } = {},
) {
  if (!vaultName) throw new Error("KEYVAULT_NAME is required to verify the Repo App private key.");

  const configuredFile = String(sourceFile ?? "").trim();
  if (configuredFile) {
    const resolvedFile = path.resolve(configuredFile);
    await setSecretFileWithRetry(
      vaultName,
      REPO_APP_PRIVATE_KEY_SECRET.physicalName,
      resolvedFile,
      { exec, log, fsImpl, sleep },
    );
    const imported = await inspectKeyVaultSecret(
      vaultName,
      REPO_APP_PRIVATE_KEY_SECRET.physicalName,
      { exec },
    );
    if (imported.status !== "available") {
      throw new Error(
        `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' ` +
          `was written but is ${imported.status}.`,
      );
    }
    log.ok(`Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' imported.`);
    return { status: "imported", ...REPO_APP_PRIVATE_KEY_SECRET };
  }

  const canonical = await inspectKeyVaultSecret(
    vaultName,
    REPO_APP_PRIVATE_KEY_SECRET.physicalName,
    { exec },
  );
  if (canonical.status === "available") {
    log.ok(`Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is accessible.`);
    return { status: "available", ...REPO_APP_PRIVATE_KEY_SECRET };
  }
  if (canonical.status === "inaccessible") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is inaccessible. ` +
        "Verify the active Azure identity has Key Vault secret get permission.",
    );
  }

  const legacy = await inspectKeyVaultSecret(
    vaultName,
    REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName,
    { exec },
  );
  if (legacy.status === "inaccessible") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is missing, and ` +
        `legacy secret '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' is inaccessible. ` +
        "Verify the active Azure identity has Key Vault secret get permission.",
    );
  }
  if (legacy.status === "missing") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is missing and no ` +
        `legacy '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' secret is available to migrate. ` +
        "Provide REPO_APP_PRIVATE_KEY_FILE or --repo-app-private-key-file with the GitHub App PEM.",
    );
  }

  const scratchDir = fsImpl.mkdtempSync(path.join(scratchRoot, "agentweaver-repo-app-key-"));
  const migrationFile = path.join(scratchDir, "repo-app-private-key.pem");
  try {
    const downloaded = await exec.capture(
      "az",
      [
        "keyvault",
        "secret",
        "download",
        "--vault-name",
        vaultName,
        "--name",
        REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName,
        "--file",
        migrationFile,
        "--encoding",
        "utf-8",
        "--overwrite",
      ],
      { allowFailure: true },
    );
    if (downloaded.code !== 0) {
      throw new Error(
        `Legacy Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' ` +
          `could not be downloaded for migration: ${downloaded.stderr || "unknown Azure CLI error"}`,
      );
    }
    await setSecretFileWithRetry(
      vaultName,
      REPO_APP_PRIVATE_KEY_SECRET.physicalName,
      migrationFile,
      { exec, log, fsImpl, sleep },
    );
  } finally {
    fsImpl.rmSync(scratchDir, { recursive: true, force: true });
  }

  const migrated = await inspectKeyVaultSecret(
    vaultName,
    REPO_APP_PRIVATE_KEY_SECRET.physicalName,
    { exec },
  );
  if (migrated.status !== "available") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' ` +
        `was migrated but is ${migrated.status}.`,
    );
  }
  log.warn(
    `Migrated legacy Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' to ` +
      `'${REPO_APP_PRIVATE_KEY_SECRET.physicalName}'; the legacy secret was preserved.`,
  );
  return { status: "migrated", ...REPO_APP_PRIVATE_KEY_SECRET };
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
