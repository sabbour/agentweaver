import * as execDefault from "./exec.mjs";

export const ACCESS_AS_USER_SCOPE_VALUE = "access_as_user";
export const ACCESS_AS_USER_SCOPE_ID = "75e2c2a2-90dd-46c7-8e83-29bb43c7f8f3";
export const ENTRA_APP_REGISTRATION_REPAIR_COMMAND =
  "npm run azure:setup-entra-app -- --app-id <ENTRA_CLIENT_ID> --redirect-uri <callback-uri>";

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SCOPE_DESCRIPTION = Object.freeze({
  adminConsentDescription: "Allow Agentweaver to sign in users through its own API.",
  adminConsentDisplayName: "Sign in to Agentweaver",
  type: "User",
  userConsentDescription: "Allow this application to sign you in to Agentweaver.",
  userConsentDisplayName: "Sign in to Agentweaver",
  value: ACCESS_AS_USER_SCOPE_VALUE,
});

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function sameId(left, right) {
  return String(left ?? "").toLowerCase() === String(right ?? "").toLowerCase();
}

function readArrayProperty(owner, property, label) {
  const value = owner?.[property];
  if (value !== undefined && value !== null && !Array.isArray(value)) {
    throw new Error(`The Entra app registration has an invalid ${label} value; resolve it explicitly before rerunning setup-entra-app.`);
  }
  return Array.isArray(value) ? value : [];
}

function withScope(existing, scope) {
  const values = Array.isArray(existing) ? existing : [];
  const matching = values.filter((candidate) => candidate?.value === ACCESS_AS_USER_SCOPE_VALUE);
  if (matching.length > 1) {
    throw new Error(
      "The Entra app has multiple access_as_user scope definitions. Resolve the duplicate scope definitions explicitly, then rerun setup-entra-app.",
    );
  }

  const candidate = matching[0];
  if (candidate) {
    if (!UUID_RE.test(String(candidate.id ?? "")) ||
        !["Admin", "User"].includes(candidate.type)) {
      throw new Error(
        "The existing access_as_user scope has an invalid ID or is not a delegated scope. Resolve that conflicting scope explicitly, then rerun setup-entra-app.",
      );
    }
    scope.id = candidate.id;
  } else if (values.some((item) => sameId(item?.id, scope.id))) {
    throw new Error(
      "The managed access_as_user scope ID is already used by another Entra API scope. Resolve the scope-ID conflict explicitly, then rerun setup-entra-app.",
    );
  }

  const next = values.map((item) => item === candidate
    ? (item.isEnabled === true ? item : { ...item, isEnabled: true })
    : item);
  if (!candidate) next.push({ ...scope, isEnabled: true });
  return next;
}

function withSelfPreauthorization(existing, appId, scopeId) {
  const values = Array.isArray(existing) ? existing : [];
  const selfEntries = values.filter((entry) => sameId(entry?.appId, appId));
  if (selfEntries.length > 1) {
    throw new Error(
      "The Entra app has multiple self-client preauthorization entries. Resolve the duplicate entries explicitly, then rerun setup-entra-app.",
    );
  }

  if (selfEntries.length === 0) {
    return [...values, { appId, delegatedPermissionIds: [scopeId] }];
  }

  const entry = selfEntries[0];
  if (entry.delegatedPermissionIds !== undefined && entry.delegatedPermissionIds !== null &&
      !Array.isArray(entry.delegatedPermissionIds)) {
    throw new Error(
      "The Entra app has an invalid self-client preauthorization permission list. Resolve it explicitly, then rerun setup-entra-app.",
    );
  }
  const permissionIds = Array.isArray(entry.delegatedPermissionIds)
    ? entry.delegatedPermissionIds
    : [];
  if (permissionIds.some((id) => sameId(id, scopeId))) return values;
  return values.map((item) => item === entry
    ? { ...entry, delegatedPermissionIds: [...permissionIds, scopeId] }
    : item);
}

function withSelfRequiredResourceAccess(existing, appId, scopeId) {
  const values = Array.isArray(existing) ? existing : [];
  const selfEntries = values.filter((entry) => sameId(entry?.resourceAppId, appId));
  const hasScope = selfEntries.some((entry) =>
    Array.isArray(entry.resourceAccess) && entry.resourceAccess.some((access) =>
      access?.type === "Scope" && sameId(access.id, scopeId)));
  if (hasScope) return values;

  if (selfEntries.length === 0) {
    return [
      ...values,
      { resourceAppId: appId, resourceAccess: [{ id: scopeId, type: "Scope" }] },
    ];
  }

  const entry = selfEntries[0];
  if (entry.resourceAccess !== undefined && entry.resourceAccess !== null &&
      !Array.isArray(entry.resourceAccess)) {
    throw new Error(
      "The Entra app has an invalid self-resource permission list. Resolve it explicitly, then rerun setup-entra-app.",
    );
  }
  const resourceAccess = Array.isArray(entry.resourceAccess) ? entry.resourceAccess : [];
  return values.map((item) => item === entry
    ? { ...entry, resourceAccess: [...resourceAccess, { id: scopeId, type: "Scope" }] }
    : item);
}

/**
 * Return only the Graph application properties needed to add Agentweaver's
 * delegated self-resource permission. Existing scopes, resource permissions,
 * preauthorizations, roles, redirects and API settings are preserved.
 */
export function buildEntraAuthRegistrationPatch(app) {
  const appId = String(app?.appId ?? "").trim();
  if (!UUID_RE.test(appId)) {
    throw new Error("The Entra application does not contain a valid application (client) ID.");
  }

  if (app.api !== undefined && app.api !== null && !isRecord(app.api)) {
    throw new Error("The Entra app registration has an invalid API configuration; resolve it explicitly before rerunning setup-entra-app.");
  }
  const api = isRecord(app.api) ? app.api : {};
  const scopes = readArrayProperty(api, "oauth2PermissionScopes", "oauth2PermissionScopes");
  const preauthorized = readArrayProperty(api, "preAuthorizedApplications", "preAuthorizedApplications");
  const required = readArrayProperty(app, "requiredResourceAccess", "requiredResourceAccess");
  const identifierUris = readArrayProperty(app, "identifierUris", "identifierUris");
  const matchingScope = scopes.find((scope) => scope?.value === ACCESS_AS_USER_SCOPE_VALUE);
  const scopeId = matchingScope?.id ?? ACCESS_AS_USER_SCOPE_ID;
  const managedScope = {
    ...SCOPE_DESCRIPTION,
    id: scopeId,
    isEnabled: true,
  };
  const oauth2PermissionScopes = withScope(scopes, managedScope);
  const preAuthorizedApplications = withSelfPreauthorization(
    preauthorized,
    appId,
    scopeId,
  );
  const requiredResourceAccess = withSelfRequiredResourceAccess(
    required,
    appId,
    scopeId,
  );
  const managedIdentifierUris = identifierUris.length > 0
    ? identifierUris
    : [`api://${appId}`];

  const patch = {
    api: {
      ...api,
      oauth2PermissionScopes,
      preAuthorizedApplications,
      requestedAccessTokenVersion: 2,
    },
    identifierUris: managedIdentifierUris,
    requiredResourceAccess,
  };
  const current = {
    api: app.api,
    identifierUris: app.identifierUris,
    requiredResourceAccess: app.requiredResourceAccess,
  };
  return { patch, changed: JSON.stringify(current) !== JSON.stringify(patch), scopeId };
}

export function buildEntraAuthRegistrationPhases(app) {
  const { patch, changed, scopeId } = buildEntraAuthRegistrationPatch(app);
  const api = isRecord(app.api) ? app.api : {};
  const phaseOneApi = { ...patch.api };
  if (Array.isArray(api.preAuthorizedApplications)) {
    phaseOneApi.preAuthorizedApplications = api.preAuthorizedApplications;
  } else {
    delete phaseOneApi.preAuthorizedApplications;
  }

  const resourcePatch = {
    ...patch,
    api: phaseOneApi,
  };
  const currentResources = {
    api: app.api,
    identifierUris: app.identifierUris,
    requiredResourceAccess: app.requiredResourceAccess,
  };
  return {
    changed,
    patch,
    resourcePatch,
    resourceChanged: JSON.stringify(currentResources) !== JSON.stringify(resourcePatch),
    preauthorizationChanged: JSON.stringify(api.preAuthorizedApplications)
      !== JSON.stringify(patch.api.preAuthorizedApplications),
    preauthorizationPatch: {
      api: { preAuthorizedApplications: patch.api.preAuthorizedApplications },
    },
    scopeId,
  };
}

export function inspectEntraAuthResourceRegistration(app, appId) {
  const errors = [];
  const scopes = Array.isArray(app?.api?.oauth2PermissionScopes)
    ? app.api.oauth2PermissionScopes
    : [];
  const scope = scopes.find(
    (item) => item?.value === ACCESS_AS_USER_SCOPE_VALUE,
  );
  const scopeId = String(scope?.id ?? "");

  if (!sameId(app?.appId, appId)) errors.push("the exact configured application");
  if (app?.signInAudience !== "AzureADMyOrg") errors.push("single-tenant sign-in");
  if (app?.isFallbackPublicClient !== true) errors.push("the public-client fallback setting");
  if (!Array.isArray(app?.identifierUris) ||
      !app.identifierUris.some((identifierUri) => typeof identifierUri === "string" && identifierUri.trim())) {
    errors.push("an API identifier URI");
  }
  if (app?.api?.requestedAccessTokenVersion !== 2) errors.push("v2 access tokens");
  if (!scope || scope.isEnabled !== true ||
      !["Admin", "User"].includes(scope.type) || !UUID_RE.test(scopeId)) {
    errors.push("an enabled delegated access_as_user scope");
  } else {
    const hasSelfResource = Array.isArray(app.requiredResourceAccess) &&
      app.requiredResourceAccess.some((resource) =>
        sameId(resource?.resourceAppId, appId) &&
        Array.isArray(resource.resourceAccess) &&
        resource.resourceAccess.some((access) =>
          access?.type === "Scope" && sameId(access.id, scopeId)));
    if (!hasSelfResource) errors.push("the self-resource delegated permission");
  }

  return errors;
}

export function inspectEntraAuthRegistration(app, appId) {
  const errors = inspectEntraAuthResourceRegistration(app, appId);
  const scopes = Array.isArray(app?.api?.oauth2PermissionScopes)
    ? app.api.oauth2PermissionScopes
    : [];
  const scope = scopes.find((item) => item?.value === ACCESS_AS_USER_SCOPE_VALUE);
  const scopeId = String(scope?.id ?? "");

  if (scope && scope.isEnabled === true &&
      ["Admin", "User"].includes(scope.type) && UUID_RE.test(scopeId)) {
    const hasSelfPreauthorization = Array.isArray(app.api?.preAuthorizedApplications) &&
      app.api.preAuthorizedApplications.some((client) =>
        sameId(client?.appId, appId) &&
        Array.isArray(client.delegatedPermissionIds) &&
        client.delegatedPermissionIds.some((id) => sameId(id, scopeId)));
    if (!hasSelfPreauthorization) errors.push("self-client preauthorization for access_as_user");
  }

  return errors;
}

async function captureTenantId(exec) {
  const result = await exec.capture(
    "az",
    ["account", "show", "--query", "tenantId", "-o", "tsv"],
    { allowFailure: true },
  );
  if (result.code !== 0) return "";
  return String(result.stdout ?? "").trim();
}

async function captureApplication(exec, appId) {
  const result = await exec.capture(
    "az",
    ["ad", "app", "show", "--id", appId, "-o", "json"],
    { allowFailure: true },
  );
  if (result.code !== 0 || !String(result.stdout ?? "").trim()) return null;
  try {
    const app = JSON.parse(result.stdout);
    return isRecord(app) ? app : null;
  } catch {
    return null;
  }
}

/** Read-only installation gate shared by provision and deploy commands. */
export async function validateEntraAppRegistration({
  appId,
  tenantId,
  exec = execDefault,
} = {}) {
  const configuredAppId = String(appId ?? "").trim();
  const configuredTenantId = String(tenantId ?? "").trim();
  if (!UUID_RE.test(configuredAppId) || !UUID_RE.test(configuredTenantId)) {
    throw new Error(
      "Entra preflight requires valid ENTRA_CLIENT_ID and ENTRA_TENANT_ID UUIDs before Azure resources can be changed.",
    );
  }

  let activeTenant;
  let app;
  try {
    activeTenant = await captureTenantId(exec);
    if (activeTenant) app = await captureApplication(exec, configuredAppId);
  } catch {
    activeTenant = "";
  }

  if (!activeTenant || !app) {
    throw new Error(
      "Entra registration preflight could not read the active tenant and exact app registration. No Azure resources were changed. Run `az login --tenant <ENTRA_TENANT_ID>` using an operator account already allowed to read the exact app registration, then rerun. To reconcile the registration, run `"
        + ENTRA_APP_REGISTRATION_REPAIR_COMMAND
        + "`.",
    );
  }
  if (!sameId(activeTenant, configuredTenantId)) {
    throw new Error(
      "The active Azure CLI tenant does not match ENTRA_TENANT_ID. Run `az login --tenant <ENTRA_TENANT_ID>` for the configured tenant and rerun; no Azure resources were changed.",
    );
  }

  const errors = inspectEntraAuthRegistration(app, configuredAppId);
  if (errors.length > 0) {
    throw new Error(
      `Entra registration preflight failed: ${errors.join(", ")} are missing or invalid. No Azure resources were changed. Reconcile the exact registration with \``
        + ENTRA_APP_REGISTRATION_REPAIR_COMMAND
        + "` and rerun installation.",
    );
  }

  return { app, tenantId: activeTenant, scopeId: app.api.oauth2PermissionScopes.find(
    (item) => item?.value === ACCESS_AS_USER_SCOPE_VALUE,
  ).id };
}
