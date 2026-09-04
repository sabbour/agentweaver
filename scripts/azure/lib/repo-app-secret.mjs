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
const DELETED_BUT_RECOVERABLE = /ObjectIsDeletedButRecoverable|deleted but recoverable/i;
const RECOVERY_IN_PROGRESS = /Conflict|already being recovered|recovery.*in progress/i;
const RECOVERY_ALREADY_COMPLETED = /already (?:been )?recovered|already (?:in )?(?:an? )?active(?: state)?/i;
const RECOVERY_POLL_ATTEMPTS = 60;
const RECOVERY_POLL_INTERVAL_MS = 500;

async function inspectActiveKeyVaultSecretResult(vaultName, name, { exec = execDefault } = {}) {
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
  if (result.code === 0) return { status: "available", error: "" };
  if (SECRET_NOT_FOUND.test(result.stderr || "")) {
    return { status: "missing", error: result.stderr || "" };
  }
  return { status: "inaccessible", error: result.stderr || "" };
}

async function inspectDeletedKeyVaultSecretResult(vaultName, name, { exec = execDefault } = {}) {
  const result = await exec.capture(
    "az",
    [
      "keyvault",
      "secret",
      "show-deleted",
      "--vault-name",
      vaultName,
      "--name",
      name,
      "--query",
      "recoveryId",
      "--output",
      "tsv",
    ],
    { allowFailure: true },
  );
  if (result.code === 0) return { status: "recoverable", error: "" };
  if (SECRET_NOT_FOUND.test(result.stderr || "")) {
    return { status: "missing", error: result.stderr || "" };
  }
  return { status: "inaccessible", error: result.stderr || "" };
}

async function inspectKeyVaultSecretResult(vaultName, name, { exec = execDefault } = {}) {
  const active = await inspectActiveKeyVaultSecretResult(vaultName, name, { exec });
  if (active.status !== "missing") return active;

  const deleted = await inspectDeletedKeyVaultSecretResult(vaultName, name, { exec });
  if (deleted.status !== "missing") return deleted;

  const activeAfterDeletedCheck = await inspectActiveKeyVaultSecretResult(vaultName, name, { exec });
  return activeAfterDeletedCheck.status === "missing"
    ? { status: "missing", error: activeAfterDeletedCheck.error || active.error }
    : activeAfterDeletedCheck;
}

export async function inspectKeyVaultSecret(vaultName, name, opts = {}) {
  const { status } = await inspectKeyVaultSecretResult(vaultName, name, opts);
  return { status };
}

async function recoverDeletedSecretAndWait(
  keyvaultName,
  name,
  {
    exec = execDefault,
    log = logDefault,
    maxPollAttempts = RECOVERY_POLL_ATTEMPTS,
    pollIntervalMs = RECOVERY_POLL_INTERVAL_MS,
    sleep = defaultSleep,
  } = {},
) {
  const requestRecovery = async () => {
    const recovered = await exec.capture(
      "az",
      [
        "keyvault",
        "secret",
        "recover",
        "--vault-name",
        keyvaultName,
        "--name",
        name,
        "--output",
        "none",
      ],
      { allowFailure: true },
    );
    const recoveryError = recovered.stderr || "";
    if (
      recovered.code !== 0 &&
      !SECRET_NOT_FOUND.test(recoveryError) &&
      !DELETED_BUT_RECOVERABLE.test(recoveryError) &&
      !RECOVERY_IN_PROGRESS.test(recoveryError) &&
      !RECOVERY_ALREADY_COMPLETED.test(recoveryError)
    ) {
      throw new Error(`Failed to recover Key Vault secret '${name}': ${recoveryError || "unknown Azure CLI error"}`);
    }
  };

  log.info(`  Recovering soft-deleted Key Vault secret '${name}'...`);
  await requestRecovery();
  for (let attempt = 1; attempt <= maxPollAttempts; attempt++) {
    const inspected = await inspectActiveKeyVaultSecretResult(keyvaultName, name, { exec });
    if (inspected.status === "available") return;
    const retryable =
      inspected.status === "missing" ||
      DELETED_BUT_RECOVERABLE.test(inspected.error) ||
      RECOVERY_IN_PROGRESS.test(inspected.error);
    if (!retryable) {
      throw new Error(
        `Key Vault secret '${name}' is inaccessible while waiting for recovery: ` +
          `${inspected.error || "unknown Azure CLI error"}`,
      );
    }
    if (attempt < maxPollAttempts) {
      await sleep(pollIntervalMs);
    }
  }

  throw new Error(
    `Key Vault secret '${name}' did not become available after ` +
      `${maxPollAttempts * pollIntervalMs}ms of bounded recovery polling.`,
  );
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
    if (
      (DELETED_BUT_RECOVERABLE.test(result.stderr || "") ||
        RECOVERY_IN_PROGRESS.test(result.stderr || "")) &&
      attempt < maxAttempts
    ) {
      await recoverDeletedSecretAndWait(keyvaultName, name, {
        exec,
        log,
        sleep,
      });
      continue;
    }
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

async function reconcileCanonicalSecret(
  vaultName,
  {
    exec = execDefault,
    log = logDefault,
    sleep = defaultSleep,
  } = {},
) {
  const canonical = await inspectKeyVaultSecretResult(
    vaultName,
    REPO_APP_PRIVATE_KEY_SECRET.physicalName,
    { exec },
  );
  if (canonical.status === "available") return "available";
  if (canonical.status === "recoverable") {
    await recoverDeletedSecretAndWait(
      vaultName,
      REPO_APP_PRIVATE_KEY_SECRET.physicalName,
      { exec, log, sleep },
    );
    return "recovered";
  }
  if (canonical.status === "inaccessible") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is inaccessible. ` +
        "Verify the active Azure identity has Key Vault secret get permission.",
    );
  }
  return "missing";
}

function logCanonicalStatus(status, log) {
  if (status === "available") {
    log.ok(`Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is accessible.`);
  } else if (status === "recovered") {
    log.ok(`Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' recovered.`);
  }
}

export async function ensureRepoAppPrivateKeySecret(
  params = {},
  {
    exec = execDefault,
    log = logDefault,
    fsImpl = fs,
    sleep = defaultSleep,
  } = {},
) {
  const vaultName = String(params?.vaultName ?? "").trim();
  const sourceFile = params?.sourceFile ?? "";
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

  const canonicalStatus = await reconcileCanonicalSecret(vaultName, { exec, log, sleep });
  if (canonicalStatus !== "missing") {
    logCanonicalStatus(canonicalStatus, log);
    return { status: canonicalStatus, ...REPO_APP_PRIVATE_KEY_SECRET };
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
  if (legacy.status !== "available") {
    throw new Error(
      `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is missing and no ` +
        `legacy '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' secret is available. ` +
        "Provide REPO_APP_PRIVATE_KEY_FILE or --repo-app-private-key-file with the GitHub App PEM.",
    );
  }

  const canonicalAfterLegacyCheck = await reconcileCanonicalSecret(vaultName, { exec, log, sleep });
  if (canonicalAfterLegacyCheck !== "missing") {
    logCanonicalStatus(canonicalAfterLegacyCheck, log);
    return { status: canonicalAfterLegacyCheck, ...REPO_APP_PRIVATE_KEY_SECRET };
  }

  throw new Error(
    `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is missing while legacy ` +
      `secret '${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName}' is available. Automatic legacy migration is ` +
      "disabled because Azure Key Vault secret set cannot conditionally create the canonical value across " +
      "deployment runners. Migrate explicitly with:\n" +
      `  az keyvault secret download --vault-name ${vaultName} --name ` +
      `${REPO_APP_PRIVATE_KEY_SECRET.legacyPhysicalName} --file <protected-repo-app-private-key.pem> ` +
      "--encoding utf-8 --overwrite\n" +
      "then rerun the deployment with:\n" +
      "  npm run azure:provision-infra -- --repo-app-private-key-file <protected-repo-app-private-key.pem>\n" +
      "The configured-file import intentionally replaces the canonical secret. Serialize that explicit import " +
      "in CI and delete the protected local file afterward.",
  );
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
