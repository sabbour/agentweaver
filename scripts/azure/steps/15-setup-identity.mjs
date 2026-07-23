// 15-setup-identity.mjs -- Faithful Node port of scripts/aks/15-setup-identity.sh
// (cross-checked against 15-setup-identity.ps1). Read both before changing
// this file; they must stay in lockstep with this port's behavior.
//
// Creates: user-assigned managed identity, Key Vault (RBAC-authorized),
// GitHub OAuth secrets, Key Vault role assignments, OIDC issuer + workload
// identity on the cluster, and federated credentials for the api, worker,
// and agent-host service accounts.
//
// SECURITY NOTE (see .squad/decisions.md "Staging AKS recovery" entry): this
// port intentionally does NOT auto-resolve GitHub OAuth credentials from any
// local source (e.g. .NET user-secrets) -- that incident showed a
// local-development-only credential source is unsafe for staging, which uses
// a separate GitHub OAuth App. Credentials must come from env/flags or an
// explicit interactive prompt.
//
// cfg is the resolved variables.mjs output: RESOURCE_GROUP, CLUSTER_NAME,
// LOCATION, KEYVAULT_NAME, NAMESPACE, TENANT_ID. Optional:
// GITHUB_CLIENT_ID/GITHUB_CLIENT_SECRET (prompted interactively if omitted
// and a TTY is available).

import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as azDefault from "../lib/az.mjs";
import * as promptDefault from "../lib/prompt.mjs";
import * as secretDefault from "../lib/secret.mjs";
import os from "node:os";

export const IDENTITY_NAME = "agentweaver-api-identity";

/** Resolves GitHub OAuth credentials from cfg, falling back to an interactive prompt when available. */
export async function resolveGithubCredentials(cfg, { prompt = promptDefault } = {}) {
  let clientId = cfg.GITHUB_CLIENT_ID || "";
  let clientSecret = cfg.GITHUB_CLIENT_SECRET || "";

  if ((!clientId || !clientSecret) && prompt.isInteractive()) {
    if (!clientId) {
      clientId = await prompt.text("GitHub OAuth client ID: ");
    }
    if (!clientSecret) {
      clientSecret = await prompt.secret("GitHub OAuth client secret: ");
    }
  }

  const missing = [];
  if (!clientId) missing.push("GITHUB_CLIENT_ID");
  if (!clientSecret) missing.push("GITHUB_CLIENT_SECRET");
  if (missing.length > 0) {
    throw new Error(
      `GitHub OAuth credentials are missing. Set the following variables (or supply via flags): ${missing.join(", ")}`,
    );
  }
  return { clientId, clientSecret };
}

/**
 * Sets a Key Vault secret, tolerating transient RBAC-propagation Forbidden errors with bounded retry.
 * Writes `value` to a short-lived private (0600) scratch file and passes it via `az`'s '--file'
 * parameter instead of '--value', so the secret never appears in this process's argv -- argv is
 * readable by any co-resident process/user via `ps`/`/proc/<pid>/cmdline` for the command's entire
 * runtime, unlike a file that's deleted immediately after the command exits.
 */
export async function setSecretWithRetry(
  keyvaultName,
  name,
  value,
  { exec = execDefault, log = logDefault, maxAttempts = 12, sleep = defaultSleep, secret = secretDefault, scratchDir = os.tmpdir() } = {},
) {
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const result = await secret.withSecretFile(scratchDir, `kv-secret-${name}`, value, (filePath) =>
      exec.capture(
        "az",
        ["keyvault", "secret", "set", "--vault-name", keyvaultName, "--name", name, "--file", filePath, "--output", "none"],
        { allowFailure: true },
      ),
    );
    if (result.code === 0) return;
    const isRbacPropagating = /Forbidden|ForbiddenByRbac|not authorized/i.test(result.stderr || "");
    if (isRbacPropagating && attempt < maxAttempts) {
      log.info(`  [retry ${attempt}/${maxAttempts}] RBAC role for '${name}' still propagating; waiting 15s...`);
      await sleep(15000);
      continue;
    }
    throw new Error(`Failed to set Key Vault secret '${name}': ${result.stderr}`);
  }
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Grants a role assignment, tolerating an "already exists" response (idempotent). */
async function createRoleAssignmentIdempotent(args, { exec = execDefault } = {}) {
  const result = await exec.capture("az", ["role", "assignment", "create", ...args], { allowFailure: true });
  if (result.code !== 0 && !/already exists/i.test(result.stderr || "")) {
    throw new Error(`role assignment failed: ${result.stderr}`);
  }
}

/**
 * Creates managed identity, Key Vault, workload identity, and secrets:
 * faithful port of 15-setup-identity.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, az = azDefault, prompt = promptDefault } = opts;

  let TENANT_ID = cfg.TENANT_ID;
  if (!TENANT_ID) {
    TENANT_ID = await az.getTenantId();
  }

  const { clientId: GITHUB_CLIENT_ID, clientSecret: GITHUB_CLIENT_SECRET } = await resolveGithubCredentials(cfg, {
    prompt,
  });

  log.info("");
  log.section("Step 1: Create user-assigned managed identity");
  await exec.run("az", [
    "identity",
    "create",
    "--name",
    IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--location",
    cfg.LOCATION,
  ]);

  const { stdout: IDENTITY_CLIENT_ID } = await exec.capture("az", [
    "identity",
    "show",
    "--name",
    IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "clientId",
    "-o",
    "tsv",
  ]);
  const { stdout: IDENTITY_OBJECT_ID } = await exec.capture("az", [
    "identity",
    "show",
    "--name",
    IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "principalId",
    "-o",
    "tsv",
  ]);
  log.field("Identity client ID", IDENTITY_CLIENT_ID);
  log.field("Identity object ID", IDENTITY_OBJECT_ID);

  log.info("");
  log.section("Step 2: Create Key Vault");
  const kvShow = await exec.capture(
    "az",
    ["keyvault", "show", "--name", cfg.KEYVAULT_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (kvShow.code !== 0) {
    await exec.run("az", [
      "keyvault",
      "create",
      "--name",
      cfg.KEYVAULT_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--location",
      cfg.LOCATION,
      "--enable-rbac-authorization",
    ]);
  } else {
    log.ok(`Key Vault '${cfg.KEYVAULT_NAME}' already exists.`);
  }

  const { stdout: KEYVAULT_ID } = await exec.capture("az", ["keyvault", "show", "--name", cfg.KEYVAULT_NAME, "--query", "id", "-o", "tsv"]);
  log.field("Key Vault ID", KEYVAULT_ID);

  log.info("");
  log.section("Step 2b: Grant provisioning caller data-plane secret access");
  let callerOid = (await exec.capture("az", ["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], { allowFailure: true })).stdout.trim();
  let callerPtype = "User";
  if (!callerOid) {
    const callerAppId = (
      await exec.capture("az", ["account", "show", "--query", "user.name", "-o", "tsv"], { allowFailure: true })
    ).stdout.trim();
    if (callerAppId) {
      callerOid = (
        await exec.capture("az", ["ad", "sp", "show", "--id", callerAppId, "--query", "id", "-o", "tsv"], { allowFailure: true })
      ).stdout.trim();
      callerPtype = "ServicePrincipal";
    }
  }
  if (callerOid) {
    log.info(`  Granting 'Key Vault Secrets Officer' to caller ${callerOid} (${callerPtype})...`);
    await createRoleAssignmentIdempotent(
      ["--role", "Key Vault Secrets Officer", "--assignee-object-id", callerOid, "--assignee-principal-type", callerPtype, "--scope", KEYVAULT_ID],
      { exec },
    );
  } else {
    log.warn("Could not resolve caller object ID; relying on ambient Key Vault permissions.");
  }

  log.info("");
  log.section("Step 3: Store required secrets in Key Vault");
  await setSecretWithRetry(cfg.KEYVAULT_NAME, "github-client-id", GITHUB_CLIENT_ID, { exec, log });
  await setSecretWithRetry(cfg.KEYVAULT_NAME, "github-client-secret", GITHUB_CLIENT_SECRET, { exec, log });

  log.info("");
  log.section("Step 4: Grant Key Vault roles to managed identity");
  await createRoleAssignmentIdempotent(
    ["--role", "Key Vault Secrets User", "--assignee-object-id", IDENTITY_OBJECT_ID, "--assignee-principal-type", "ServicePrincipal", "--scope", KEYVAULT_ID],
    { exec },
  );
  await createRoleAssignmentIdempotent(
    ["--role", "Key Vault Secrets Officer", "--assignee-object-id", IDENTITY_OBJECT_ID, "--assignee-principal-type", "ServicePrincipal", "--scope", KEYVAULT_ID],
    { exec },
  );

  log.info("");
  log.section("Step 5: Enable OIDC issuer + workload identity on cluster");
  const oidcEnabled = (
    await exec.capture("az", ["aks", "show", "--name", cfg.CLUSTER_NAME, "--resource-group", cfg.RESOURCE_GROUP, "--query", "oidcIssuerProfile.enabled", "-o", "tsv"], { allowFailure: true })
  ).stdout.trim();
  const wiEnabled = (
    await exec.capture("az", ["aks", "show", "--name", cfg.CLUSTER_NAME, "--resource-group", cfg.RESOURCE_GROUP, "--query", "securityProfile.workloadIdentity.enabled", "-o", "tsv"], { allowFailure: true })
  ).stdout.trim();
  if (oidcEnabled === "true" && wiEnabled === "true") {
    log.skip("OIDC issuer and workload identity already enabled.");
  } else {
    await exec.run("az", [
      "aks",
      "update",
      "--name",
      cfg.CLUSTER_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--enable-oidc-issuer",
      "--enable-workload-identity",
    ]);
  }

  const { stdout: OIDC_ISSUER } = await exec.capture("az", [
    "aks",
    "show",
    "--name",
    cfg.CLUSTER_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "oidcIssuerProfile.issuerUrl",
    "-o",
    "tsv",
  ]);
  log.field("OIDC issuer", OIDC_ISSUER);

  log.info("");
  log.section("Step 6: Create federated credential");
  const fedCredExists = await exec.capture(
    "az",
    ["identity", "federated-credential", "show", "--name", "agentweaver-api-fedcred", "--identity-name", IDENTITY_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (fedCredExists.code !== 0) {
    await exec.run("az", [
      "identity",
      "federated-credential",
      "create",
      "--name",
      "agentweaver-api-fedcred",
      "--identity-name",
      IDENTITY_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--issuer",
      OIDC_ISSUER,
      "--subject",
      `system:serviceaccount:${cfg.NAMESPACE}:agentweaver-api`,
      "--audience",
      "api://AzureADTokenExchange",
    ]);
  } else {
    log.ok("Federated credential already exists.");
  }

  log.info("");
  log.section("Step 7: Create federated credential for worker");
  const workerFedCredExists = await exec.capture(
    "az",
    ["identity", "federated-credential", "show", "--name", "agentweaver-worker-fedcred", "--identity-name", IDENTITY_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (workerFedCredExists.code !== 0) {
    await exec.run("az", [
      "identity",
      "federated-credential",
      "create",
      "--name",
      "agentweaver-worker-fedcred",
      "--identity-name",
      IDENTITY_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--issuer",
      OIDC_ISSUER,
      "--subject",
      `system:serviceaccount:${cfg.NAMESPACE}:agentweaver-worker`,
      "--audience",
      "api://AzureADTokenExchange",
    ]);
  } else {
    log.ok("Worker federated credential already exists.");
  }

  log.info("");
  log.section("Step 8: Create federated credential for agent-host");
  const agentHostFedCredExists = await exec.capture(
    "az",
    ["identity", "federated-credential", "show", "--name", "agentweaver-agenthost-fedcred", "--identity-name", IDENTITY_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (agentHostFedCredExists.code !== 0) {
    await exec.run("az", [
      "identity",
      "federated-credential",
      "create",
      "--name",
      "agentweaver-agenthost-fedcred",
      "--identity-name",
      IDENTITY_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--issuer",
      OIDC_ISSUER,
      "--subject",
      `system:serviceaccount:${cfg.NAMESPACE}:agentweaver-agent-host`,
      "--audience",
      "api://AzureADTokenExchange",
    ]);
  } else {
    log.ok("Agent-host federated credential already exists.");
  }

  log.info("");
  log.section("Summary");
  log.field("IDENTITY_CLIENT_ID", IDENTITY_CLIENT_ID);
  log.field("KEYVAULT_NAME", cfg.KEYVAULT_NAME);
  log.field("TENANT_ID", TENANT_ID);
  log.info("");
  log.info("Three federated credentials are now configured on agentweaver-api-identity:");
  log.info(`  agentweaver-api-fedcred      -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-api`);
  log.info(`  agentweaver-worker-fedcred   -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-worker`);
  log.info(`  agentweaver-agenthost-fedcred -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-agent-host`);
  log.info("");
  log.info("NOTE: Run scripts/azure/steps/16-provision-oauth-signing-key.mjs before the first");
  log.info("      deploy to provision the mcp-oauth-signing-key secret in Key Vault.");

  return { IDENTITY_CLIENT_ID, IDENTITY_OBJECT_ID, KEYVAULT_ID, TENANT_ID, OIDC_ISSUER };
}
