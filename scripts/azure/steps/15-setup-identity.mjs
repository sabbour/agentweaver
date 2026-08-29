// 15-setup-identity.mjs -- Faithful Node port of scripts/aks/15-setup-identity.sh
// (cross-checked against 15-setup-identity.ps1). Read both before changing
// this file; they must stay in lockstep with this port's behavior.
//
// Creates: user-assigned managed identities (one shared by the API and worker
// service accounts, one dedicated least-privilege identity for AgentHost sandbox
// pods with NO Key Vault roles -- issue #471), Key Vault (RBAC-authorized),
// Key Vault role assignments (API/worker identity only),
// OIDC issuer + workload identity on the cluster, and federated credentials for
// the api, worker, and agent-host service accounts (agent-host on its own
// dedicated identity).
//
// cfg is the resolved variables.mjs output: RESOURCE_GROUP, CLUSTER_NAME,
// LOCATION, KEYVAULT_NAME, NAMESPACE, TENANT_ID.

import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as azDefault from "../lib/az.mjs";
import * as secretDefault from "../lib/secret.mjs";
import os from "node:os";

export const IDENTITY_NAME = "agentweaver-api-identity";

// Dedicated, least-privilege managed identity for the AgentHost sandbox pods (issue #471).
// This identity is granted NO Key Vault roles: the sandbox executes untrusted shell/tool code,
// so it must NOT be able to exchange its projected workload-identity token for a Key Vault access
// token and read every user's secrets. The run owner's GitHub token is brokered per-run through the
// API's /configure call (see KubernetesSandboxExecutor.ResolveGitHubAccessTokenAsync ->
// AgentHostRuntimeState.GitHubAccessToken) instead of a direct vault fetch.
export const AGENTHOST_IDENTITY_NAME = "agentweaver-agenthost-identity";

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
  const { exec = execDefault, log = logDefault, az = azDefault } = opts;

  let TENANT_ID = cfg.TENANT_ID;
  if (!TENANT_ID) {
    TENANT_ID = await az.getTenantId();
  }

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
  log.section("Step 1b: Create dedicated AgentHost managed identity (no Key Vault roles)");
  // Least-privilege identity for the sandbox pods (issue #471). Deliberately kept separate from the
  // API identity and NEVER granted Key Vault roles below, so a compromised/abused sandbox cannot read
  // other users' secrets. The AgentHost pod receives the run owner's token via the API /configure
  // broker instead of a direct vault fetch.
  await exec.run("az", [
    "identity",
    "create",
    "--name",
    AGENTHOST_IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--location",
    cfg.LOCATION,
  ]);

  const { stdout: AGENTHOST_IDENTITY_CLIENT_ID } = await exec.capture("az", [
    "identity",
    "show",
    "--name",
    AGENTHOST_IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "clientId",
    "-o",
    "tsv",
  ]);
  const { stdout: AGENTHOST_IDENTITY_OBJECT_ID } = await exec.capture("az", [
    "identity",
    "show",
    "--name",
    AGENTHOST_IDENTITY_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "principalId",
    "-o",
    "tsv",
  ]);
  log.field("AgentHost identity client ID", AGENTHOST_IDENTITY_CLIENT_ID);
  log.field("AgentHost identity object ID", AGENTHOST_IDENTITY_OBJECT_ID);

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
  log.section("Step 3: Grant Key Vault roles to managed identity");
  // These roles are granted to the API identity ONLY. The AgentHost identity
  // (AGENTHOST_IDENTITY_NAME) is intentionally excluded (issue #471): sandbox pods must have no
  // direct Key Vault access and instead receive the run owner's token via the API /configure broker.
  await createRoleAssignmentIdempotent(
    ["--role", "Key Vault Secrets User", "--assignee-object-id", IDENTITY_OBJECT_ID, "--assignee-principal-type", "ServicePrincipal", "--scope", KEYVAULT_ID],
    { exec },
  );
  await createRoleAssignmentIdempotent(
    ["--role", "Key Vault Secrets Officer", "--assignee-object-id", IDENTITY_OBJECT_ID, "--assignee-principal-type", "ServicePrincipal", "--scope", KEYVAULT_ID],
    { exec },
  );

  log.info("");
  log.section("Step 4: Enable OIDC issuer + workload identity on cluster");
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
  log.section("Step 5: Create federated credential");
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
  log.section("Step 8: Create federated credential for agent-host (dedicated identity)");
  // issue #471: the agent-host federated credential lives on the DEDICATED, Key-Vault-less
  // AGENTHOST_IDENTITY_NAME — NOT the API identity — so the sandbox's workload-identity token maps to
  // an identity with no vault access. Migration: if the legacy fedcred still exists on the API
  // identity (older deployments federated agentweaver-agent-host to agentweaver-api-identity), delete
  // it so the sandbox can no longer assume the KV-privileged API identity.
  const legacyAgentHostFedCred = await exec.capture(
    "az",
    ["identity", "federated-credential", "show", "--name", "agentweaver-agenthost-fedcred", "--identity-name", IDENTITY_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (legacyAgentHostFedCred.code === 0) {
    log.warn(
      "Removing legacy agent-host federated credential from the API identity so the sandbox no " +
        "longer federates to the Key-Vault-privileged agentweaver-api-identity (issue #471).");
    await exec.run("az", [
      "identity",
      "federated-credential",
      "delete",
      "--name",
      "agentweaver-agenthost-fedcred",
      "--identity-name",
      IDENTITY_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--yes",
    ]);
  }

  const agentHostFedCredExists = await exec.capture(
    "az",
    ["identity", "federated-credential", "show", "--name", "agentweaver-agenthost-fedcred", "--identity-name", AGENTHOST_IDENTITY_NAME, "--resource-group", cfg.RESOURCE_GROUP],
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
      AGENTHOST_IDENTITY_NAME,
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
  log.field("AGENTHOST_IDENTITY_CLIENT_ID", AGENTHOST_IDENTITY_CLIENT_ID);
  log.field("KEYVAULT_NAME", cfg.KEYVAULT_NAME);
  log.field("TENANT_ID", TENANT_ID);
  log.info("");
  log.info("Federated credentials are now configured on two separate identities:");
  log.info(`  agentweaver-api-identity      / agentweaver-api-fedcred      -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-api`);
  log.info(`  agentweaver-api-identity      / agentweaver-worker-fedcred   -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-worker`);
  log.info(`  agentweaver-agenthost-identity / agentweaver-agenthost-fedcred -> system:serviceaccount:${cfg.NAMESPACE}:agentweaver-agent-host`);
  log.info("");
  log.info("The AgentHost identity has NO Key Vault role assignments (issue #471): the run owner's");
  log.info("GitHub token is brokered per-run through the API /configure call, not a direct vault fetch.");
  log.info("");

  return {
    IDENTITY_CLIENT_ID,
    IDENTITY_OBJECT_ID,
    AGENTHOST_IDENTITY_CLIENT_ID,
    AGENTHOST_IDENTITY_OBJECT_ID,
    KEYVAULT_ID,
    TENANT_ID,
    OIDC_ISSUER,
  };
}
