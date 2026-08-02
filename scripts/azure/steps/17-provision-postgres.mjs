// 17-provision-postgres.mjs -- Faithful Node port of
// scripts/aks/17-provision-postgres.sh (cross-checked against
// 17-provision-postgres.ps1). Read both before changing this file; they must
// stay in lockstep with this port's behavior.
//
// Provisions Azure Database for PostgreSQL Flexible Server with either
// private VNet access (delegated subnet + private DNS zone + VNet link) or
// Azure-public access, and stores the admin credentials in a K8s Secret
// ('agentweaver-postgres'). Idempotent at every step. The generated admin
// password is registered with lib/secret.mjs's redaction registry
// immediately and NEVER logged.
//
// cfg is the resolved variables.mjs output: RESOURCE_GROUP, CLUSTER_NAME,
// LOCATION, NAMESPACE. Optional PG_* overrides (server name, location, access
// mode, DB name, admin user, version, SKU, storage, HA mode, backup days,
// subnet name/prefix, DNS zone/link name) and AKS_VNET_NAME/AKS_MC_RG
// (auto-detected if unset).

import fs from "node:fs";
import path from "node:path";
import { randomBytes } from "node:crypto";
import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";
import * as secretDefault from "../lib/secret.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

const PG_DEFAULTS = Object.freeze({
  PG_SERVER_NAME: "agentweaver-pg",
  PG_ACCESS_MODE: "private",
  PG_DB_NAME: "agentweaver",
  PG_ADMIN_USER: "pgadmin",
  PG_VERSION: "16",
  PG_SKU: "Standard_D2ds_v4",
  PG_STORAGE_GB: "32",
  PG_HA_MODE: "ZoneRedundant",
  PG_BACKUP_DAYS: "7",
  PG_SUBNET_NAME: "aks-postgres",
  PG_SUBNET_PREFIX: "10.225.0.0/28",
  PG_DNS_ZONE: "privatelink.postgres.database.azure.com",
  PG_DNS_LINK_NAME: "agentweaver-pg-dns-link",
});

function pgOption(cfg, name) {
  return cfg[name] || PG_DEFAULTS[name];
}

function isEnabledLike(value) {
  if (typeof value === "boolean") return value;
  if (value == null) return false;
  return String(value).trim().toLowerCase() === "enabled";
}

function hasRestrictionSignal(value) {
  if (value == null) return false;
  const text = String(value).trim().toLowerCase();
  if (!text) return false;
  return text.includes("restricted") || text.includes("offerrestricted") || text.includes("notavailable");
}

function collectRestrictionReasons(...values) {
  return values
    .flatMap((value) => (Array.isArray(value) ? value : [value]))
    .filter((value) => value != null)
    .map((value) => String(value).trim())
    .filter(Boolean);
}

function firstNonEmptyReason(...values) {
  return collectRestrictionReasons(...values)[0] || "";
}

function parseSkuAvailability(capabilities, skuName) {
  const supportedServerEditions = Array.isArray(capabilities?.supportedServerEditions)
    ? capabilities.supportedServerEditions
    : [];

  if (supportedServerEditions.length === 0) {
    return {
      ok: false,
      reason:
        firstNonEmptyReason(
          capabilities?.reason,
          capabilities?.status,
          capabilities?.restricted,
          capabilities?.supportedFeatures
            ?.filter?.((feature) => hasRestrictionSignal(feature?.name) || isEnabledLike(feature?.status))
            ?.map?.((feature) => `${feature?.name}: ${feature?.status}`),
        ) || "Azure reported no supported server editions for this subscription/region.",
    };
  }

  const offerRestrictedFeature = Array.isArray(capabilities?.supportedFeatures)
    ? capabilities.supportedFeatures.find(
        (feature) => String(feature?.name ?? "").trim().toLowerCase() === "offerrestricted" && isEnabledLike(feature?.status),
      )
    : null;

  let matchedSku = null;
  let matchedEdition = null;
  for (const edition of supportedServerEditions) {
    const serverSkus = Array.isArray(edition?.supportedServerSkus) ? edition.supportedServerSkus : [];
    const sku = serverSkus.find((candidate) => candidate?.name === skuName);
    if (sku) {
      matchedSku = sku;
      matchedEdition = edition;
      break;
    }
  }

  if (!matchedSku) {
    return {
      ok: false,
      reason:
        firstNonEmptyReason(
          capabilities?.reason,
          offerRestrictedFeature ? `${offerRestrictedFeature.name}: ${offerRestrictedFeature.status}` : "",
        ) || `Azure did not list SKU '${skuName}' in supportedServerSkus for this region.`,
    };
  }

  const reasons = collectRestrictionReasons(
    capabilities?.reason,
    matchedEdition?.reason,
    matchedSku?.reason,
    offerRestrictedFeature ? `${offerRestrictedFeature.name}: ${offerRestrictedFeature.status}` : "",
  );
  const restricted =
    isEnabledLike(capabilities?.restricted) ||
    hasRestrictionSignal(capabilities?.status) ||
    hasRestrictionSignal(matchedEdition?.status) ||
    hasRestrictionSignal(matchedSku?.status) ||
    Boolean(offerRestrictedFeature) ||
    reasons.some((reason) => hasRestrictionSignal(reason));

  return {
    ok: !restricted,
    reason: firstNonEmptyReason(reasons),
  };
}

async function resolveServerFqdn(exec, resourceGroup, serverName) {
  const result = await exec.capture(
    "az",
    [
      "postgres",
      "flexible-server",
      "show",
      "--resource-group",
      resourceGroup,
      "--name",
      serverName,
      "--query",
      "fullyQualifiedDomainName",
      "--output",
      "tsv",
    ],
    { allowFailure: true },
  );
  return result.stdout.trim() || `${serverName}.postgres.database.azure.com`;
}

async function assertSkuProvisionable(exec, location, skuName) {
  const result = await exec.capture(
    "az",
    ["postgres", "flexible-server", "list-skus", "--location", location, "--output", "json"],
    { allowFailure: true },
  );
  if (result.code !== 0) {
    throw new Error(
      `Could not verify PostgreSQL Flexible Server SKU availability for region '${location}' before provisioning. ` +
        `Azure CLI exited with code ${result.code}${result.stderr ? `: ${result.stderr.trim()}` : "."}`,
    );
  }

  let capabilities;
  try {
    capabilities = JSON.parse(result.stdout);
  } catch (error) {
    throw new Error(
      `Could not parse 'az postgres flexible-server list-skus' output for region '${location}' while validating SKU '${skuName}': ${error.message}`,
    );
  }

  const availability = parseSkuAvailability(capabilities, skuName);
  if (!availability.ok) {
    const reasonSuffix = availability.reason ? ` (Azure reason: '${availability.reason}')` : "";
    throw new Error(
      `PostgreSQL Flexible Server SKU '${skuName}' is not available for this subscription in region '${location}'${reasonSuffix}. ` +
        "Try a different --postgres-location or PG_SKU value.",
    );
  }
}

/**
 * Generates a strong random Postgres admin password (48 alphanumeric-ish
 * chars, no shell metacharacters). Uses Node's built-in crypto module
 * (no external 'openssl' process) so this works identically on every
 * platform, including Windows machines without openssl on PATH.
 */
export async function generateAdminPassword() {
  const stdout = randomBytes(48).toString("base64");
  return stdout.replace(/[+=/]/g, "").slice(0, 48);
}

/**
 * Provisions PostgreSQL Flexible Server: faithful port of
 * 17-provision-postgres.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault, secret = secretDefault, fs: fsImpl = fs, repoRoot = DEFAULT_REPO_ROOT } = opts;
  const scratchDir = cfg.AGENTWEAVER_TMP_DIR || path.join(repoRoot, ".agentweaver", "tmp");

  const PG_SERVER_NAME = pgOption(cfg, "PG_SERVER_NAME");
  const PG_LOCATION = cfg.PG_LOCATION || cfg.LOCATION;
  const PG_ACCESS_MODE = pgOption(cfg, "PG_ACCESS_MODE");
  const PG_DB_NAME = pgOption(cfg, "PG_DB_NAME");
  const PG_ADMIN_USER = pgOption(cfg, "PG_ADMIN_USER");
  const PG_VERSION = pgOption(cfg, "PG_VERSION");
  const PG_SKU = pgOption(cfg, "PG_SKU");
  const PG_STORAGE_GB = pgOption(cfg, "PG_STORAGE_GB");
  const PG_HA_MODE = pgOption(cfg, "PG_HA_MODE");
  const PG_BACKUP_DAYS = pgOption(cfg, "PG_BACKUP_DAYS");
  const PG_SUBNET_NAME = pgOption(cfg, "PG_SUBNET_NAME");
  const PG_SUBNET_PREFIX = pgOption(cfg, "PG_SUBNET_PREFIX");
  const PG_DNS_ZONE = pgOption(cfg, "PG_DNS_ZONE");
  const PG_DNS_LINK_NAME = pgOption(cfg, "PG_DNS_LINK_NAME");
  const privateAccess = PG_ACCESS_MODE === "private";

  let AKS_MC_RG = "";
  let AKS_VNET_NAME = "";
  let AKS_VNET_ID = "";
  let PG_DNS_ZONE_ID = "";
  let PG_FQDN = `${PG_SERVER_NAME}.postgres.database.azure.com`;
  if (privateAccess) {
    AKS_MC_RG = cfg.AKS_MC_RG;
    if (!AKS_MC_RG) {
      const result = await exec.capture(
        "az",
        ["aks", "show", "--resource-group", cfg.RESOURCE_GROUP, "--name", cfg.CLUSTER_NAME, "--query", "nodeResourceGroup", "--output", "tsv"],
        { allowFailure: true },
      );
      AKS_MC_RG = result.stdout.trim();
    }
    if (!AKS_MC_RG) {
      throw new Error(`AKS_MC_RG is not set and could not be detected from cluster '${cfg.CLUSTER_NAME}'.`);
    }

    AKS_VNET_NAME = cfg.AKS_VNET_NAME;
    if (!AKS_VNET_NAME) {
      const result = await exec.capture(
        "az",
        ["network", "vnet", "list", "--resource-group", AKS_MC_RG, "--query", "[0].name", "--output", "tsv"],
        { allowFailure: true },
      );
      AKS_VNET_NAME = result.stdout.trim();
    }
    if (!AKS_VNET_NAME) {
      throw new Error(`AKS_VNET_NAME is not set and no VNet was found in node resource group '${AKS_MC_RG}'.`);
    }

    const { stdout: SUBSCRIPTION_ID } = await exec.capture("az", ["account", "show", "--query", "id", "--output", "tsv"]);
    AKS_VNET_ID = `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${AKS_MC_RG}/providers/Microsoft.Network/virtualNetworks/${AKS_VNET_NAME}`;
    PG_DNS_ZONE_ID = `/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${cfg.RESOURCE_GROUP}/providers/Microsoft.Network/privateDnsZones/${PG_DNS_ZONE}`;
  }

  log.info("");
  log.section("Agentweaver PostgreSQL Flexible Server provisioning");
  log.field("Resource Group", cfg.RESOURCE_GROUP);
  log.field("Location", PG_LOCATION);
  log.field("Server name", PG_SERVER_NAME);
  log.field("Access mode", PG_ACCESS_MODE);
  log.field("FQDN", PG_FQDN);
  log.field("Database", PG_DB_NAME);
  if (privateAccess) {
    log.field("AKS VNet", `${AKS_VNET_NAME} in ${AKS_MC_RG}`);
  } else {
    log.field("Public access", "0.0.0.0 (Azure services/resources only)");
  }
  log.field("K8s namespace", cfg.NAMESPACE);
  log.info("");

  log.info(`Ensuring Kubernetes namespace '${cfg.NAMESPACE}' exists for the Postgres secret...`);
  const { stdout: nsYaml } = await exec.capture("kubectl", ["create", "namespace", cfg.NAMESPACE, "--dry-run=client", "-o", "yaml"]);
  await applyRenderedYaml(exec, fsImpl, scratchDir, "namespace.yaml", nsYaml);

  let PG_SUBNET_ID = "";
  if (privateAccess) {
    // -- 1. Delegated subnet --
    log.info(`Step 1: Ensuring delegated subnet '${PG_SUBNET_NAME}'...`);
    PG_SUBNET_ID = (
      await exec.capture("az", ["network", "vnet", "subnet", "show", "--resource-group", AKS_MC_RG, "--vnet-name", AKS_VNET_NAME, "--name", PG_SUBNET_NAME, "--query", "id", "--output", "tsv"], {
        allowFailure: true,
      })
    ).stdout.trim();
    if (PG_SUBNET_ID) {
      log.skip(`Subnet '${PG_SUBNET_NAME}' already exists.`);
    } else {
      const created = await exec.capture("az", [
        "network",
        "vnet",
        "subnet",
        "create",
        "--resource-group",
        AKS_MC_RG,
        "--vnet-name",
        AKS_VNET_NAME,
        "--name",
        PG_SUBNET_NAME,
        "--address-prefixes",
        PG_SUBNET_PREFIX,
        "--delegations",
        "Microsoft.DBforPostgreSQL/flexibleServers",
        "--query",
        "id",
        "--output",
        "tsv",
      ]);
      PG_SUBNET_ID = created.stdout.trim();
      log.ok(`Subnet created: ${PG_SUBNET_ID}`);
    }

    // -- 2. Private DNS zone --
    log.info(`Step 2: Ensuring Private DNS zone '${PG_DNS_ZONE}'...`);
    const zoneShow = await exec.capture("az", ["network", "private-dns", "zone", "show", "--resource-group", cfg.RESOURCE_GROUP, "--name", PG_DNS_ZONE, "--query", "id", "--output", "tsv"], {
      allowFailure: true,
    });
    if (zoneShow.stdout.trim()) {
      log.skip(`DNS zone '${PG_DNS_ZONE}' already exists.`);
    } else {
      await exec.run("az", ["network", "private-dns", "zone", "create", "--resource-group", cfg.RESOURCE_GROUP, "--name", PG_DNS_ZONE, "--output", "none"]);
      log.ok("DNS zone created.");
    }

    // -- 3. VNet link --
    log.info(`Step 3: Ensuring VNet DNS link '${PG_DNS_LINK_NAME}'...`);
    const linkShow = await exec.capture(
      "az",
      ["network", "private-dns", "link", "vnet", "show", "--resource-group", cfg.RESOURCE_GROUP, "--zone-name", PG_DNS_ZONE, "--name", PG_DNS_LINK_NAME, "--query", "id", "--output", "tsv"],
      { allowFailure: true },
    );
    if (linkShow.stdout.trim()) {
      log.skip(`VNet link '${PG_DNS_LINK_NAME}' already exists.`);
    } else {
      await exec.run("az", [
        "network",
        "private-dns",
        "link",
        "vnet",
        "create",
        "--resource-group",
        cfg.RESOURCE_GROUP,
        "--zone-name",
        PG_DNS_ZONE,
        "--name",
        PG_DNS_LINK_NAME,
        "--virtual-network",
        AKS_VNET_ID,
        "--registration-enabled",
        "false",
        "--output",
        "none",
      ]);
      log.ok("VNet link created.");
    }
  } else {
    log.skip("Skipping delegated subnet/private DNS setup (PG_ACCESS_MODE=public)");
  }

  // -- 4. Flexible Server --
  log.info(`Step 4: Ensuring Flexible Server '${PG_SERVER_NAME}'...`);
  const existingServer = (
    await exec.capture("az", ["postgres", "flexible-server", "show", "--resource-group", cfg.RESOURCE_GROUP, "--name", PG_SERVER_NAME, "--query", "state", "--output", "tsv"], {
      allowFailure: true,
    })
  ).stdout.trim();

  let created = false;
  if (existingServer) {
    log.skip(`Server '${PG_SERVER_NAME}' already exists (state: ${existingServer}).`);
  } else {
    log.info(`  Pre-flight: validating SKU '${PG_SKU}' is provisionable in '${PG_LOCATION}'...`);
    await assertSkuProvisionable(exec, PG_LOCATION, PG_SKU);
    log.ok("  SKU availability pre-flight passed.");

    log.info("  Generating admin password (not echoed; will be stored in K8s secret)...");
    const PG_ADMIN_PASSWORD = await generateAdminPassword();
    secret.registerSecret(PG_ADMIN_PASSWORD, "postgres-admin-password");

    log.info(`  Creating Flexible Server '${PG_SERVER_NAME}' -- this takes ~5 minutes...`);
    // SameZone was removed until we correctly wire/test az postgres flexible-server create --allow-same-zone.
    const zonalFlags = PG_HA_MODE !== "Disabled" ? ["--zonal-resiliency", "Enabled"] : [];
    await exec.run(
      "az",
      [
        "postgres",
        "flexible-server",
        "create",
        "--resource-group",
        cfg.RESOURCE_GROUP,
        "--name",
        PG_SERVER_NAME,
        "--location",
        PG_LOCATION,
        "--admin-user",
        PG_ADMIN_USER,
        "--admin-password",
        PG_ADMIN_PASSWORD,
        "--version",
        PG_VERSION,
        "--sku-name",
        PG_SKU,
        "--tier",
        "GeneralPurpose",
        "--storage-size",
        PG_STORAGE_GB,
        ...zonalFlags,
        "--backup-retention",
        PG_BACKUP_DAYS,
        ...(privateAccess
          ? ["--subnet", PG_SUBNET_ID, "--private-dns-zone", PG_DNS_ZONE_ID]
          : ["--public-access", "0.0.0.0"]),
        "--yes",
        "--output",
        "none",
      ],
      { azSafeEnv: true },
    );
    log.ok("Server created.");
    PG_FQDN = await resolveServerFqdn(exec, cfg.RESOURCE_GROUP, PG_SERVER_NAME);

    log.info("  Storing credentials in K8s secret 'agentweaver-postgres'...");
    const PG_CONNECTION_STRING = `Host=${PG_FQDN};Port=5432;Database=${PG_DB_NAME};Username=${PG_ADMIN_USER};Password=${PG_ADMIN_PASSWORD};Ssl Mode=Require;Trust Server Certificate=false`;
    const { stdout: secretYaml } = await exec.capture("kubectl", [
      "create",
      "secret",
      "generic",
      "agentweaver-postgres",
      "--namespace",
      cfg.NAMESPACE,
      "--from-literal=host=" + PG_FQDN,
      "--from-literal=port=5432",
      "--from-literal=database=" + PG_DB_NAME,
      "--from-literal=username=" + PG_ADMIN_USER,
      "--from-literal=password=" + PG_ADMIN_PASSWORD,
      "--from-literal=connectionstring=" + PG_CONNECTION_STRING,
      "--save-config",
      "--dry-run=client",
      "-o",
      "yaml",
    ]);
    await applyRenderedYaml(exec, fsImpl, scratchDir, "secret-agentweaver-postgres.yaml", secretYaml);
    log.ok("K8s secret 'agentweaver-postgres' created/updated.");
    log.info("       Admin password is stored in: secret/agentweaver-postgres, key 'password'");
    log.info("       Connection string in:         secret/agentweaver-postgres, key 'connectionstring'");
    created = true;
  }

  PG_FQDN = await resolveServerFqdn(exec, cfg.RESOURCE_GROUP, PG_SERVER_NAME);

  // -- 5. Application database --
  log.info(`Step 5: Ensuring database '${PG_DB_NAME}'...`);
  const existingDb = (
    await exec.capture("az", ["postgres", "flexible-server", "db", "show", "--resource-group", cfg.RESOURCE_GROUP, "--server-name", PG_SERVER_NAME, "--name", PG_DB_NAME, "--query", "name", "--output", "tsv"], {
      allowFailure: true,
    })
  ).stdout.trim();
  if (existingDb) {
    log.skip(`Database '${PG_DB_NAME}' already exists.`);
  } else {
    await exec.run("az", ["postgres", "flexible-server", "db", "create", "--resource-group", cfg.RESOURCE_GROUP, "--server-name", PG_SERVER_NAME, "--name", PG_DB_NAME, "--output", "none"]);
    log.ok(`Database '${PG_DB_NAME}' created.`);
  }

  // -- 6. Private DNS A record --
  if (privateAccess) {
    log.info(`Step 6: Verifying private DNS A record for '${PG_SERVER_NAME}'...`);
    const existingA = (
      await exec.capture("az", ["network", "private-dns", "record-set", "a", "show", "--resource-group", cfg.RESOURCE_GROUP, "--zone-name", PG_DNS_ZONE, "--name", PG_SERVER_NAME, "--query", "name", "--output", "tsv"], {
        allowFailure: true,
      })
    ).stdout.trim();
    if (existingA) {
      log.skip(`A record '${PG_SERVER_NAME}' already exists in ${PG_DNS_ZONE}.`);
    } else {
      const privateIp = (
        await exec.capture(
          "az",
          ["network", "private-dns", "record-set", "a", "list", "--resource-group", cfg.RESOURCE_GROUP, "--zone-name", PG_DNS_ZONE, "--query", "[?name!='@'].aRecords[0].ipv4Address | [0]", "--output", "tsv"],
          { allowFailure: true },
        )
      ).stdout.trim();
      if (privateIp) {
        log.info(`  Adding A record '${PG_SERVER_NAME}' -> ${privateIp}...`);
        await exec.run("az", ["network", "private-dns", "record-set", "a", "add-record", "--resource-group", cfg.RESOURCE_GROUP, "--zone-name", PG_DNS_ZONE, "--record-set-name", PG_SERVER_NAME, "--ipv4-address", privateIp, "--output", "none"]);
        log.ok(`A record added: ${PG_SERVER_NAME}.${PG_DNS_ZONE} -> ${privateIp}`);
      } else {
        log.warn(`Could not determine private IP. Verify ${PG_SERVER_NAME}.${PG_DNS_ZONE} manually.`);
      }
    }
  } else {
    log.skip("Skipping private DNS record reconciliation (PG_ACCESS_MODE=public)");
  }

  // -- 7. Verify server state --
  log.info("Step 7: Verifying server state...");
  const serverState = (
    await exec.capture("az", ["postgres", "flexible-server", "show", "--resource-group", cfg.RESOURCE_GROUP, "--name", PG_SERVER_NAME, "--query", "state", "--output", "tsv"], { allowFailure: true })
  ).stdout.trim() || "unknown";
  log.field("Server state", serverState);
  if (serverState !== "Ready") {
    log.warn("Server is not in Ready state. It may still be provisioning.");
  }

  log.info("");
  log.section("POSTGRES PROVISIONING COMPLETE");
  log.field("Server", PG_SERVER_NAME);
  log.field("FQDN", PG_FQDN);
  log.field("Database", PG_DB_NAME);
  log.field("K8s secret", `secret/agentweaver-postgres (namespace: ${cfg.NAMESPACE})`);

  return { PG_SERVER_NAME, PG_FQDN, PG_DB_NAME, serverState, created };
}

/** Writes rendered YAML to a scratch file and applies it via `kubectl apply -f <file>`, then removes the file. */
async function applyRenderedYaml(exec, fsImpl, scratchDir, filename, yamlContent) {
  if (exec.isDryRun && exec.isDryRun()) return;
  fsImpl.mkdirSync(scratchDir, { recursive: true });
  const filePath = path.join(scratchDir, filename);
  fsImpl.writeFileSync(filePath, yamlContent);
  try {
    await exec.run("kubectl", ["apply", "-f", filePath]);
  } finally {
    fsImpl.rmSync(filePath, { force: true });
  }
}
