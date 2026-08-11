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
// MONITORING_LOCATION,
// KEYVAULT_NAME, CLUSTER_NAME, IDENTITY_CLIENT_ID (optional -- workspace role
// assignment is skipped with a warning when absent, matching the bash
// script's `[[ -z "${IDENTITY_CLIENT_ID:-}" ]]` guard).

import os from "node:os";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as secretDefault from "../lib/secret.mjs";

export const LOG_ANALYTICS_WORKSPACE_NAME = "agentweaver-logs";
export const APP_INSIGHTS_NAME = "agentweaver-insights";
const MONITORING_RESOURCE_TYPES = Object.freeze([
  {
    namespace: "Microsoft.OperationalInsights",
    resourceType: "workspaces",
    label: "Log Analytics workspaces",
  },
  {
    namespace: "Microsoft.Insights",
    resourceType: "components",
    label: "Application Insights components",
  },
]);

function normalizeLocation(value) {
  return String(value ?? "").toLowerCase().replace(/[^a-z0-9]/g, "");
}

function flattenLocationStrings(value) {
  if (typeof value === "string") return [value];
  if (!Array.isArray(value)) return [];
  return value.flatMap(flattenLocationStrings);
}

function parseJson(stdout) {
  try {
    return JSON.parse(stdout);
  } catch {
    return null;
  }
}

function canonicalLocation(raw, metadataByAlias) {
  const normalized = normalizeLocation(raw);
  return metadataByAlias.get(normalized)?.name ?? normalized;
}

function locationMetadataByAlias(locations) {
  const byAlias = new Map();
  for (const location of locations) {
    if (!location?.name) continue;
    for (const alias of [location.name, location.displayName, location.regionalDisplayName]) {
      if (alias) byAlias.set(normalizeLocation(alias), location);
    }
  }
  return byAlias;
}

async function querySupportedLocations(exec, resource) {
  const result = await exec.capture(
    "az",
    [
      "provider",
      "show",
      "--namespace",
      resource.namespace,
      "--query",
      `resourceTypes[?resourceType=='${resource.resourceType}'] | [0].locations`,
      "--output",
      "json",
    ],
    { allowFailure: true },
  );
  if (result.dryRun) return { dryRun: true, locations: [] };
  const parsed = parseJson(result.stdout);
  return {
    dryRun: false,
    locations: result.code === 0 ? flattenLocationStrings(parsed) : [],
  };
}

async function queryLocationMetadata(exec) {
  const result = await exec.capture("az", ["account", "list-locations", "--output", "json"], {
    allowFailure: true,
  });
  if (result.dryRun || result.code !== 0) return [];
  const parsed = parseJson(result.stdout);
  return Array.isArray(parsed) ? parsed : [];
}

function fallbackRank(candidate, requested, metadataByAlias) {
  const requestedMetadata = metadataByAlias.get(normalizeLocation(requested));
  const candidateMetadata = metadataByAlias.get(normalizeLocation(candidate));
  const requestedPaired = new Set(
    (requestedMetadata?.metadata?.pairedRegion ?? []).map((region) => normalizeLocation(region?.name)),
  );
  const euapBase = normalizeLocation(requested).replace(/euap$/, "");

  if (normalizeLocation(candidate) === euapBase) return 0;
  if (requestedPaired.has(normalizeLocation(candidate))) return 1;
  if (
    requestedMetadata?.metadata?.geographyGroup &&
    candidateMetadata?.metadata?.geographyGroup === requestedMetadata.metadata.geographyGroup
  ) {
    return 2;
  }
  return 3;
}

/**
 * Selects one Azure region that supports every monitoring resource that still
 * needs to be created. Existing resources are deliberately excluded so
 * upgrades never try to move or recreate them.
 */
export async function selectMonitoringLocation(requestedLocation, resources, { exec, log }) {
  const requested = String(requestedLocation ?? "").trim();
  const supportResults = await Promise.all(resources.map((resource) => querySupportedLocations(exec, resource)));
  if (supportResults.some((result) => result.dryRun)) {
    log.info(`Dry-run: using configured monitoring location '${requested}' without live provider discovery.`);
    return requested;
  }

  const metadata = await queryLocationMetadata(exec);
  const metadataByAlias = locationMetadataByAlias(metadata);
  const supportSets = supportResults.map((result) =>
    new Set(result.locations.map((location) => canonicalLocation(location, metadataByAlias))),
  );

  if (supportSets.some((set) => set.size === 0)) {
    log.warn(
      `Azure provider location metadata was unavailable for ${resources
        .filter((_, index) => supportSets[index].size === 0)
        .map((resource) => resource.label)
        .join(" and ")}; attempting configured monitoring location '${requested}'.`,
    );
    return requested;
  }

  const requestedCanonical = canonicalLocation(requested, metadataByAlias);
  if (supportSets.every((set) => set.has(requestedCanonical))) return requestedCanonical;

  const common = [...supportSets[0]].filter((location) => supportSets.every((set) => set.has(location)));
  if (common.length === 0) {
    const details = resources
      .map((resource, index) => `${resource.label}: ${[...supportSets[index]].sort().join(", ")}`)
      .join("; ");
    throw new Error(
      `No common Azure region supports all monitoring resources that must be created. ${details}. ` +
        "Set MONITORING_LOCATION (or --monitoring-location) after verifying provider availability.",
    );
  }

  common.sort((a, b) => {
    const rankDifference = fallbackRank(a, requested, metadataByAlias) - fallbackRank(b, requested, metadataByAlias);
    return rankDifference || a.localeCompare(b);
  });
  const selected = common[0];
  log.warn(
    `Monitoring location '${requested}' is unavailable for ${resources.map((resource) => resource.label).join(" and ")}. ` +
      `Using Azure-supported fallback '${selected}'. Existing monitoring resources are never moved.`,
  );
  return selected;
}

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
  const requestedMonitoringLocation = cfg.MONITORING_LOCATION || cfg.LOCATION;
  log.field("AKS location", cfg.LOCATION);
  log.field("Monitoring location preference", requestedMonitoringLocation);
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
  const appInsightsShow = await exec.capture(
    "az",
    ["monitor", "app-insights", "component", "show", "--app", APP_INSIGHTS_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  const missingResources = [];
  if (workspaceShow.code !== 0) missingResources.push(MONITORING_RESOURCE_TYPES[0]);
  if (appInsightsShow.code !== 0) missingResources.push(MONITORING_RESOURCE_TYPES[1]);
  const monitoringLocation =
    missingResources.length > 0
      ? await selectMonitoringLocation(requestedMonitoringLocation, missingResources, { exec, log })
      : requestedMonitoringLocation;
  if (missingResources.length > 0) log.field("Selected monitoring location", monitoringLocation);

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
      monitoringLocation,
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
      monitoringLocation,
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
