// provision-infra.mjs -- The `azure:provision-infra` interactive smart installer, replacing
// install.sh/install.ps1. Faithful behavior parity for the underlying steps
// and their pipeline ordering; the prompt-driven UX itself is new (not a 1:1
// script port), per specs/006-memory-and-decision-inbox/plan.md's P5 scope.
//
// This module ONLY does Azure deploys. Local dev environment setup (prereq
// checks + npm/dotnet restore, no Azure calls at all) lives in dev.mjs's
// `--setup` flag (`npm run setup` / `node scripts/azure/cli.mjs dev
// --setup`) instead -- it was originally a `--local` flag here, but nesting
// a "skip Azure entirely" mode under the `azure:provision-infra` command was
// confusing (provision-infra.mjs's own name says Azure infrastructure). dev.mjs is already the
// canonical "local dev" entry point, so local setup belongs there.
//
// MODES:
//   AKS/Azure deploy mode, mirroring install.sh's
//            install_aks() pipeline order, confirmed from install.sh source:
//              10-create-cluster
//              -> 15-setup-identity
//              -> 15-provision-monitoring
//              -> 16-provision-oauth-signing-key (unless --skip-oauth-key)
//              -> 17-provision-postgres          (unless --skip-postgres)
//              -> 20-build-push-images
//              -> 25-verify-image-provenance
//              -> gen-a2a-mtls-certs
//              -> 30-deploy
//              -> 40-verify
//            (steps/30-deploy.mjs ALSO idempotently provisions monitoring and
//            regenerates A2A mTLS certs internally if they are missing --
//            calling them here first is harmless/idempotent and keeps this
//            pipeline's ordering an honest, literal match of install.sh's
//            documented step order.)
//
// INTERACTIVE SMART INSTALLER: triggered only when `provision-infra` is invoked with
// NO CLI arguments at all AND stdin/stdout are a TTY (prompt.isInteractive()).
// It prompts for Azure subscription (lib/az.mjs, current default shown
// first), resource group (existing list, or "Create new..."), location
// (region list, smart default from variables.mjs's DEFAULTS.LOCATION),
// cluster/ACR/Key Vault names (prefilled with variables.mjs defaults,
// editable), GitHub OAuth client id + secret (secret prompt, no echo,
// preceded by step-by-step GitHub OAuth App creation guidance), and the
// GitHub org(s) allowed to sign in (GITHUB_ALLOWED_ORG, comma-separated,
// validated/reprompted on invalid input). The collected answers are injected as the HIGHEST-precedence config
// source (same bucket as CLI flags) before lib/config.mjs's resolveConfig()
// runs, so resolveConfig's own generic per-field prompt fallback never
// re-prompts for anything the guided flow already collected.
//
// NON-INTERACTIVE PATH: any other invocation (flags present, and/or no TTY)
// resolves config via lib/config.mjs's precedence (flags > env > params-file
// > defaults > prompt) and never blocks on a prompt that cannot appear --
// resolveConfig() surfaces a clear, actionable error naming the missing
// field(s) instead of hanging.
//
// SECRET HANDLING: GITHUB_CLIENT_SECRET is registered with lib/secret.mjs's
// redaction registry the instant it is known (both in the guided-prompt path
// and via config.mjs's `secret: true` field spec) and is NEVER printed,
// logged, or included in the OUTPUTS SUMMARY.

import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import * as azDefault from "./lib/az.mjs";
import * as promptDefault from "./lib/prompt.mjs";
import { registerSecret } from "./lib/secret.mjs";
import { resolveGitHubRepository } from "./lib/github.mjs";
import { resolveConfig, loadParamsFile } from "./lib/config.mjs";
import { resolveVariables, DEFAULTS, DEFAULT_REPO_ROOT } from "./variables.mjs";

import * as createClusterDefault from "./steps/10-create-cluster.mjs";
import * as setupIdentityDefault from "./steps/15-setup-identity.mjs";
import * as provisionMonitoringDefault from "./steps/15-provision-monitoring.mjs";
import * as oauthSigningKeyDefault from "./steps/16-provision-oauth-signing-key.mjs";
import * as provisionPostgresDefault from "./steps/17-provision-postgres.mjs";
import * as buildImagesDefault from "./steps/20-build-push-images.mjs";
import * as verifyProvenanceDefault from "./steps/25-verify-image-provenance.mjs";
import * as genA2aMtlsCertsDefault from "./steps/gen-a2a-mtls-certs.mjs";
import * as deployStepDefault from "./steps/30-deploy.mjs";
import * as verifyStepDefault from "./steps/40-verify.mjs";

// Suggested Key Vault name for a FRESH provisioning run only -- unlike the
// deploy-only path (variables.mjs's resolveKeyvaultName(), which has NO
// default), provisioning always creates whatever vault name is given here,
// so a generic suggestion carries none of the "silently connects to the
// wrong existing vault" risk that motivated removing the deploy-time
// default. Still fully editable (interactive prompt) or overridable
// (--keyvault-name flag / KEYVAULT_NAME env / params file).
const PROVISION_KEYVAULT_NAME_SUGGESTION = "agentweaver-kv";

/**
 * Parses `provision-infra` subcommand argv into a flags object plus a paramsFile path.
 * Recognizes: --skip-postgres, --skip-oauth-key, --force,
 * --image-tag <tag>, --image-source <acr-build|ghcr>, --ghcr-ref <ref>,
 * --ghcr-token <token> (or =value forms),
 * --params-file/--config <path>, --resource-group, --cluster-name,
 * --acr-name, --location, --keyvault-name, --postgres-server-name, --namespace,
 * --github-client-id, --github-client-secret, -h/--help.
 */
export function parseArgs(argv = []) {
  const flags = {};
  let paramsFile;
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
    if (arg === "--skip-postgres") {
      flags.SKIP_POSTGRES = true;
    } else if (arg === "--skip-oauth-key") {
      flags.SKIP_OAUTH_KEY = true;
    } else if (arg === "--force") {
      flags.FORCE = true;
    } else if (arg === "-h" || arg === "--help") {
      help = true;
    } else if (arg === "--image-tag" || arg.startsWith("--image-tag=")) {
      const { value, consumed } = takeValue(i, "--image-tag");
      flags.IMAGE_TAG = value;
      i += consumed;
    } else if (arg === "--image-source" || arg.startsWith("--image-source=")) {
      const { value, consumed } = takeValue(i, "--image-source");
      flags.IMAGE_SOURCE = value;
      i += consumed;
    } else if (arg === "--ghcr-ref" || arg.startsWith("--ghcr-ref=")) {
      const { value, consumed } = takeValue(i, "--ghcr-ref");
      flags.GHCR_REF = value;
      i += consumed;
    } else if (arg === "--ghcr-token" || arg.startsWith("--ghcr-token=")) {
      const { value, consumed } = takeValue(i, "--ghcr-token");
      flags.GHCR_TOKEN = value;
      i += consumed;
    } else if (arg === "--params-file" || arg === "--config" || arg.startsWith("--params-file=") || arg.startsWith("--config=")) {
      const { value, consumed } = takeValue(i, "--params-file");
      paramsFile = value;
      i += consumed;
    } else if (arg === "--resource-group" || arg.startsWith("--resource-group=")) {
      const { value, consumed } = takeValue(i, "--resource-group");
      flags.RESOURCE_GROUP = value;
      i += consumed;
    } else if (arg === "--cluster-name" || arg.startsWith("--cluster-name=")) {
      const { value, consumed } = takeValue(i, "--cluster-name");
      flags.CLUSTER_NAME = value;
      i += consumed;
    } else if (arg === "--acr-name" || arg.startsWith("--acr-name=")) {
      const { value, consumed } = takeValue(i, "--acr-name");
      flags.ACR_NAME = value;
      i += consumed;
    } else if (arg === "--location" || arg.startsWith("--location=")) {
      const { value, consumed } = takeValue(i, "--location");
      flags.LOCATION = value;
      i += consumed;
    } else if (arg === "--keyvault-name" || arg.startsWith("--keyvault-name=")) {
      const { value, consumed } = takeValue(i, "--keyvault-name");
      flags.KEYVAULT_NAME = value;
      i += consumed;
    } else if (arg === "--postgres-server-name" || arg.startsWith("--postgres-server-name=")) {
      const { value, consumed } = takeValue(i, "--postgres-server-name");
      flags.PG_SERVER_NAME = value;
      i += consumed;
    } else if (arg === "--namespace" || arg.startsWith("--namespace=")) {
      const { value, consumed } = takeValue(i, "--namespace");
      flags.NAMESPACE = value;
      i += consumed;
    } else if (arg === "--github-client-id" || arg.startsWith("--github-client-id=")) {
      const { value, consumed } = takeValue(i, "--github-client-id");
      flags.GITHUB_CLIENT_ID = value;
      i += consumed;
    } else if (arg === "--github-client-secret" || arg.startsWith("--github-client-secret=")) {
      const { value, consumed } = takeValue(i, "--github-client-secret");
      flags.GITHUB_CLIENT_SECRET = value;
      i += consumed;
    } else if (arg === "--github-allowed-org" || arg.startsWith("--github-allowed-org=")) {
      const { value, consumed } = takeValue(i, "--github-allowed-org");
      flags.GITHUB_ALLOWED_ORG = value;
      i += consumed;
    } else {
      throw new Error(`Unknown argument: ${arg}. Run 'provision-infra --help' for usage.`);
    }
  }

  return { flags, paramsFile, help };
}

export const HELP_TEXT = `provision-infra -- Agentweaver Azure infrastructure installer (replaces install.sh/install.ps1's install_aks())

Usage:
  node scripts/azure/cli.mjs provision-infra                 Interactive smart installer (TTY only)
  node scripts/azure/cli.mjs provision-infra [flags]          Non-interactive Azure deploy

Local dev environment setup (no Azure) lives under 'dev --setup' instead:
  node scripts/azure/cli.mjs dev --setup             Checks prereqs, installs deps (no Azure)

Flags:
  --skip-postgres             Skip Postgres provisioning (17-provision-postgres).
  --skip-oauth-key            Skip MCP OAuth signing key provisioning (16-provision-oauth-signing-key).
  --force                     Allow GHCR import to overwrite an existing target ACR tag if the digest differs.
  --image-tag <tag>           Use this image tag instead of the derived default.
  --image-source <source>     Image source: 'acr-build' (default) or 'ghcr'.
  --ghcr-ref <ref>            Required with --image-source ghcr; only accepts immutable refs (vX.Y.Z or sha-<hex>).
  --ghcr-token <token>        Optional GHCR registry token for private-package import; NEVER echoed/logged.
  --params-file <path>        JSON/JSONC params file (see scripts/azure/params.example.json).
  --config <path>             Alias for --params-file.
  --resource-group <name>
  --cluster-name <name>
  --acr-name <name>
  --location <region>
  --keyvault-name <name>
  --postgres-server-name <name>
  --namespace <name>
  --github-client-id <id>
  --github-client-secret <secret>   NEVER echoed/logged; prefer env/params-file/prompt instead.
  --github-allowed-org <orgs>  Comma-separated GitHub org login(s) allowed to sign in (default: microsoft).
  -h, --help                  Show this help.

Need a GitHub OAuth App? Create one at https://github.com/settings/applications/new -- the
interactive installer walks you through this (name, homepage, callback URL) before prompting
for the client ID/secret.

Config precedence: flags > env > params-file > detected defaults > prompt.
Non-interactive (no TTY) never prompts -- missing required fields fail with a clear error.
`;

const IMAGE_SOURCE_VALUES = Object.freeze(["acr-build", "ghcr"]);
const POSTGRES_SERVER_NAME_RE = /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$/;

/**
 * A plausible GitHub org login: starts with an alphanumeric, up to 39 chars
 * total, letters/digits/hyphens only. Matches GitHub's own login constraints
 * closely enough to catch typos without being a full API round-trip.
 */
const GITHUB_ORG_LOGIN_RE = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;

/**
 * A plausible GitHub team slug/display-name: letters/digits/hyphens/spaces.
 * apps/Agentweaver.Api/Auth/GitHubOrgList.cs defensively slugifies a display
 * name with spaces or uppercase (e.g. "AKS PM" -> "aks-pm"), so this is
 * intentionally looser than a strict lowercase-hyphenated slug check -- we
 * only need to catch obviously-invalid input here, not enforce the exact
 * canonical form.
 */
const GITHUB_TEAM_RE = /^[A-Za-z0-9][A-Za-z0-9 -]{0,100}$/;

/** Splits on the same delimiters GitHubOrgList.cs uses: ',' and ';'. */
const GITHUB_ORG_LIST_SEPARATORS = /[,;]/;

/**
 * True when `entry` is a valid single allow-rule: `*` (all organizations), a
 * bare org login, `org/*` (explicit organization wildcard), or
 * `org/team-slug`. Mirrors the parsing rules in
 * apps/Agentweaver.Api/Auth/GitHubOrgList.cs so the CLI's validation never
 * rejects a value the backend actually accepts.
 * @param {string} entry
 * @returns {boolean}
 */
function isValidGithubOrgEntry(entry) {
  if (entry === "*") return true;
  const slashIndex = entry.indexOf("/");
  if (slashIndex < 0) {
    return GITHUB_ORG_LOGIN_RE.test(entry);
  }
  const org = entry.slice(0, slashIndex).trim();
  const team = entry.slice(slashIndex + 1).trim();
  if (!GITHUB_ORG_LOGIN_RE.test(org)) return false;
  if (team.length === 0 || team === "*") return true;
  return GITHUB_TEAM_RE.test(team);
}

/**
 * Validates a comma/semicolon-separated GitHub allowlist string. Each entry
 * may be `*`, a bare org login, `org/*`, or `org/team-slug` -- the same
 * mixed-list grammar apps/Agentweaver.Api/Auth/GitHubOrgList.cs parses at
 * runtime.
 * Returns `true` when every entry is valid, or an actionable error message
 * string otherwise (used both as prompt.text()'s reprompt validator and as a
 * config.mjs field validator for the non-interactive path).
 * @param {string} value
 * @returns {true|string}
 */
export function validateGithubOrgList(value) {
  const orgs = String(value ?? "")
    .split(GITHUB_ORG_LIST_SEPARATORS)
    .map((o) => o.trim())
    .filter((o) => o.length > 0);
  if (orgs.length === 0) {
    return "Enter at least one GitHub org login (comma-separated), e.g. 'microsoft' or 'microsoft,azure/some-team'.";
  }
  const invalid = orgs.find((o) => !isValidGithubOrgEntry(o));
  if (invalid) {
    return `'${invalid}' doesn't look like a valid '*', 'org', 'org/*', or 'org/team-slug' entry (letters, numbers, hyphens; max 39 chars for the org).`;
  }
  return true;
}

/** Trims/dedupes-empty and rejoins a comma/semicolon-separated GitHub org allowlist string. */
export function normalizeGithubOrgList(value) {
  return String(value ?? "")
    .split(GITHUB_ORG_LIST_SEPARATORS)
    .map((o) => o.trim())
    .filter((o) => o.length > 0)
    .join(",");
}

function validatePostgresServerName(value) {
  const name = String(value ?? "");
  if (!POSTGRES_SERVER_NAME_RE.test(name)) {
    return "PG_SERVER_NAME must be 3-63 chars of lowercase letters, numbers, or hyphens, and cannot start or end with a hyphen.";
  }
  return true;
}

/** Builds the lib/config.mjs field schema for the AKS deploy config. */
function buildSchema({ prompt, az }) {
  return {
    RESOURCE_GROUP: { default: DEFAULTS.RESOURCE_GROUP },
    CLUSTER_NAME: { default: DEFAULTS.CLUSTER_NAME },
    ACR_NAME: { default: DEFAULTS.ACR_NAME },
    LOCATION: { default: DEFAULTS.LOCATION },
    KEYVAULT_NAME: { default: PROVISION_KEYVAULT_NAME_SUGGESTION },
    PG_SERVER_NAME: {
      default: DEFAULTS.PG_SERVER_NAME,
      validate: (value) => {
        const result = validatePostgresServerName(value);
        return result === true ? undefined : result;
      },
    },
    NAMESPACE: { default: DEFAULTS.NAMESPACE },
    IMAGE_SOURCE: {
      default: "acr-build",
      validate: (value) => (
        IMAGE_SOURCE_VALUES.includes(String(value))
          ? undefined
          : `IMAGE_SOURCE must be one of: ${IMAGE_SOURCE_VALUES.join(", ")}.`
      ),
    },
    GHCR_REF: {
      validate: (value, config) => {
        if (config.IMAGE_SOURCE !== "ghcr") return undefined;
        return value ? undefined : "GHCR_REF is required when IMAGE_SOURCE=ghcr.";
      },
    },
    GHCR_TOKEN: {
      secret: true,
    },
    GITHUB_CLIENT_ID: {
      required: true,
      prompt: () => prompt.text("GitHub OAuth client ID"),
    },
    GITHUB_CLIENT_SECRET: {
      required: true,
      secret: true,
      prompt: () => prompt.secret("GitHub OAuth client secret"),
    },
    GITHUB_ALLOWED_ORG: {
      default: DEFAULTS.GITHUB_ALLOWED_ORG,
      parse: normalizeGithubOrgList,
      validate: (value) => {
        const result = validateGithubOrgList(value);
        return result === true ? undefined : result;
      },
      prompt: () =>
        prompt.text("GitHub org(s) allowed to sign in (comma-separated)", {
          default: DEFAULTS.GITHUB_ALLOWED_ORG,
          validate: validateGithubOrgList,
        }),
    },
  };
}

/**
 * Runs the guided interactive installer flow: subscription, resource group
 * (existing/new), location, resource names, GitHub OAuth credentials.
 * Returns a flags-shaped object suitable for feeding into resolveConfig() as
 * the highest-precedence source. Every collaborator is injectable for tests.
 */
export async function runInteractiveInstaller({ prompt = promptDefault, az = azDefault, log = logDefault } = {}) {
  const collected = {};

  log.banner("Agentweaver interactive installer", "Provision Azure infrastructure and deploy");

  // Show a live progress indicator around slow az discovery calls so the
  // installer never looks hung. Falls back to running the task directly when
  // the injected log has no withProgress (e.g. unit-test stubs).
  const withProgress = (label, task) =>
    typeof log.withProgress === "function" ? log.withProgress(label, task) : task();

  // --- Subscription ---------------------------------------------------------
  const [subscriptions, current] = await withProgress("Loading Azure subscriptions", () =>
    Promise.all([az.listSubscriptions().catch(() => []), az.showAccount().catch(() => null)]),
  );
  if (Array.isArray(subscriptions) && subscriptions.length > 0) {
    const currentId = current?.id;
    const ordered = [...subscriptions].sort((a, b) => (a.id === currentId ? -1 : b.id === currentId ? 1 : 0));
    const choices = ordered.map((s) => ({
      label: `${s.name}${s.id === currentId ? " (current default)" : ""}`,
      value: s,
    }));
    const chosen = await prompt.select("Select an Azure subscription", choices, { default: 0 });
    if (chosen.id !== currentId) {
      await az.setActiveSubscription(chosen.id);
    }
    collected.subscriptionId = chosen.id;
  } else {
    // az subscription discovery failed or returned none -- degrade to a
    // manual text prompt (defaulting to the current subscription, if known)
    // instead of aborting the whole installer.
    collected.subscriptionId = await prompt.text("Azure subscription ID (leave blank to use the current default)", {
      default: current?.id ?? "",
    });
  }

  // --- Resource group --------------------------------------------------------
  const groups = await withProgress("Loading resource groups", () => az.listResourceGroups().catch(() => []));
  const CREATE_NEW = Symbol("create-new-resource-group");
  const sortedGroups = [...groups].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
  const rgChoices = [
    { label: "Create new...", value: CREATE_NEW },
    ...sortedGroups.map((g) => ({ label: g.name, value: g.name })),
  ];
  const rgChoice = groups.length > 0 ? await prompt.select("Select a resource group", rgChoices, { default: 0 }) : CREATE_NEW;
  collected.RESOURCE_GROUP =
    rgChoice === CREATE_NEW ? await prompt.text("New resource group name", { default: DEFAULTS.RESOURCE_GROUP }) : rgChoice;

  // --- Location ---------------------------------------------------------
  const locations = await withProgress("Loading Azure regions", () => az.listLocations().catch(() => []));
  if (Array.isArray(locations) && locations.length > 0) {
    const sortedLocations = [...locations].sort((a, b) =>
      (a.displayName || a.name).localeCompare(b.displayName || b.name, undefined, { sensitivity: "base" }),
    );
    const names = sortedLocations.map((l) => l.name);
    const defaultIndex = names.indexOf(DEFAULTS.LOCATION);
    const choices = sortedLocations.map((l) => ({ label: l.displayName || l.name, value: l.name }));
    collected.LOCATION = await prompt.select("Select a location", choices, {
      default: defaultIndex >= 0 ? defaultIndex : 0,
    });
  } else {
    collected.LOCATION = await prompt.text("Location", { default: DEFAULTS.LOCATION });
  }

  // --- Resource names (prefilled, editable) ---------------------------------
  collected.CLUSTER_NAME = await prompt.text("AKS cluster name", { default: DEFAULTS.CLUSTER_NAME });
  collected.ACR_NAME = await prompt.text("ACR name", { default: DEFAULTS.ACR_NAME });
  collected.KEYVAULT_NAME = await prompt.text("Key Vault name", { default: PROVISION_KEYVAULT_NAME_SUGGESTION });
  collected.PG_SERVER_NAME = await prompt.text("Postgres server name", {
    default: DEFAULTS.PG_SERVER_NAME,
    validate: validatePostgresServerName,
  });

  // --- GitHub OAuth credentials ---------------------------------------------
  log.info("");
  log.section("Create a GitHub OAuth App");
  log.info("You need a GitHub OAuth App's client ID and secret. GitHub requires a callback URL up front,");
  log.info("but this deployment's Gateway host does not exist yet -- so create the app now with a temporary");
  log.info("placeholder callback URL, then update it once the real URL is printed at the end of this deploy.");
  log.info("");
  log.info("  1. Open https://github.com/settings/applications/new");
  log.info("  2. Application name: e.g. 'Agentweaver' (or 'Agentweaver (staging)')");
  log.info("  3. Homepage URL: any placeholder for now (e.g. https://example.com) -- update it after deploy.");
  log.info("  4. Authorization callback URL:");
  log.info("       - Local dev: use the real value now -- http://localhost:5000/auth/github/callback");
  log.info("       - Azure: GitHub won't accept an empty field, so enter a placeholder for now, e.g.");
  log.info("         https://placeholder.invalid/auth/github/callback -- you'll replace it after deploy.");
  log.info("  5. Click 'Register application'. Copy the Client ID, then click 'Generate a new client secret'");
  log.info("     and copy it immediately -- GitHub only shows the secret once.");
  log.info("");
  log.info("After this deploy finishes, the real callback URL is printed as 'GitHub OAuth callback URL' in the");
  log.info("OUTPUTS SUMMARY. Go back to the OAuth App at https://github.com/settings/developers and set both the");
  log.info("Homepage URL and the Authorization callback URL to that value -- sign-in will not work until you do.");
  log.info("");
  log.info("Note: sign-in is further restricted to members of the GitHub org(s) you allowlist next -- org SSO");
  log.info("authorization may need to be granted on the OAuth App for private membership to be visible.");
  log.info("");
  collected.GITHUB_CLIENT_ID = await prompt.text("GitHub OAuth client ID");
  const clientSecret = await prompt.secret("GitHub OAuth client secret");
  registerSecret(clientSecret, "GITHUB_CLIENT_SECRET"); // redact immediately, before it is stored anywhere
  collected.GITHUB_CLIENT_SECRET = clientSecret;

  // --- GitHub org allowlist ---------------------------------------------------
  const allowedOrgs = await prompt.text("GitHub org(s) allowed to sign in (comma-separated)", {
    default: DEFAULTS.GITHUB_ALLOWED_ORG,
    validate: validateGithubOrgList,
  });
  collected.GITHUB_ALLOWED_ORG = normalizeGithubOrgList(allowedOrgs);

  return collected;
}

/** True only when `provision-infra` was invoked with zero CLI arguments and a TTY is available. */
export function shouldRunInteractiveInstaller(argv, { prompt = promptDefault } = {}) {
  return argv.length === 0 && prompt.isInteractive();
}

/**
 * Main entry point for the `provision-infra` subcommand.
 *
 * @param {object} [opts]
 * @param {string[]} [opts.argv] Raw CLI args following `provision-infra` (defaults to none).
 * @param {Record<string,string>} [opts.env] Defaults to process.env.
 * @param {string} [opts.repoRoot] Defaults to DEFAULT_REPO_ROOT.
 * @param {typeof promptDefault} [opts.prompt]
 * @param {typeof azDefault} [opts.az]
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {typeof resolveVariables} [opts.resolveVariables]
 * @param {object} [opts.steps] Injectable step modules for testing (createCluster, setupIdentity, provisionMonitoring, oauthSigningKey, provisionPostgres, buildImages, verifyProvenance, genA2aMtlsCerts, deployStep, verifyStep).
 */
export async function run(opts = {}) {
  const {
    argv = [],
    env = process.env,
    repoRoot = DEFAULT_REPO_ROOT,
    prompt = promptDefault,
    az = azDefault,
    exec = execDefault,
    log = logDefault,
    resolveVariables: resolveVariablesFn = resolveVariables,
    steps = {},
  } = opts;

  const createCluster = steps.createCluster ?? createClusterDefault;
  const setupIdentity = steps.setupIdentity ?? setupIdentityDefault;
  const provisionMonitoring = steps.provisionMonitoring ?? provisionMonitoringDefault;
  const oauthSigningKey = steps.oauthSigningKey ?? oauthSigningKeyDefault;
  const provisionPostgres = steps.provisionPostgres ?? provisionPostgresDefault;
  const buildImages = steps.buildImages ?? buildImagesDefault;
  const verifyProvenance = steps.verifyProvenance ?? verifyProvenanceDefault;
  const genA2aMtlsCerts = steps.genA2aMtlsCerts ?? genA2aMtlsCertsDefault;
  const deployStep = steps.deployStep ?? deployStepDefault;
  const verifyStep = steps.verifyStep ?? verifyStepDefault;

  const { flags, paramsFile: paramsFilePath, help } = parseArgs(argv);

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  const paramsFile = loadParamsFile(paramsFilePath);

  if (shouldRunInteractiveInstaller(argv, { prompt })) {
    const collected = await runInteractiveInstaller({ prompt, az, log });
    Object.assign(flags, collected);
  }

  const githubRepo = await resolveGitHubRepository({ repoRoot, exec }).catch(() => null);
  const ghcrOwner = githubRepo?.owner ?? "";
  const ghcrRepository = githubRepo?.repo ?? "";
  const schema = buildSchema({ prompt, az });
  const config = await resolveConfig(schema, { flags, env, paramsFile });
  if (config.IMAGE_SOURCE === "ghcr" && (!ghcrOwner || !ghcrRepository)) {
    throw new Error("IMAGE_SOURCE=ghcr requires a GitHub origin remote so the GHCR owner/repository can be derived automatically.");
  }

  log.info("");
  log.section("Resolved deploy configuration");
  log.field("Resource Group", config.RESOURCE_GROUP);
  log.field("Cluster", config.CLUSTER_NAME);
  log.field("ACR", config.ACR_NAME);
  log.field("Location", config.LOCATION);
  log.field("Key Vault", config.KEYVAULT_NAME);
  log.field("Postgres server", config.PG_SERVER_NAME);
  log.field("Namespace", config.NAMESPACE);
  log.field("Image source", config.IMAGE_SOURCE);
  if (config.IMAGE_SOURCE === "ghcr") {
    log.field("GHCR owner", ghcrOwner);
    log.field("GHCR ref", config.GHCR_REF);
  }
  log.field("GitHub OAuth client ID", config.GITHUB_CLIENT_ID);
  log.field("Allowed GitHub org(s)", config.GITHUB_ALLOWED_ORG);

  const envOverride = {
    RESOURCE_GROUP: config.RESOURCE_GROUP,
    CLUSTER_NAME: config.CLUSTER_NAME,
    ACR_NAME: config.ACR_NAME,
    LOCATION: config.LOCATION,
    KEYVAULT_NAME: config.KEYVAULT_NAME,
    PG_SERVER_NAME: config.PG_SERVER_NAME,
    NAMESPACE: config.NAMESPACE,
    GITHUB_ALLOWED_ORG: config.GITHUB_ALLOWED_ORG,
  };
  if (flags.IMAGE_TAG) envOverride.IMAGE_TAG = flags.IMAGE_TAG;

  const resolveCfg = () => resolveVariablesFn({ env: { ...env, ...envOverride }, repoRoot });
  let cfg = await (typeof log.withProgress === "function"
    ? log.withProgress("Resolving deploy configuration", resolveCfg)
    : resolveCfg());
  cfg = {
    ...cfg,
    IMAGE_SOURCE: config.IMAGE_SOURCE,
    GHCR_REF: config.GHCR_REF,
    GHCR_OWNER: ghcrOwner,
    GHCR_REPOSITORY: ghcrRepository,
    GHCR_TOKEN: config.GHCR_TOKEN,
    FORCE: Boolean(flags.FORCE),
    GITHUB_CLIENT_ID: config.GITHUB_CLIENT_ID,
    GITHUB_CLIENT_SECRET: config.GITHUB_CLIENT_SECRET,
    repoRoot,
  };

  log.step(1, 10, "Creating cluster (ACR + AKS)");
  await createCluster.run(cfg, { exec, log });

  log.step(2, 10, "Setting up identity");
  await setupIdentity.run(cfg, { exec, log, az, prompt });

  // Re-resolve variables so IDENTITY_CLIENT_ID (populated live by az after
  // 15-setup-identity provisions the managed identity) is picked up before
  // any later step needs it -- mirrors install.sh's explicit `az identity
  // show` capture immediately after 15-setup-identity.sh.
  cfg = await resolveVariablesFn({ env: { ...env, ...envOverride }, repoRoot });
  cfg = {
    ...cfg,
    IMAGE_SOURCE: config.IMAGE_SOURCE,
    GHCR_REF: config.GHCR_REF,
    GHCR_OWNER: ghcrOwner,
    GHCR_REPOSITORY: ghcrRepository,
    GHCR_TOKEN: config.GHCR_TOKEN,
    FORCE: Boolean(flags.FORCE),
    GITHUB_CLIENT_ID: config.GITHUB_CLIENT_ID,
    GITHUB_CLIENT_SECRET: config.GITHUB_CLIENT_SECRET,
    repoRoot,
  };

  log.step(3, 10, "Provisioning monitoring");
  await provisionMonitoring.run(cfg, { exec, log });

  if (!flags.SKIP_OAUTH_KEY) {
    log.step(4, 10, "Provisioning MCP OAuth signing key");
    await oauthSigningKey.run(cfg, { exec, log, repoRoot });
  } else {
    log.skip("Skipping 16-provision-oauth-signing-key (--skip-oauth-key)");
  }

  if (!flags.SKIP_POSTGRES) {
    log.step(5, 10, "Provisioning Postgres");
    await provisionPostgres.run(cfg, { exec, log, repoRoot });
  } else {
    log.skip("Skipping 17-provision-postgres (--skip-postgres)");
  }

  log.step(6, 10, "Building and pushing images");
  const buildResult = await buildImages.run(cfg, { exec });
  cfg = {
    ...cfg,
    EXPECTED_IMAGE_DIGESTS: buildResult.expectedImageDigests ?? undefined,
    IMPORTED_IMAGE_SOURCES: buildResult.importedImageSources ?? undefined,
  };

  log.step(7, 10, "Ensuring A2A mTLS certificates");
  await genA2aMtlsCerts.run(cfg, { exec, log, repoRoot });

  log.step(8, 10, "Deploying manifests");
  const deployResult = await deployStep.run(cfg, { run: exec.run, capture: exec.capture, log, repoRoot });

  // Provenance verification is a POST-DEPLOY safety net: it inspects the image
  // digests ACTUALLY running in the cluster, so it must run AFTER the deploy
  // above. Running it before deploy compares against still-old (or, on a first
  // provision, non-existent) pods -- the latter fails hard with "could not
  // determine desired replica count". This mirrors deploy-from-local's
  // build -> deploy -> verify-provenance order.
  log.step(9, 10, "Verifying image provenance");
  const provenanceResult = await verifyProvenance.run(cfg, { exec });

  log.step(10, 10, "Verifying deployment");
  const verifyResult = await verifyStep.run(cfg, { exec, log });

  log.info("");
  log.section("OUTPUTS SUMMARY");
  log.field("Resource Group", cfg.RESOURCE_GROUP);
  log.field("Cluster", cfg.CLUSTER_NAME);
  log.field("ACR", cfg.ACR_LOGIN_SERVER);
  log.field("Namespace", cfg.NAMESPACE);
  log.field("Postgres server", cfg.PG_SERVER_NAME);
  log.field("Image tag", cfg.IMAGE_TAG);
  log.field("AgentHost image tag", cfg.AGENTHOST_IMAGE_TAG);
  log.field("Image source", cfg.IMAGE_SOURCE);
  if (cfg.IMAGE_SOURCE === "ghcr") {
    log.field("GHCR ref", cfg.GHCR_REF);
  }
  log.field("Allowed GitHub org(s)", cfg.GITHUB_ALLOWED_ORG);
  log.field("Gateway host", deployResult?.HOST ?? "<unknown>");
  log.field("Gateway IP", deployResult?.GATEWAY_IP ?? "<unknown>");
  log.field(
    "GitHub OAuth callback URL",
    deployResult?.HOST ? `https://${deployResult.HOST}/auth/github/callback` : "<unknown -- see Gateway host above>",
  );
  if (deployResult?.HOST) {
    log.info(
      `  -> Update the GitHub OAuth App's Homepage URL and Authorization callback URL to the value above at https://github.com/settings/developers`,
    );
  }
  log.field("Verification", `${verifyResult.pass}/${verifyResult.pass + verifyResult.fail} checks passed`);
  // NEVER print GITHUB_CLIENT_SECRET or any credential value here.

  log.info("");
  if (verifyResult.ok) {
    log.banner("Deployment complete", deployResult?.HOST ? `https://${deployResult.HOST}` : "Environment provisioned");
  } else {
    log.banner("Deployment finished with failing checks", "Review the verification results above");
  }

  return {
    ok: verifyResult.ok,
    cfg,
    build: buildResult,
    provenance: provenanceResult,
    deploy: deployResult,
    verify: verifyResult,
  };
}
