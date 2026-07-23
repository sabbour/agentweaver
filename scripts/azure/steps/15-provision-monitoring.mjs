// 15-provision-monitoring.mjs -- Faithful Node port of
// scripts/aks/15-provision-monitoring.sh (cross-checked against
// 15-provision-monitoring.ps1). Read both before changing this file; they
// must stay in lockstep with this port's behavior.
//
// Provisions Application Insights + a Log Analytics workspace, grants the
// workload identity Log Analytics Reader, stores the AppInsights connection
// string in Key Vault, and enables AKS Managed Prometheus. Idempotent.
//
// INTEGRATION NOTE (P2->P4 handoff): steps/30-deploy.mjs calls this module's
// run() directly (no more shelling out via run-os-script.mjs to the legacy
// bash/PowerShell version) -- see that file's monitoring-provisioning check.
//
// cfg is the resolved variables.mjs output: RESOURCE_GROUP, LOCATION,
// KEYVAULT_NAME, CLUSTER_NAME, IDENTITY_CLIENT_ID (optional -- workspace role
// assignment is skipped with a warning when absent, matching the bash
// script's `[[ -z "${IDENTITY_CLIENT_ID:-}" ]]` guard).

import os from "node:os";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as secretDefault from "../lib/secret.mjs";

export const LOG_ANALYTICS_WORKSPACE_NAME = "agentweaver-logs";
export const APP_INSIGHTS_NAME = "agentweaver-insights";

/**
 * Provisions Application Insights + AKS Managed Prometheus: faithful port of
 * 15-provision-monitoring.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, secret = secretDefault, scratchDir = os.tmpdir() } = opts;

  log.info("");
  log.section("Provision Monitoring");
  log.field("Resource Group", cfg.RESOURCE_GROUP);
  log.field("Location", cfg.LOCATION);
  log.field("Key Vault", cfg.KEYVAULT_NAME);
  log.field("Cluster", cfg.CLUSTER_NAME);
  log.info("");

  // -- 1. Log Analytics workspace --
  log.info(`Ensuring Log Analytics workspace '${LOG_ANALYTICS_WORKSPACE_NAME}'...`);
  const workspaceShow = await exec.capture(
    "az",
    ["monitor", "log-analytics", "workspace", "show", "--resource-group", cfg.RESOURCE_GROUP, "--workspace-name", LOG_ANALYTICS_WORKSPACE_NAME],
    { allowFailure: true },
  );
  if (workspaceShow.code === 0) {
    log.ok("Log Analytics workspace already exists.");
  } else {
    await exec.run("az", [
      "monitor",
      "log-analytics",
      "workspace",
      "create",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--workspace-name",
      LOG_ANALYTICS_WORKSPACE_NAME,
      "--location",
      cfg.LOCATION,
    ]);
    log.info("  [created] Log Analytics workspace.");
  }

  const { stdout: WORKSPACE_RESOURCE_ID } = await exec.capture("az", [
    "monitor",
    "log-analytics",
    "workspace",
    "show",
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--workspace-name",
    LOG_ANALYTICS_WORKSPACE_NAME,
    "--query",
    "id",
    "--output",
    "tsv",
  ]);
  const { stdout: APPINSIGHTS_WORKSPACE_ID } = await exec.capture("az", [
    "monitor",
    "log-analytics",
    "workspace",
    "show",
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--workspace-name",
    LOG_ANALYTICS_WORKSPACE_NAME,
    "--query",
    "customerId",
    "--output",
    "tsv",
  ]);

  // -- 2. Grant workload identity read access to Log Analytics --
  log.info(`Granting Log Analytics Reader on '${LOG_ANALYTICS_WORKSPACE_NAME}' to workload identity...`);
  if (!cfg.IDENTITY_CLIENT_ID) {
    log.warn("IDENTITY_CLIENT_ID is not set; skipping workspace role assignment.");
  } else {
    const { stdout: IDENTITY_OBJECT_ID } = await exec.capture(
      "az",
      ["identity", "list", "--resource-group", cfg.RESOURCE_GROUP, "--query", `[?clientId=='${cfg.IDENTITY_CLIENT_ID}'].principalId | [0]`, "--output", "tsv"],
      { allowFailure: true },
    );
    if (!IDENTITY_OBJECT_ID.trim()) {
      log.warn(`No managed identity found for IDENTITY_CLIENT_ID='${cfg.IDENTITY_CLIENT_ID}'; skipping workspace role assignment.`);
    } else {
      const { stdout: existingAssignments } = await exec.capture("az", [
        "role",
        "assignment",
        "list",
        "--assignee-object-id",
        IDENTITY_OBJECT_ID,
        "--scope",
        WORKSPACE_RESOURCE_ID,
        "--role",
        "Log Analytics Reader",
        "--query",
        "length(@)",
        "--output",
        "tsv",
      ]);
      if (existingAssignments.trim() === "0") {
        await exec.run("az", [
          "role",
          "assignment",
          "create",
          "--role",
          "Log Analytics Reader",
          "--assignee-object-id",
          IDENTITY_OBJECT_ID,
          "--assignee-principal-type",
          "ServicePrincipal",
          "--scope",
          WORKSPACE_RESOURCE_ID,
          "--output",
          "none",
        ]);
        log.info("  [granted] Log Analytics Reader on workspace.");
      } else {
        log.ok("Log Analytics Reader already assigned.");
      }
    }
  }

  // -- 3. Application Insights (workspace-based) --
  log.info(`Ensuring Application Insights '${APP_INSIGHTS_NAME}'...`);
  const appInsightsShow = await exec.capture(
    "az",
    ["monitor", "app-insights", "component", "show", "--app", APP_INSIGHTS_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  if (appInsightsShow.code === 0) {
    log.ok("Application Insights already exists.");
  } else {
    await exec.run("az", [
      "monitor",
      "app-insights",
      "component",
      "create",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--app",
      APP_INSIGHTS_NAME,
      "--location",
      cfg.LOCATION,
      "--kind",
      "web",
      "--workspace",
      LOG_ANALYTICS_WORKSPACE_NAME,
    ]);
    log.info("  [created] Application Insights.");
  }

  // -- 4. Store connection string in Key Vault --
  log.info("Storing AppInsights connection string in Key Vault...");
  const { stdout: CONN_STR } = await exec.capture("az", [
    "monitor",
    "app-insights",
    "component",
    "show",
    "--app",
    APP_INSIGHTS_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "connectionString",
    "--output",
    "tsv",
  ]);
  await secret.withSecretFile(scratchDir, "appinsights-connection-string", CONN_STR, (filePath) =>
    exec.capture("az", [
      "keyvault",
      "secret",
      "set",
      "--vault-name",
      cfg.KEYVAULT_NAME,
      "--name",
      "appinsights-connection-string",
      "--file",
      filePath,
      "--output",
      "none",
    ]),
  );
  log.info("  [stored] appinsights-connection-string in Key Vault.");

  // -- 5. Enable AKS Managed Prometheus --
  log.info(`Enabling AKS Managed Prometheus on cluster '${cfg.CLUSTER_NAME}'...`);
  await exec.run("az", ["aks", "update", "--resource-group", cfg.RESOURCE_GROUP, "--name", cfg.CLUSTER_NAME, "--enable-azure-monitor-metrics"]);
  log.info("  [enabled] AKS Managed Prometheus.");

  log.info("");
  log.section("Monitoring provisioning complete");
  log.info("  Application Insights connection string stored as 'appinsights-connection-string' in Key Vault.");
  log.info(`  Log Analytics workspace customerId: ${APPINSIGHTS_WORKSPACE_ID}`);
  log.info(`  AKS Managed Prometheus enabled on cluster '${cfg.CLUSTER_NAME}'.`);

  return { APPINSIGHTS_WORKSPACE_ID, WORKSPACE_RESOURCE_ID };
}
