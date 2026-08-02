// variables.mjs -- Shared Azure configuration resolution.
//
// Infrastructure defaults remain aligned with the legacy variable scripts,
// except image identity and KEYVAULT_NAME are deliberately NOT
// command-driven-by-hardcoded-default:
//   - Most inputs have an env-var override with a hardcoded default (see
//     DEFAULTS below), exactly matching `${VAR:-default}` in the bash script.
//   - KEYVAULT_NAME is the one deliberate exception: it has NO hardcoded
//     default (see resolveKeyvaultName() below) -- a wrong-but-plausible
//     name here doesn't just fail to find a resource, it silently redirects
//     rendered Key Vault references (and the GitHub OAuth secret lookups
//     that flow from them) at the wrong vault. Every caller MUST supply it
//     explicitly (env var, params file, or provision-infra's prompt).
//   - TENANT_ID, IDENTITY_CLIENT_ID, APPINSIGHTS_WORKSPACE_ID are resolved
//     LIVE from `az` only if not already supplied via env, and failures are
//     swallowed to '' (the bash script's `... || true` tolerance) rather than
//     aborting resolution.
//   - IMAGE_TAG: prefer IMAGE_TAG env var; else current git short SHA.
//     Release commands must explicitly provide their published semver tag;
//     generic variable resolution never invents release identity from VERSION.
//     `latest`/`latest-release` are always rejected. AGENTHOST_IMAGE_TAG
//     defaults to IMAGE_TAG when not set.
//
// Live `az` resolution is LAZY and OPTIONAL: pass `{ resolveLive: false }` to
// skip it entirely (fields resolve to ''), and/or inject stub
// az/git implementations via `{ az: {...}, git: {...} }` -- this is what lets
// scripts/azure/tests/* run without any real Azure CLI or git present.

import path from "node:path";
import { fileURLToPath } from "node:url";
import { capture } from "./lib/exec.mjs";
import * as azDefault from "./lib/az.mjs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// scripts/azure/variables.mjs -> repo root is two levels up (mirrors the bash
// script's VARIABLES_DIR=scripts/aks -> REPO_ROOT=VARIABLES_DIR/../..).
export const DEFAULT_REPO_ROOT = path.resolve(__dirname, "..", "..");

export const IDENTITY_NAME = "agentweaver-api-identity";
// Dedicated least-privilege identity for AgentHost sandbox pods (issue #471); no Key Vault roles.
export const AGENTHOST_IDENTITY_NAME = "agentweaver-agenthost-identity";
export const LOG_ANALYTICS_WORKSPACE_NAME = "agentweaver-logs";

export const DEFAULTS = Object.freeze({
  RESOURCE_GROUP: "agentweaver-rg",
  CLUSTER_NAME: "agentweaver-aks",
  ACR_NAME: "agentweaverregistry",
  LOCATION: "westus2",
  // Default for new AKS clusters/nodepools only. Existing clusters are left as-is because
  // 10-create-cluster.mjs skips `az aks create`/`az aks nodepool add` when those resources exist.
  NODE_VM_SIZE: "Standard_D4s_v6",
  PG_SERVER_NAME: "agentweaver-pg",
  PG_HA_MODE: "ZoneRedundant",
  PG_ACCESS_MODE: "private",
  // Deliberately NO default here (see resolveKeyvaultName() below): unlike
  // the other resource names, a wrong-but-plausible Key Vault name doesn't
  // just fail to find a resource -- it silently redirects the rendered
  // ConfigMap/SecretProviderClass Key Vault references (and the GitHub OAuth
  // secret lookups that flow from them) at a DIFFERENT vault, which can fail
  // silently instead of loudly. Incident: a generic "agentweaver-kv" default
  // here was never a real vault in any provisioned subscription; see
  // scripts/harness-shared/learnings.md for the full writeup.
  NAMESPACE: "agentweaver",
  KATA_POOL_NAME: "katapool",
  APP_POOL_NAME: "apppool",
  GITHUB_ALLOWED_ORG: "microsoft",
  // NOTE: this is the literal AuthModeResolver.Parse() recognizes for legacy GitHub auth
  // (apps/Agentweaver.Api/Auth/AuthMode.cs) -- it must be exactly "GitHubLegacy", NOT "GitHub".
  // Parse() treats any value other than a case-insensitive match of "GitHubLegacy" as Entra, and
  // appsettings.json's own default is already "Auth:Mode": "Entra". Defaulting AUTH_MODE here to
  // anything but the exact "GitHubLegacy" string would silently flip every deployment that doesn't
  // set AUTH_MODE over to Entra mode instead of preserving today's GitHub sign-in behavior.
  AUTH_MODE: "GitHubLegacy",
});

/** Reject 'latest'/'latest-release'; accept a git short SHA (7-40 hex) or a 'v'-prefixed semver. */
const SHORT_SHA_RE = /^[0-9a-f]{7,40}$/;
const SEMVER_TAG_RE = /^v\d+\.\d+\.\d+/;
const QUALIFIED_IMAGE_REFERENCE_RE =
  /^(?<registry>(?:localhost(?::\d+)?|[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:(?::\d+)|(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+(?:\:\d+)?)))\/(?<repository>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)(?::(?<tag>[\w][\w.-]{0,127})|@(?<digest>sha256:[A-Fa-f0-9]{64}))$/;

export class InvalidImageTagError extends Error {}
export class InvalidImageReferenceError extends Error {}

/** Thrown when a variable with no safe generic default (e.g. KEYVAULT_NAME) is unresolved. */
export class MissingRequiredVariableError extends Error {}

/**
 * Resolves KEYVAULT_NAME from an explicit env override ONLY -- there is no
 * hardcoded fallback (see the DEFAULTS comment above for why). Fails fast
 * with an actionable message instead of silently deploying against a
 * plausible-but-wrong vault name.
 * @param {Record<string,string>} env
 * @returns {string}
 */
export function resolveKeyvaultName(env) {
  const name = env.KEYVAULT_NAME;
  if (!name) {
    throw new MissingRequiredVariableError(
      "KEYVAULT_NAME is not set and there is no default -- it must be the name of the Key Vault already " +
        "provisioned for this environment. Set the KEYVAULT_NAME environment variable (or pass it via your " +
        "params file / the provision-infra flow) to the real vault name, e.g.: " +
        "`az keyvault list --resource-group <RESOURCE_GROUP> --query \"[].name\" -o tsv`.",
    );
  }
  return name;
}

/**
 * Validates an image tag exactly like `_validate_image_tag` in 00-variables.sh:
 * rejects 'latest'/'latest-release', then requires either a short git SHA or
 * a `vMAJOR.MINOR.PATCH[-prerelease][+build]` semver tag.
 * @param {string} tag
 * @param {string} name field name, used only in the error message.
 */
export function validateImageTag(tag, name) {
  if (tag === "latest" || tag === "latest-release") {
    throw new InvalidImageTagError(`${name} must be immutable; do not use '${tag}'.`);
  }
  if (SHORT_SHA_RE.test(tag) || SEMVER_TAG_RE.test(tag)) {
    return true;
  }
  throw new InvalidImageTagError(`${name}='${tag}' is not a valid tag (expected git SHA or vX.Y.Z semver).`);
}

/**
 * Validates a fully-qualified container image reference of the form
 * `registry/repository:tag` or `registry/repository@sha256:<digest>`.
 *
 * The registry prefix is mandatory on purpose: this mode explicitly trusts an
 * operator-chosen external image source, so partial/shorthand refs (for
 * example `agentweaver-api:latest`) are rejected rather than guessed.
 *
 * @param {string} ref
 * @param {string} name field name, used only in the error message.
 * @returns {true}
 */
export function validateQualifiedImageReference(ref, name) {
  const value = String(ref ?? "").trim();
  if (!value) {
    throw new InvalidImageReferenceError(
      `${name} is required and must be a fully-qualified image reference (registry/repository:tag or registry/repository@sha256:digest).`,
    );
  }
  if (!QUALIFIED_IMAGE_REFERENCE_RE.test(value)) {
    throw new InvalidImageReferenceError(
      `${name}='${value}' is not a valid fully-qualified image reference (expected registry/repository:tag or registry/repository@sha256:digest).`,
    );
  }
  return true;
}

async function defaultGitShortSha(repoRoot) {
  try {
    const { stdout } = await capture("git", ["-C", repoRoot, "rev-parse", "--short", "HEAD"], { allowFailure: true });
    return stdout.trim();
  } catch {
    return "";
  }
}

/**
 * Derives IMAGE_TAG from an explicit env override or the current git short
 * SHA. Release workflows provide their semver tag explicitly.
 */
export async function deriveImageTag({
  env = process.env,
  repoRoot = DEFAULT_REPO_ROOT,
  gitShortSha = defaultGitShortSha,
} = {}) {
  let tag = env.IMAGE_TAG;
  if (!tag) {
    tag = await gitShortSha(repoRoot);
  }
  if (!tag) {
    throw new InvalidImageTagError("IMAGE_TAG is not set and the current git SHA could not be resolved.");
  }
  validateImageTag(tag, "IMAGE_TAG");
  return tag;
}

/**
 * Resolves the full Agentweaver AKS variable set, matching 00-variables.sh /
 * .ps1 field-for-field.
 *
 * @param {object} [options]
 * @param {Record<string,string>} [options.env] Defaults to process.env.
 * @param {string} [options.repoRoot] Defaults to the real repo root.
 * @param {boolean} [options.resolveLive] When false, TENANT_ID/
 *   IDENTITY_CLIENT_ID/APPINSIGHTS_WORKSPACE_ID resolve to '' instead of
 *   shelling out to `az` (default: true).
 * @param {typeof import('./lib/az.mjs')} [options.az] Injectable az.mjs
 *   module (or a stub with the same function names) for testing.
 * @param {(repoRoot: string) => Promise<string>} [options.gitShortSha]
 *   Injectable git short-SHA resolver for testing.
 * @returns {Promise<Record<string, string>>}
 */
export async function resolveVariables(options = {}) {
  const {
    env = process.env,
    repoRoot = DEFAULT_REPO_ROOT,
    resolveLive = true,
    az = azDefault,
    gitShortSha = defaultGitShortSha,
  } = options;

  const pick = (name) => env[name] || DEFAULTS[name];

  const RESOURCE_GROUP = pick("RESOURCE_GROUP");
  const CLUSTER_NAME = pick("CLUSTER_NAME");
  const ACR_NAME = pick("ACR_NAME");
  const LOCATION = pick("LOCATION");
  const NODE_VM_SIZE = pick("NODE_VM_SIZE");
  const PG_SERVER_NAME = pick("PG_SERVER_NAME");
  const PG_LOCATION = env.PG_LOCATION || LOCATION;
  const PG_HA_MODE = pick("PG_HA_MODE");
  const PG_ACCESS_MODE = pick("PG_ACCESS_MODE");
  const NAMESPACE = pick("NAMESPACE");
  const KATA_POOL_NAME = pick("KATA_POOL_NAME");
  const APP_POOL_NAME = pick("APP_POOL_NAME");

  const KEYVAULT_NAME = resolveKeyvaultName(env);
  const AGENTHOST_KEYVAULT_URI =
    env.AGENTHOST_KEYVAULT_URI || `https://${KEYVAULT_NAME}.vault.azure.net/`;

  const GITHUB_ALLOWED_ORG = env.GITHUB_ALLOWED_ORG || DEFAULTS.GITHUB_ALLOWED_ORG;
  const IMAGE_API = env.IMAGE_API || "";
  const IMAGE_FRONTEND = env.IMAGE_FRONTEND || "";
  const IMAGE_MCP = env.IMAGE_MCP || "";
  const IMAGE_AGENT_HOST = env.IMAGE_AGENT_HOST || "";
  for (const [name, value] of Object.entries({ IMAGE_API, IMAGE_FRONTEND, IMAGE_MCP, IMAGE_AGENT_HOST })) {
    if (value) validateQualifiedImageReference(value, name);
  }

  // Auth:Mode / Auth:Entra:* deploy-time wiring (issue: Entra sign-in endpoints from #653/#658
  // were never actually enabled on deployed environments). ENTRA_CLIENT_ID/ENTRA_TENANT_ID have no
  // safe generic default -- unset means "Entra mode misconfigured" -- so they resolve to "" and are
  // only meaningful when AUTH_MODE=Entra.
  const AUTH_MODE = env.AUTH_MODE || DEFAULTS.AUTH_MODE;
  const ENTRA_CLIENT_ID = env.ENTRA_CLIENT_ID || "";
  const ENTRA_TENANT_ID = env.ENTRA_TENANT_ID || "";
  // These are deliberately opt-in: a local Azure CLI timeout does not prove
  // whether a remote ACR build/import completed, so callers must reconcile
  // the target tag/digest before deciding whether a retry is safe.
  const ACR_BUILD_TIMEOUT_MS = env.ACR_BUILD_TIMEOUT_MS || "";
  const ACR_IMPORT_TIMEOUT_MS = env.ACR_IMPORT_TIMEOUT_MS || "";

  let TENANT_ID = env.TENANT_ID || "";
  if (!TENANT_ID && resolveLive) {
    TENANT_ID = await az.getTenantId();
  }

  let IDENTITY_CLIENT_ID = env.IDENTITY_CLIENT_ID || "";
  if (!IDENTITY_CLIENT_ID && resolveLive) {
    IDENTITY_CLIENT_ID = await az.getIdentityClientId(RESOURCE_GROUP, IDENTITY_NAME);
  }

  // Dedicated AgentHost identity client id (issue #471). Resolved the same way as the API identity;
  // stays '' when the identity has not been provisioned yet (older cluster) so the deploy degrades
  // gracefully — an empty annotation just means the sandbox pod gets no workload identity, and the
  // GitHub token is brokered via the API /configure call regardless.
  let AGENTHOST_IDENTITY_CLIENT_ID = env.AGENTHOST_IDENTITY_CLIENT_ID || "";
  if (!AGENTHOST_IDENTITY_CLIENT_ID && resolveLive) {
    AGENTHOST_IDENTITY_CLIENT_ID = await az.getIdentityClientId(RESOURCE_GROUP, AGENTHOST_IDENTITY_NAME);
  }

  let APPINSIGHTS_WORKSPACE_ID = env.APPINSIGHTS_WORKSPACE_ID || "";
  if (!APPINSIGHTS_WORKSPACE_ID && resolveLive) {
    APPINSIGHTS_WORKSPACE_ID = await az.getLogAnalyticsWorkspaceCustomerId(
      RESOURCE_GROUP,
      LOG_ANALYTICS_WORKSPACE_NAME,
    );
  }

  const IMAGE_TAG = await deriveImageTag({ env, repoRoot, gitShortSha });

  let AGENTHOST_IMAGE_TAG = env.AGENTHOST_IMAGE_TAG || IMAGE_TAG;
  if (env.AGENTHOST_IMAGE_TAG) {
    validateImageTag(AGENTHOST_IMAGE_TAG, "AGENTHOST_IMAGE_TAG");
  }

  const ACR_LOGIN_SERVER = `${ACR_NAME}.azurecr.io`;

  return {
    RESOURCE_GROUP,
    CLUSTER_NAME,
    ACR_NAME,
    LOCATION,
    NODE_VM_SIZE,
    PG_SERVER_NAME,
    PG_LOCATION,
    PG_HA_MODE,
    PG_ACCESS_MODE,
    NAMESPACE,
    KATA_POOL_NAME,
    APP_POOL_NAME,
    IMAGE_TAG,
    AGENTHOST_IMAGE_TAG,
    ACR_LOGIN_SERVER,
    KEYVAULT_NAME,
    AGENTHOST_KEYVAULT_URI,
    GITHUB_ALLOWED_ORG,
    IMAGE_API,
    IMAGE_FRONTEND,
    IMAGE_MCP,
    IMAGE_AGENT_HOST,
    AUTH_MODE,
    ENTRA_CLIENT_ID,
    ENTRA_TENANT_ID,
    ACR_BUILD_TIMEOUT_MS,
    ACR_IMPORT_TIMEOUT_MS,
    TENANT_ID,
    IDENTITY_CLIENT_ID,
    AGENTHOST_IDENTITY_CLIENT_ID,
    APPINSIGHTS_WORKSPACE_ID,
  };
}

/** Prints the "=== Agentweaver AKS variables ===" summary block, matching 00-variables.sh's echo output. */
export function printSummary(vars, log) {
  log.section("Agentweaver AKS variables");
  log.field("Resource Group", vars.RESOURCE_GROUP);
  log.field("Cluster", vars.CLUSTER_NAME);
  log.field("ACR", vars.ACR_LOGIN_SERVER);
  log.field("Location", vars.LOCATION);
  log.field("Node VM size", vars.NODE_VM_SIZE);
  log.field("Postgres server", vars.PG_SERVER_NAME);
  log.field("Postgres location", vars.PG_LOCATION);
  log.field("Postgres HA mode", vars.PG_HA_MODE);
  log.field("Postgres access mode", vars.PG_ACCESS_MODE);
  log.field("Namespace", vars.NAMESPACE);
  log.field("Kata pool", vars.KATA_POOL_NAME);
  log.field("App pool", vars.APP_POOL_NAME);
  log.field("Image tag", vars.IMAGE_TAG);
  log.field("AgentHost tag", vars.AGENTHOST_IMAGE_TAG);
  log.field("Key Vault", vars.KEYVAULT_NAME);
  log.field("AgentHost KV", vars.AGENTHOST_KEYVAULT_URI);
  log.field("Tenant ID", vars.TENANT_ID || "<not set>");
  log.field("Identity client", vars.IDENTITY_CLIENT_ID || "<not set>");
  log.field("AgentHost identity client", vars.AGENTHOST_IDENTITY_CLIENT_ID || "<not set>");
  log.field("AppInsights workspace", vars.APPINSIGHTS_WORKSPACE_ID || "<not set>");
}
