#!/usr/bin/env node
// setup-entra-app.mjs -- Bootstrap a single-tenant Microsoft Entra app
// registration for Agentweaver.
//
// Scope:
//   - Create or reuse one Entra app registration.
//   - Enforce single-tenant sign-in (`AzureADMyOrg`).
//   - Ensure the requested web redirect URIs exist on the app.
//   - Define the current Agentweaver platform App Roles via Microsoft Graph.
//   - Ensure the app's service principal exists.
//   - Print the resulting identifiers in the config shapes Agentweaver will need.
//   - Configure the Entra sign-in application used by every deployment.
//
// This lives in the Node-based `scripts/azure/` toolchain on purpose: the repo
// removed the old bash/PowerShell Azure tooling and keeps cross-platform Azure
// automation here.

import { pathToFileURL } from "node:url";
import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";

export const DEFAULT_APP_NAME = "agentweaver-authn";
// NOTE: these are registered under the app's *publicClient* platform (not `web`).
// Agentweaver's API redeems the authorization code server-side, and Microsoft Entra ID
// only allows secretless (PKCE-only) redemption for `publicClient`-registered redirect
// URIs regardless of caller. `web`-registered URIs always require a client_secret or
// client_assertion at the token endpoint, and `spa`-registered URIs are restricted to
// browser/CORS-origin redemption -- neither works for this deployment's architecture,
// and this tenant's policy blocks client-secret creation outright.
export const DEFAULT_REDIRECT_URIS = Object.freeze(["http://localhost:5000/auth/entra/callback"]);
export const SIGN_IN_AUDIENCE = "AzureADMyOrg";
export const MANAGED_ROLE_DESCRIPTION_PREFIX = "Agentweaver:";

// Coarse platform-role set for Auth:Mode=Entra. The IDs are intentionally
// stable so re-runs keep the same roles instead of creating duplicates.
export const DEFAULT_APP_ROLES = Object.freeze([
  Object.freeze({
    allowedMemberTypes: ["User"],
    description: "Agentweaver: platform-wide administrator access.",
    displayName: "PlatformAdmin",
    id: "38a31dfd-b8b8-42b3-a820-1521f6274955",
    isEnabled: true,
    value: "PlatformAdmin",
  }),
  Object.freeze({
    allowedMemberTypes: ["User"],
    description: "Agentweaver: platform-wide project creation access.",
    displayName: "ProjectCreator",
    id: "11fcb5a0-1f50-4f7e-9d51-443183f0bfe3",
    isEnabled: true,
    value: "ProjectCreator",
  }),
  Object.freeze({
    allowedMemberTypes: ["User"],
    description: "Agentweaver: platform-wide contributor access.",
    displayName: "Contributor",
    id: "85fd3442-8291-4d52-a76f-ee962e711d7f",
    isEnabled: true,
    value: "Contributor",
  }),
  Object.freeze({
    allowedMemberTypes: ["User"],
    description: "Agentweaver: platform-wide read-only access.",
    displayName: "Viewer",
    id: "cf59bdf5-5721-4465-8b47-3266383ff50e",
    isEnabled: true,
    value: "Viewer",
  }),
]);

export const HELP_TEXT = `setup-entra-app -- Bootstrap a single-tenant Entra app registration for Agentweaver

Usage:
  node scripts/azure/cli.mjs setup-entra-app [flags]
  npm run azure:setup-entra-app -- [flags]

Flags:
  --app-name <name>                     Display name to create/reuse (default: ${DEFAULT_APP_NAME}).
  --app-id <client-id>                  Target an existing application by client ID instead of display name lookup.
  --redirect-uri <uri>                  Public-client redirect URI to ensure on the app. Repeatable; defaults to ${DEFAULT_REDIRECT_URIS[0]}.
  --service-management-reference <id>   Optional app-create passthrough required by some corporate tenants.
  -h, --help                            Show this help.

Notes:
  - This command is optional. Run it when a deployment chooses Auth:Mode=Entra.
    Agentweaver deployments use this Entra application for browser sign-in.
  - The app is always enforced as single-tenant (${SIGN_IN_AUDIENCE}).
  - Redirect URIs are registered under the *publicClient* platform (not \`web\` or \`spa\`), and
    \`isFallbackPublicClient\` is enforced true, so the API can redeem authorization codes with
    PKCE only -- no client secret, and no browser/CORS restriction. If a URI is found under
    \`web\` it is moved to \`publicClient\`. Redirect URIs are merged idempotently; re-running
    adds missing URIs without removing unrelated existing ones.
  - The App Roles are: PlatformAdmin, ProjectCreator, Contributor, Viewer. Creating the app
    does NOT grant anyone a role -- see the "Grant platform roles" output after this command
    runs for how to assign a user or group.
`;

export function parseArgs(argv = []) {
  const flags = { REDIRECT_URIS: [] };
  let help = false;

  const takeValue = (i, name) => {
    const raw = argv[i];
    const eq = raw.indexOf("=");
    if (eq !== -1) return { value: raw.slice(eq + 1), consumed: 0 };
    const next = argv[i + 1];
    if (next === undefined) throw new Error(`${name} requires a value`);
    return { value: next, consumed: 1 };
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "-h" || arg === "--help") {
      help = true;
    } else if (arg === "--app-name" || arg.startsWith("--app-name=")) {
      const { value, consumed } = takeValue(i, "--app-name");
      flags.APP_NAME = value;
      i += consumed;
    } else if (arg === "--app-id" || arg.startsWith("--app-id=")) {
      const { value, consumed } = takeValue(i, "--app-id");
      flags.APP_ID = value;
      i += consumed;
    } else if (arg === "--redirect-uri" || arg.startsWith("--redirect-uri=")) {
      const { value, consumed } = takeValue(i, "--redirect-uri");
      flags.REDIRECT_URIS.push(...String(value).split(","));
      i += consumed;
    } else if (arg === "--service-management-reference" || arg.startsWith("--service-management-reference=")) {
      const { value, consumed } = takeValue(i, "--service-management-reference");
      flags.SERVICE_MANAGEMENT_REFERENCE = value;
      i += consumed;
    } else {
      throw new Error(`Unknown argument: ${arg}. Run 'setup-entra-app --help' for usage.`);
    }
  }

  return { flags, help };
}

export function normalizeRedirectUris(values) {
  const seen = new Set();
  const out = [];
  for (const raw of values ?? []) {
    const value = String(raw ?? "").trim();
    if (!value) continue;
    validateRedirectUri(value);
    const key = value.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(value);
  }
  return out;
}

export function validateRedirectUri(value) {
  let parsed;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error(`Redirect URI '${value}' is not a valid absolute URL.`);
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new Error(`Redirect URI '${value}' must use http:// or https://.`);
  }
  return true;
}

function sanitizeAppRole(role) {
  return {
    allowedMemberTypes: Array.isArray(role.allowedMemberTypes) ? [...role.allowedMemberTypes] : ["User"],
    description: String(role.description ?? ""),
    displayName: String(role.displayName ?? ""),
    id: String(role.id ?? ""),
    isEnabled: role.isEnabled !== false,
    value: role.value === null || role.value === undefined ? null : String(role.value),
  };
}

function isManagedRole(role) {
  const description = String(role?.description ?? "");
  return description.startsWith(MANAGED_ROLE_DESCRIPTION_PREFIX);
}

export function mergeManagedAppRoles(currentRoles = [], desiredRoles = DEFAULT_APP_ROLES) {
  const preserved = currentRoles.filter((role) => !isManagedRole(role)).map(sanitizeAppRole);
  const desired = desiredRoles.map(sanitizeAppRole);
  const merged = [...preserved, ...desired];
  const currentComparable = JSON.stringify(currentRoles.map(sanitizeAppRole));
  const mergedComparable = JSON.stringify(merged);
  return { merged, changed: currentComparable !== mergedComparable };
}

async function captureJson(exec, args, { allowFailure = false } = {}) {
  const result = await exec.capture("az", [...args, "-o", "json"], { allowFailure });
  const text = result.stdout.trim();
  if (!text) {
    if (allowFailure) return null;
    throw new Error(`Command returned no JSON: az ${args.join(" ")}`);
  }
  return JSON.parse(text);
}

async function captureTsv(exec, args) {
  const { stdout } = await exec.capture("az", [...args, "-o", "tsv"]);
  return stdout.trim();
}

async function getTenantId(exec) {
  return captureTsv(exec, ["account", "show", "--query", "tenantId"]);
}

async function getAppById(appId, exec) {
  return captureJson(exec, ["ad", "app", "show", "--id", appId]);
}

async function findExistingApp({ appId, appName, exec }) {
  if (appId) {
    return captureJson(exec, ["ad", "app", "show", "--id", appId], { allowFailure: true });
  }

  const apps = (await captureJson(exec, ["ad", "app", "list", "--display-name", appName])) ?? [];
  const exact = apps.filter((app) => String(app.displayName ?? "") === appName);
  if (exact.length === 0) return null;
  if (exact.length > 1) {
    throw new Error(
      `Multiple Entra applications share the display name '${appName}'. Re-run with --app-id <client-id> to target one explicitly.`,
    );
  }
  return exact[0];
}

function ensureSingleTenant(app) {
  const audience = String(app?.signInAudience ?? "");
  if (audience && audience !== SIGN_IN_AUDIENCE) {
    throw new Error(
      `Existing app '${app.displayName ?? app.appId}' is '${audience}', but Agentweaver requires '${SIGN_IN_AUDIENCE}'.`,
    );
  }
}

async function createApp({ appName, redirectUris, serviceManagementReference, exec, log }) {
  log.section("Creating Entra app registration");
  const args = [
    "ad",
    "app",
    "create",
    "--display-name",
    appName,
    "--sign-in-audience",
    SIGN_IN_AUDIENCE,
    "--is-fallback-public-client",
    "true",
  ];
  if (redirectUris.length > 0) {
    args.push("--public-client-redirect-uris", ...redirectUris);
  }
  if (serviceManagementReference) {
    args.push("--service-management-reference", serviceManagementReference);
  }
  const app = await captureJson(exec, args);
  log.ok(`Created app '${app.displayName}' (${app.appId}).`);
  return app;
}

function unionStrings(existing = [], desired = []) {
  return normalizeRedirectUris([...existing, ...desired]);
}

async function ensureFallbackPublicClient(app, { exec, log }) {
  if (app?.isFallbackPublicClient === true) {
    log.skip("isFallbackPublicClient already true.");
    return false;
  }
  log.section("Enabling isFallbackPublicClient");
  await exec.run("az", ["ad", "app", "update", "--id", app.appId, "--is-fallback-public-client", "true"]);
  log.ok("isFallbackPublicClient enabled.");
  return true;
}

async function ensureRedirectUris(app, desiredRedirectUris, { exec, log }) {
  const currentPublic = normalizeRedirectUris(app?.publicClient?.redirectUris ?? []);
  const currentWeb = normalizeRedirectUris(app?.web?.redirectUris ?? []);
  const mergedPublic = unionStrings(currentPublic, desiredRedirectUris);
  const publicChanged = JSON.stringify(currentPublic) !== JSON.stringify(mergedPublic);

  // Any of our managed redirect URIs found under `web` came from an older revision of this
  // script (or manual setup) and must move to `publicClient` -- `web` always requires a
  // client_secret/client_assertion at redemption, which this deployment cannot supply.
  const desiredSet = new Set(desiredRedirectUris.map((uri) => uri.toLowerCase()));
  const remainingWeb = currentWeb.filter((uri) => !desiredSet.has(uri.toLowerCase()));
  const webChanged = JSON.stringify(currentWeb) !== JSON.stringify(remainingWeb);

  if (!publicChanged && !webChanged) {
    log.skip("Redirect URIs already match the requested set under publicClient.");
    return false;
  }

  log.section("Updating redirect URIs");
  if (publicChanged) {
    await exec.run("az", ["ad", "app", "update", "--id", app.appId, "--public-client-redirect-uris", ...mergedPublic]);
  }
  if (webChanged) {
    log.info("Moving managed redirect URI(s) off the `web` platform onto `publicClient`.");
    await exec.run("az", ["ad", "app", "update", "--id", app.appId, "--web-redirect-uris", ...remainingWeb]);
  }
  log.ok("Redirect URIs updated.");
  return true;
}

async function ensureAppRoles(app, { exec, log }) {
  const { merged, changed } = mergeManagedAppRoles(app?.appRoles ?? []);
  if (!changed) {
    log.skip("Agentweaver App Roles already configured.");
    return false;
  }
  log.section("Patching App Roles");
  await exec.run("az", [
    "rest",
    "--method",
    "PATCH",
    "--uri",
    `https://graph.microsoft.com/v1.0/applications/${app.id}`,
    "--headers",
    "Content-Type=application/json",
    "--body",
    JSON.stringify({ appRoles: merged }),
  ]);
  log.ok("App Roles patched.");
  return true;
}

async function ensureServicePrincipal(app, { exec, log }) {
  const existing = await captureJson(exec, ["ad", "sp", "show", "--id", app.appId], { allowFailure: true });
  if (existing) {
    log.skip(`Service principal already exists (${existing.id}).`);
    return existing;
  }

  log.section("Creating service principal");
  const sp = await captureJson(exec, ["ad", "sp", "create", "--id", app.appId]);
  log.ok(`Created service principal ${sp.id}.`);
  return sp;
}

function buildResult({ app, servicePrincipal, tenantId, redirectUris }) {
  return {
    appId: app.appId,
    appName: app.displayName,
    appObjectId: app.id,
    redirectUris,
    servicePrincipalObjectId: servicePrincipal.id,
    signInAudience: app.signInAudience,
    tenantId,
  };
}

function printSummary(result, log) {
  log.section("Outputs summary");
  log.field("Entra app name", result.appName);
  log.field("Entra client ID", result.appId);
  log.field("Entra app object ID", result.appObjectId);
  log.field("Entra service principal object ID", result.servicePrincipalObjectId);
  log.field("Entra tenant ID", result.tenantId);
  log.field("Sign-in audience", result.signInAudience);
  log.field("Redirect URI(s) [publicClient]", result.redirectUris.join(", "));

  log.section("Agentweaver config handoff");
  log.info("Entra browser sign-in configuration:");
  log.field("Auth:Mode", "Entra");
  log.info("Environment / params-file values:");
  log.field("ENTRA_CLIENT_ID", result.appId);
  log.field("ENTRA_TENANT_ID", result.tenantId);
  log.info("Key Vault secret values to mirror current github-client-* wiring later:");
  log.field("entra-client-id", result.appId);
  log.field("entra-tenant-id", result.tenantId);
  log.info("Appsettings / Kubernetes env names to mirror later:");
  log.field("Auth__Entra__ClientId", result.appId);
  log.field("Auth__Entra__TenantId", result.tenantId);
  log.field("Auth__Entra__RedirectUri", result.redirectUris[0] ?? "");
  log.warn(
    "Auth__Entra__ClientSecret must stay unset. Redirect URIs on this app are registered under "
    + "the `publicClient` platform (not `web`), which is what lets the API redeem authorization "
    + "codes with PKCE only. Supplying a client_secret here would not work: Microsoft Entra ID "
    + "requires client_secret/client_assertion for `web`-registered redirect URIs, and restricts "
    + "`spa`-registered redirect URIs to browser/CORS-origin redemption only -- neither matches a "
    + "server-side code exchange against a `publicClient` redirect URI. If a future need arises "
    + "for confidential-client auth, that requires re-registering the redirect URI under `web` and "
    + "supplying a client secret (blocked by policy on tenants that disallow client-secret "
    + "credentials), not adding a secret to this publicClient registration.");

  log.section("Grant platform roles (required before anyone can sign in)");
  log.warn(
    "Creating this app registration does NOT grant anyone access. Every Auth:Mode=Entra sign-in "
    + "is rejected with 'Access denied. A recognized Agentweaver platform role is required.' until "
    + "a user or a group is assigned to one of the App Roles below.");
  log.info(`App Roles and their IDs (resourceId is the service principal object ID: ${result.servicePrincipalObjectId}):`);
  for (const role of DEFAULT_APP_ROLES) {
    log.field(role.value, role.id);
  }
  log.info("Assign a GROUP to a role (recommended -- e.g. an 'akspm' Entra group as Contributor):");
  log.info(
    `  GROUP_ID=$(az ad group show --group "akspm" --query id -o tsv)\n`
    + `  az rest --method POST \\\n`
    + `    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${result.servicePrincipalObjectId}/appRoleAssignedTo" \\\n`
    + `    --headers "Content-Type=application/json" \\\n`
    + `    --body '{"principalId":"'"$GROUP_ID"'","resourceId":"${result.servicePrincipalObjectId}","appRoleId":"85fd3442-8291-4d52-a76f-ee962e711d7f"}'`,
  );
  log.info("Assign a single USER to a role (e.g. alice@contoso.com as PlatformAdmin):");
  log.info(
    `  USER_ID=$(az ad user show --id "alice@contoso.com" --query id -o tsv)\n`
    + `  az rest --method POST \\\n`
    + `    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${result.servicePrincipalObjectId}/appRoleAssignedTo" \\\n`
    + `    --headers "Content-Type=application/json" \\\n`
    + `    --body '{"principalId":"'"$USER_ID"'","resourceId":"${result.servicePrincipalObjectId}","appRoleId":"38a31dfd-b8b8-42b3-a820-1521f6274955"}'`,
  );
  log.info(
    "List current role assignments to verify (or find the appRoleAssignment id to remove one):",
  );
  log.info(
    `  az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${result.servicePrincipalObjectId}/appRoleAssignedTo"`,
  );
}

export async function run({ argv = [], exec = execDefault, log = logDefault } = {}) {
  const { flags, help } = parseArgs(argv);
  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  const appName = String(flags.APP_NAME ?? DEFAULT_APP_NAME).trim();
  if (!appName) throw new Error("--app-name cannot be empty.");

  const desiredRedirectUris = normalizeRedirectUris(
    flags.REDIRECT_URIS.length > 0 ? flags.REDIRECT_URIS : DEFAULT_REDIRECT_URIS,
  );

  log.banner(
    "Agentweaver Entra app bootstrap",
    "Optional bootstrap for Auth:Mode=Entra.",
    "Creates or reuses a single-tenant app registration, App Roles, and service principal.",
  );

  const tenantId = await getTenantId(exec);

  let app = await findExistingApp({ appId: flags.APP_ID, appName, exec });
  if (app) {
    ensureSingleTenant(app);
    log.ok(`Using existing app '${app.displayName}' (${app.appId}).`);
  } else {
    app = await createApp({
      appName,
      redirectUris: desiredRedirectUris,
      serviceManagementReference: flags.SERVICE_MANAGEMENT_REFERENCE,
      exec,
      log,
    });
  }

  await ensureFallbackPublicClient(app, { exec, log });
  await ensureRedirectUris(app, desiredRedirectUris, { exec, log });
  app = await getAppById(app.appId, exec);
  ensureSingleTenant(app);

  await ensureAppRoles(app, { exec, log });
  app = await getAppById(app.appId, exec);

  const servicePrincipal = await ensureServicePrincipal(app, { exec, log });
  const result = buildResult({
    app,
    servicePrincipal,
    tenantId,
    redirectUris: normalizeRedirectUris(app?.publicClient?.redirectUris ?? desiredRedirectUris),
  });

  printSummary(result, log);
  return { ok: true, ...result };
}

/* c8 ignore start -- process.argv entry point, not exercised by unit tests */
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  run({ argv: process.argv.slice(2) }).catch((err) => {
    const showStack = Boolean(process.env.DEBUG || process.env.AGENTWEAVER_DEBUG);
    logDefault.error((showStack && err?.stack) || err?.message || String(err));
    process.exitCode = 1;
  });
}
/* c8 ignore stop */
