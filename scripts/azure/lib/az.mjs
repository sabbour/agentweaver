// az.mjs -- Thin, side-effect-minimal helpers over exec.mjs's capture() for
// Azure CLI resolution queries. These are pure resolution helpers; the only
// side effect performed anywhere in this module is `az account set`
// (explicitly requested by setActiveSubscription), matching the scope this
// module documents.

import { capture } from "./exec.mjs";

/** Lists all subscriptions visible to the logged-in `az` account. */
export async function listSubscriptions(opts = {}) {
  const { json } = await capture("az", ["account", "list", "-o", "json"], { json: true, ...opts });
  return json ?? [];
}

/** Shows the currently active subscription (`az account show`). */
export async function showAccount(opts = {}) {
  const { json } = await capture("az", ["account", "show", "-o", "json"], { json: true, ...opts });
  return json ?? null;
}

/** Sets the active subscription. The one deliberate side effect in this module. */
export async function setActiveSubscription(subscriptionIdOrName, opts = {}) {
  await capture("az", ["account", "set", "--subscription", subscriptionIdOrName], opts);
}

/** Lists resource groups in the active subscription. */
export async function listResourceGroups(opts = {}) {
  const { json } = await capture("az", ["group", "list", "-o", "json"], { json: true, ...opts });
  return json ?? [];
}

/** Lists Azure locations/regions available to the active subscription. */
export async function listLocations(opts = {}) {
  const { json } = await capture(
    "az",
    ["account", "list-locations", "-o", "json"],
    { json: true, ...opts },
  );
  return json ?? [];
}

/**
 * Resolves the current Azure AD tenant id from the logged-in `az` context.
 * Returns '' (never throws) on any failure, matching 00-variables.sh's
 * `... || true` tolerance -- callers may not be logged in yet, or `az` may
 * be unavailable, and that must not abort variable resolution.
 */
export async function getTenantId(opts = {}) {
  try {
    const { stdout } = await capture("az", ["account", "show", "--query", "tenantId", "--output", "tsv"], opts);
    return stdout.trim();
  } catch {
    return "";
  }
}

/**
 * Resolves a user-assigned managed identity's clientId by name within a
 * resource group. Returns '' on any failure (identity not yet provisioned,
 * resource group missing, not logged in, etc).
 */
export async function getIdentityClientId(resourceGroup, identityName, opts = {}) {
  try {
    const { stdout } = await capture(
      "az",
      [
        "identity",
        "list",
        "--resource-group",
        resourceGroup,
        "--query",
        `[?name=='${identityName}'].clientId | [0]`,
        "--output",
        "tsv",
      ],
      opts,
    );
    return stdout.trim();
  } catch {
    return "";
  }
}

/**
 * Checks whether a Key Vault named `name` actually exists in the active
 * subscription (`az keyvault show --name`, subscription-wide -- no resource
 * group required, matching how `KEYVAULT_NAME` is looked up when rendering
 * manifests). Unlike this module's other helpers, this deliberately returns
 * a boolean rather than swallowing failure to '': deploy callers must fail
 * loudly on `false`, since a nonexistent (or typo'd-but-real, wrong) vault
 * here silently corrupts GitHub OAuth credential lookups downstream instead
 * of erroring cleanly. See scripts/harness-shared/learnings.md for the
 * incident this guards against.
 * @param {string} name
 * @returns {Promise<boolean>}
 */
export async function keyvaultExists(name, opts = {}) {
  const { code } = await capture("az", ["keyvault", "show", "--name", name, "--query", "name", "--output", "tsv"], {
    allowFailure: true,
    ...opts,
  });
  return code === 0;
}

/**
 * Resolves a Log Analytics workspace's customerId (workspace GUID) by name
 * within a resource group. Returns '' on any failure.
 */
export async function getLogAnalyticsWorkspaceCustomerId(resourceGroup, workspaceName, opts = {}) {
  try {
    const { stdout } = await capture(
      "az",
      [
        "monitor",
        "log-analytics",
        "workspace",
        "show",
        "--resource-group",
        resourceGroup,
        "--workspace-name",
        workspaceName,
        "--query",
        "customerId",
        "--output",
        "tsv",
      ],
      opts,
    );
    return stdout.trim();
  } catch {
    return "";
  }
}
