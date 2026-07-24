// 10-create-cluster.mjs -- Faithful Node port of scripts/aks/10-create-cluster.sh
// (cross-checked against 10-create-cluster.ps1). Read both before changing this
// file; they must stay in lockstep with this port's behavior.
//
// Provisions: resource group, ACR, AKS cluster (system pool + app pool + kata
// pool), and the agent-sandbox CRDs/controller. Every step is idempotent,
// exactly mirroring the bash script's `[SKIP]`-if-exists guards.
//
// cfg is the resolved variables.mjs output: RESOURCE_GROUP, CLUSTER_NAME,
// ACR_NAME, LOCATION, KATA_POOL_NAME, APP_POOL_NAME, ACR_LOGIN_SERVER.
// Optional overrides: SANDBOX_CONTROLLER_VERSION (default 'v0.5.0').

import * as execDefault from "../lib/exec.mjs";
import * as logDefault from "../lib/log.mjs";

/** True if the resource group already exists. Mirrors `az group exists`. */
export async function resourceGroupExists(cfg, { exec = execDefault } = {}) {
  const { stdout } = await exec.capture("az", ["group", "exists", "--name", cfg.RESOURCE_GROUP], {
    allowFailure: true,
  });
  return stdout.trim() === "true";
}

/** True if the ACR already exists. Mirrors `az acr show &>/dev/null`. */
export async function acrExists(cfg, { exec = execDefault } = {}) {
  const { code } = await exec.capture(
    "az",
    ["acr", "show", "--name", cfg.ACR_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  return code === 0;
}

/** True if the AKS cluster already exists. Mirrors `az aks show &>/dev/null`. */
export async function aksClusterExists(cfg, { exec = execDefault } = {}) {
  const { code } = await exec.capture(
    "az",
    ["aks", "show", "--name", cfg.CLUSTER_NAME, "--resource-group", cfg.RESOURCE_GROUP],
    { allowFailure: true },
  );
  return code === 0;
}

/** True if the named node pool already exists on the cluster. */
export async function nodePoolExists(cfg, poolName, { exec = execDefault } = {}) {
  const { code } = await exec.capture(
    "az",
    [
      "aks",
      "nodepool",
      "show",
      "--cluster-name",
      cfg.CLUSTER_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--name",
      poolName,
    ],
    { allowFailure: true },
  );
  return code === 0;
}

/** True if the agent-sandbox CRDs are already installed. Mirrors `kubectl get crd ... &>/dev/null`. */
export async function sandboxCrdInstalled({ exec = execDefault } = {}) {
  const { code } = await exec.capture(
    "kubectl",
    ["get", "crd", "sandboxclaims.extensions.agents.x-k8s.io"],
    { allowFailure: true },
  );
  return code === 0;
}

/**
 * Provisions ACR + AKS cluster for Agentweaver: faithful port of
 * 10-create-cluster.sh.
 *
 * @param {Record<string, unknown>} cfg Resolved variables from variables.mjs.
 * @param {object} [opts] Injectable collaborators, primarily for testing.
 */
export async function run(cfg, opts = {}) {
  const { exec = execDefault, log = logDefault } = opts;

  const sandboxControllerVersion = cfg.SANDBOX_CONTROLLER_VERSION || "v0.5.0";
  const manifestUrl =
    cfg.SANDBOX_CONTROLLER_MANIFEST_URL ||
    `https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${sandboxControllerVersion}/manifest.yaml`;
  const extensionsUrl = `https://github.com/kubernetes-sigs/agent-sandbox/releases/download/${sandboxControllerVersion}/extensions.yaml`;

  log.info("");
  log.section("Agentweaver AKS cluster provisioning");
  log.info("");

  log.info("Installing/upgrading aks-preview extension...");
  await exec.run("az", ["extension", "add", "--upgrade", "--name", "aks-preview"]);

  // -- Resource group --
  if (await resourceGroupExists(cfg, { exec })) {
    log.skip(`Resource group '${cfg.RESOURCE_GROUP}' already exists.`);
  } else {
    log.info(`Creating resource group '${cfg.RESOURCE_GROUP}' in ${cfg.LOCATION}...`);
    await exec.run("az", ["group", "create", "--name", cfg.RESOURCE_GROUP, "--location", cfg.LOCATION, "--output", "table"]);
  }

  // -- ACR --
  if (await acrExists(cfg, { exec })) {
    log.skip(`ACR '${cfg.ACR_NAME}' already exists.`);
  } else {
    log.info("");
    log.info(`Creating ACR '${cfg.ACR_NAME}'...`);
    await exec.run("az", [
      "acr",
      "create",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--name",
      cfg.ACR_NAME,
      "--sku",
      "Standard",
      "--admin-enabled",
      "false",
      "--output",
      "table",
    ]);
  }

  const { stdout: acrId } = await exec.capture("az", [
    "acr",
    "show",
    "--name",
    cfg.ACR_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--query",
    "id",
    "--output",
    "tsv",
  ]);
  log.info(`  ACR resource ID: ${acrId}`);

  // -- AKS cluster --
  if (await aksClusterExists(cfg, { exec })) {
    log.info("");
    log.skip(`AKS cluster '${cfg.CLUSTER_NAME}' already exists.`);
  } else {
    log.info("");
    log.info(`Creating AKS cluster '${cfg.CLUSTER_NAME}' (~10-15 minutes)...`);
    await exec.run("az", [
      "aks",
      "create",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--name",
      cfg.CLUSTER_NAME,
      "--location",
      cfg.LOCATION,
      "--network-plugin",
      "azure",
      "--network-plugin-mode",
      "overlay",
      "--network-dataplane",
      "cilium",
      "--enable-acns",
      "--os-sku",
      "AzureLinux",
      "--node-vm-size",
      "Standard_D4s_v3",
      "--node-count",
      "2",
      "--enable-cluster-autoscaler",
      "--min-count",
      "1",
      "--max-count",
      "3",
      "--nodepool-taints",
      "CriticalAddonsOnly=true:NoSchedule",
      "--enable-app-routing-istio",
      "--enable-gateway-api",
      "--enable-default-domain",
      "--enable-addons",
      "azure-keyvault-secrets-provider",
      "--enable-oidc-issuer",
      "--enable-workload-identity",
      "--attach-acr",
      acrId,
      "--ssh-access",
      "disabled",
      "--output",
      "table",
    ]);
  }

  log.info("");
  log.info("Fetching kubeconfig...");
  await exec.run("az", [
    "aks",
    "get-credentials",
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--name",
    cfg.CLUSTER_NAME,
    "--overwrite-existing",
  ]);

  // -- App node pool --
  if (await nodePoolExists(cfg, cfg.APP_POOL_NAME, { exec })) {
    log.skip(`Node pool '${cfg.APP_POOL_NAME}' already exists.`);
  } else {
    log.info("");
    log.info(`Adding app user pool '${cfg.APP_POOL_NAME}' (cluster-autoscaler 1-5 nodes)...`);
    await exec.run("az", [
      "aks",
      "nodepool",
      "add",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--cluster-name",
      cfg.CLUSTER_NAME,
      "--name",
      cfg.APP_POOL_NAME,
      "--mode",
      "User",
      "--os-sku",
      "AzureLinux",
      "--node-vm-size",
      "Standard_D4s_v3",
      "--enable-cluster-autoscaler",
      "--min-count",
      "1",
      "--max-count",
      "5",
      "--ssh-access",
      "disabled",
      "--output",
      "table",
    ]);
  }

  // -- Kata node pool --
  if (await nodePoolExists(cfg, cfg.KATA_POOL_NAME, { exec })) {
    log.skip(`Node pool '${cfg.KATA_POOL_NAME}' already exists.`);
  } else {
    log.info("");
    log.info(`Adding dedicated Kata user pool '${cfg.KATA_POOL_NAME}' (cluster-autoscaler 1-5 nodes)...`);
    await exec.run("az", [
      "aks",
      "nodepool",
      "add",
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--cluster-name",
      cfg.CLUSTER_NAME,
      "--name",
      cfg.KATA_POOL_NAME,
      "--mode",
      "User",
      "--os-sku",
      "AzureLinux",
      "--workload-runtime",
      "KataVmIsolation",
      "--node-vm-size",
      "Standard_D4s_v3",
      "--enable-cluster-autoscaler",
      "--min-count",
      "1",
      "--max-count",
      "5",
      "--node-taints",
      "sandbox=kata:NoSchedule",
      "--labels",
      "agentweaver.io/kata=true",
      "--ssh-access",
      "disabled",
      "--output",
      "table",
    ]);
  }

  // -- Sandbox controller CRDs --
  if (await sandboxCrdInstalled({ exec })) {
    log.skip("Agent-sandbox CRDs already installed.");
  } else {
    log.info("");
    log.info(`Installing agent-sandbox CRDs/controller (${sandboxControllerVersion})...`);
    await exec.run("kubectl", ["apply", "-f", manifestUrl]);
    await exec.run("kubectl", ["apply", "-f", extensionsUrl]);
    await exec.run("kubectl", [
      "wait",
      "--for=condition=Established",
      "crd/sandboxclaims.extensions.agents.x-k8s.io",
      "--timeout=180s",
    ]);
    await exec.run("kubectl", [
      "wait",
      "--for=condition=Established",
      "crd/sandboxtemplates.extensions.agents.x-k8s.io",
      "--timeout=180s",
    ]);
    await exec.run("kubectl", [
      "wait",
      "--for=condition=Established",
      "crd/sandboxwarmpools.extensions.agents.x-k8s.io",
      "--timeout=180s",
    ]);
  }

  log.info("");
  log.info("--- Node status ---");
  await exec.run("kubectl", ["get", "nodes", "-o", "wide"]);

  log.info("");
  log.info("--- RuntimeClass check ---");
  await exec.run("kubectl", ["get", "runtimeclass"]);
  log.info("");
  log.info("Verify 'kata-vm-isolation' (or 'kata-mshv-vm-isolation') is listed above.");

  log.info("");
  log.info("===================================================");
  log.info(" CLUSTER READY");
  log.info("===================================================");
  log.info("");
  log.field("Resource Group", cfg.RESOURCE_GROUP);
  log.field("Cluster", cfg.CLUSTER_NAME);
  log.field("ACR", cfg.ACR_LOGIN_SERVER);
  log.info("");
  log.info("  Next step:");
  log.info("    node scripts/azure/cli.mjs provision-infra   (or: scripts/azure/steps/15-setup-identity.mjs)");

  return { acrId };
}
