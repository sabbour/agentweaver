// Repo App private-key contract shared by Azure provisioning, deployment,
// rendering, and verification. The application receives the logical name and
// KeyVaultSecretStore resolves it to the canonical physical Key Vault name.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createPrivateKey } from "node:crypto";
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
const PEM_BLOCK = /-----BEGIN ([A-Z0-9 ]+)-----[\s\S]*?-----END \1-----/g;
const RSA_PRIVATE_KEY_LABELS = new Set(["RSA PRIVATE KEY", "PRIVATE KEY"]);
const ENCRYPTED_PRIVATE_KEY = /BEGIN ENCRYPTED PRIVATE KEY|Proc-Type:\s*4,\s*ENCRYPTED/i;

function normalizeComparablePath(value) {
  const normalized = path.normalize(value);
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function validateRepoAppPrivateKeyBytes(bytes, sourceFile) {
  const pem = bytes.toString("utf8").trim();
  if (!pem) {
    throw new Error(`Repo App private-key file '${sourceFile}' must be a non-empty file.`);
  }
  if (ENCRYPTED_PRIVATE_KEY.test(pem)) {
    throw new Error(
      `Repo App private-key file '${sourceFile}' is encrypted. ` +
        "Encrypted private keys are not supported because provisioning cannot prompt for a passphrase.",
    );
  }

  const blocks = [...pem.matchAll(PEM_BLOCK)];
  if (
    blocks.length !== 1 ||
    blocks[0][0] !== pem ||
    !RSA_PRIVATE_KEY_LABELS.has(blocks[0][1])
  ) {
    throw new Error(
      `Repo App private-key file '${sourceFile}' must contain exactly one unencrypted ` +
        "RSA PRIVATE KEY or PRIVATE KEY PEM block and no other content.",
    );
  }

  let privateKey;
  try {
    privateKey = createPrivateKey({ key: pem, format: "pem" });
  } catch {
    throw new Error(`Repo App private-key file '${sourceFile}' must contain a valid PEM-encoded RSA private key.`);
  }
  if (privateKey.type !== "private" || privateKey.asymmetricKeyType !== "rsa") {
    throw new Error(`Repo App private-key file '${sourceFile}' must contain an RSA private key.`);
  }
}

export function stageRepoAppPrivateKeyFile(
  sourceFile,
  {
    fsImpl = fs,
    scratchDir = os.tmpdir(),
  } = {},
) {
  const configuredFile = String(sourceFile ?? "").trim();
  if (!configuredFile) return null;

  const resolvedFile = path.resolve(configuredFile);
  let sourceStat;
  let realPath;
  try {
    sourceStat = fsImpl.lstatSync(resolvedFile);
    realPath = fsImpl.realpathSync.native
      ? fsImpl.realpathSync.native(resolvedFile)
      : fsImpl.realpathSync(resolvedFile);
  } catch {
    throw new Error(`Repo App private-key file '${resolvedFile}' could not be read.`);
  }
  if (sourceStat.isSymbolicLink()) {
    throw new Error(
      `Repo App private-key file '${resolvedFile}' must not be a symbolic link, junction, or reparse-point path.`,
    );
  }
  if (normalizeComparablePath(realPath) !== normalizeComparablePath(resolvedFile)) {
    throw new Error(
      `Repo App private-key file '${resolvedFile}' must not traverse a symbolic link, junction, or reparse-point path.`,
    );
  }
  if (!sourceStat.isFile() || sourceStat.size === 0) {
    throw new Error(`Repo App private-key file '${resolvedFile}' must be a non-empty file.`);
  }

  let sourceFd;
  let bytes;
  try {
    const noFollow = fsImpl.constants.O_NOFOLLOW ?? 0;
    sourceFd = fsImpl.openSync(resolvedFile, fsImpl.constants.O_RDONLY | noFollow);
    const openedStat = fsImpl.fstatSync(sourceFd);
    if (
      !openedStat.isFile() ||
      (sourceStat.dev !== openedStat.dev || sourceStat.ino !== openedStat.ino)
    ) {
      throw new Error(
        `Repo App private-key file '${resolvedFile}' changed while it was being opened or is not a regular file.`,
      );
    }
    bytes = fsImpl.readFileSync(sourceFd);
  } catch (error) {
    if (error?.message?.startsWith("Repo App private-key file")) throw error;
    throw new Error(`Repo App private-key file '${resolvedFile}' could not be read.`);
  } finally {
    if (sourceFd !== undefined) fsImpl.closeSync(sourceFd);
  }

  validateRepoAppPrivateKeyBytes(bytes, resolvedFile);

  fsImpl.mkdirSync(scratchDir, { recursive: true });
  const stageDir = fsImpl.mkdtempSync(path.join(scratchDir, "agentweaver-repo-app-key-"));
  const stagedFile = path.join(stageDir, "private-key.pem");
  let stagedFd;
  try {
    try {
      fsImpl.chmodSync(stageDir, 0o700);
    } catch {
      // Windows ACLs do not expose POSIX mode bits through Node.
    }
    stagedFd = fsImpl.openSync(
      stagedFile,
      fsImpl.constants.O_CREAT | fsImpl.constants.O_EXCL | fsImpl.constants.O_WRONLY,
      0o600,
    );
    fsImpl.writeFileSync(stagedFd, bytes);
    fsImpl.fsyncSync(stagedFd);
    try {
      fsImpl.fchmodSync(stagedFd, 0o600);
    } catch {
      // Windows ACLs do not expose POSIX mode bits through Node.
    }
  } catch {
    fsImpl.rmSync(stageDir, { recursive: true, force: true });
    throw new Error("Could not stage the validated Repo App private key in a protected temporary file.");
  } finally {
    if (stagedFd !== undefined) fsImpl.closeSync(stagedFd);
  }

  let cleaned = false;
  return {
    filePath: stagedFile,
    cleanup() {
      if (cleaned) return;
      cleaned = true;
      fsImpl.rmSync(stageDir, { recursive: true, force: true });
    },
  };
}

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

async function setStagedSecretFileWithRetry(
  keyvaultName,
  name,
  filePath,
  {
    exec = execDefault,
    log = logDefault,
    maxAttempts = 12,
    sleep = defaultSleep,
    fsImpl = fs,
    recoverDeleted = false,
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
  let stagedBytes;
  try {
    stagedBytes = fsImpl.readFileSync(filePath);
  } catch {
    throw new Error(`Repo App private-key file '${filePath}' could not be read.`);
  }
  validateRepoAppPrivateKeyBytes(stagedBytes, filePath);

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
      if (!recoverDeleted) {
        throw new Error(
          `Canonical Repo App private-key secret '${name}' is soft-deleted. Normal deployment will not ` +
            "reactivate old credentials. Suspend or revoke workload access, then rerun with the explicit " +
            "--recover-repo-app-private-key operator flag.",
        );
      }
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

export async function setSecretFileWithRetry(
  keyvaultName,
  name,
  filePath,
  opts = {},
) {
  const staged = stageRepoAppPrivateKeyFile(filePath, opts);
  try {
    return await setStagedSecretFileWithRetry(
      keyvaultName,
      name,
      staged.filePath,
      opts,
    );
  } finally {
    staged.cleanup();
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
    recoverDeleted = false,
  } = {},
) {
  const canonical = await inspectKeyVaultSecretResult(
    vaultName,
    REPO_APP_PRIVATE_KEY_SECRET.physicalName,
    { exec },
  );
  if (canonical.status === "available") return "available";
  if (canonical.status === "recoverable") {
    if (!recoverDeleted) {
      throw new Error(
        `Canonical Repo App private-key secret '${REPO_APP_PRIVATE_KEY_SECRET.physicalName}' is soft-deleted. ` +
          "Normal deployment will not reactivate old credentials. Suspend or revoke workload access, then " +
          "rerun with the explicit --recover-repo-app-private-key operator flag.",
      );
    }
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
  const stagedSourceFile = params?.stagedSourceFile ?? "";
  const recoverDeleted = params?.recoverDeleted === true;
  if (!vaultName) throw new Error("KEYVAULT_NAME is required to verify the Repo App private key.");

  const configuredFile = String(stagedSourceFile || sourceFile || "").trim();
  if (configuredFile) {
    const resolvedFile = path.resolve(configuredFile);
    if (stagedSourceFile) {
      await setStagedSecretFileWithRetry(
        vaultName,
        REPO_APP_PRIVATE_KEY_SECRET.physicalName,
        resolvedFile,
        { exec, log, fsImpl, sleep, recoverDeleted },
      );
    } else {
      await setSecretFileWithRetry(
        vaultName,
        REPO_APP_PRIVATE_KEY_SECRET.physicalName,
        resolvedFile,
        { exec, log, fsImpl, sleep, recoverDeleted },
      );
    }
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

  const canonicalStatus = await reconcileCanonicalSecret(
    vaultName,
    { exec, log, sleep, recoverDeleted },
  );
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

  const canonicalAfterLegacyCheck = await reconcileCanonicalSecret(
    vaultName,
    { exec, log, sleep, recoverDeleted },
  );
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
      "The configured-file import is one-shot and intentionally replaces the canonical secret. Serialize that " +
      "explicit import in CI. After it succeeds, unset REPO_APP_PRIVATE_KEY_FILE. Remove it from every params " +
      "file used for the import. Then delete the protected local file.",
  );
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
