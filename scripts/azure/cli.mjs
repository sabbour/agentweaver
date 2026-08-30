#!/usr/bin/env node
// cli.mjs -- Single Node entry point for the Agentweaver Azure toolchain.
// Routes the repository's local, infrastructure, and release deployment
// commands to their respective modules.

import { pathToFileURL, fileURLToPath } from "node:url";
import { existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { userInfo } from "node:os";
import * as logDefault from "./lib/log.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));

/**
 * Auto-discovers a user-specific params file (`params.<username>.json` or
 * `params.<username>.jsonc`) in the same directory as cli.mjs, and returns its
 * path if found. Returns null if no matching file exists.
 * This ensures that `deploy-from-local` and `verify` honour per-user config
 * (e.g. AUTH_MODE=Entra) without requiring a shell env variable every time.
 */
function findUserParamsFile() {
  const username = userInfo().username;
  for (const ext of [".json", ".jsonc"]) {
    const candidate = join(__dirname, `params.${username}${ext}`);
    if (existsSync(candidate)) return candidate;
  }
  return null;
}

/**
 * Merges a parsed params-file object into a base env, with `baseEnv` taking
 * precedence (explicit env vars always win over the params file).
 */
function mergeParamsIntoEnv(baseEnv, paramsFile) {
  if (!paramsFile || !Object.keys(paramsFile).length) return baseEnv;
  // params-file values fill gaps; explicit env vars override them
  return { ...paramsFile, ...baseEnv };
}

/**
 * Resolves the env a deploy subcommand should use: explicit `--params-file` flag
 * takes precedence over auto-discovered `params.<username>.json`; explicit process
 * env vars always win over params-file values (see mergeParamsIntoEnv). Shared by
 * every subcommand that deploys real infrastructure (deploy-from-local,
 * deploy-from-commit, deploy-from-release) so none of them silently fall back to
 * requiring every variable to be set by hand in the shell.
 */
async function resolveDeployEnv(rest, { importFn, modules, log }) {
  const { loadParamsFile } = modules.config ?? (await importFn("./lib/config.mjs"));
  const paramsFileIdx = rest.findIndex((a) => a === "--params-file" || a.startsWith("--params-file="));
  let paramsFilePath = null;
  if (paramsFileIdx !== -1) {
    paramsFilePath = rest[paramsFileIdx].includes("=")
      ? rest[paramsFileIdx].split("=").slice(1).join("=")
      : rest[paramsFileIdx + 1];
  } else {
    paramsFilePath = findUserParamsFile();
    if (paramsFilePath) log.info(`[params] Auto-loading ${paramsFilePath}`);
  }
  const paramsFile = loadParamsFile(paramsFilePath);
  return mergeParamsIntoEnv(process.env, paramsFile);
}

const SUBCOMMANDS = Object.freeze([
  "provision-infra",
  "setup-entra-app",
  "deploy-from-local",
  "deploy-from-commit",
  "deploy-from-release",
  "publish-release",
  "release",
  "verify",
  "dev",
]);

export const HELP_TEXT = `Agentweaver Azure toolchain

Usage:
  node scripts/azure/cli.mjs <command> [args...]

Commands:
  provision-infra      Provision/reconcile Azure infrastructure and perform its initial deployment.
  setup-entra-app      Create/reconcile the single-tenant Entra app registration used by Agentweaver.
  deploy-from-local    Deploy current local HEAD using a SHA image identifier.
  deploy-from-commit   Deploy an arbitrary exact commit without switching the caller's checkout.
  deploy-from-release  Deploy an existing published vX.Y.Z release.
  publish-release      Tag and publish a prepared exact-main release without deploying.
  release              Publish, then deploy, a prepared exact-main release.
  verify               Post-deploy health verification.
  dev                  Local dev orchestration.

Run 'node scripts/azure/cli.mjs <command> --help' for command-specific options.
`;

/**
 * Routes a parsed command + remaining argv to the matching module's run().
 * Modules are lazily imported (dynamic import) so `cli.mjs --help` and
 * unknown-command errors do not pay the cost of loading every module, and so
 * tests can inject fakes without triggering real module side effects.
 *
 * @param {string[]} argv Full argv following the script name (e.g. process.argv.slice(2)).
 * @param {object} [opts]
 * @param {typeof logDefault} [opts.log]
 * @param {Record<string, {run: (opts: object) => Promise<unknown>}>} [opts.modules] Injectable command modules for testing, keyed by subcommand name.
 * @param {(specifier: string) => Promise<unknown>} [opts.importFn] Injectable dynamic import, for testing.
 */
export async function run(argv = [], opts = {}) {
  const { log = logDefault, modules = {}, importFn = (specifier) => import(specifier) } = opts;

  const [command, ...rest] = argv;

  if (!command || command === "-h" || command === "--help" || command === "help") {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }

  if (!SUBCOMMANDS.includes(command)) {
    log.error(`Unknown command: '${command}'.`);
    log.info(HELP_TEXT);
    throw new Error(`Unknown command: '${command}'. Valid commands: ${SUBCOMMANDS.join(", ")}.`);
  }

  const moduleForCommand = async () => {
    if (modules[command]) return modules[command];
    switch (command) {
      case "provision-infra":
        return importFn("./provision-infra.mjs");
      case "setup-entra-app":
        return importFn("./setup-entra-app.mjs");
      case "deploy-from-local":
        return importFn("./deploy-from-local.mjs");
      case "deploy-from-commit":
        return importFn("./deploy-from-commit.mjs");
      case "deploy-from-release":
        return importFn("./deploy-from-release.mjs");
      case "publish-release":
        return importFn("./release-publish.mjs");
      case "release":
        return importFn("./release.mjs");
      case "verify":
        return importFn("./steps/40-verify.mjs");
      case "dev":
        return importFn("./dev.mjs");
      default:
        throw new Error(`Unknown command: '${command}'.`);
    }
  };

  const mod = await moduleForCommand();

  if (command === "verify") {
    // steps/40-verify.mjs's run(cfg, opts) takes a resolved config, not argv
    // -- resolve variables here so `cli.mjs verify` works standalone.
    if (rest.includes("-h") || rest.includes("--help")) {
      log.info(
        mod.HELP_TEXT ??
          "verify -- Post-deploy health verification (port of 40-verify.sh/.ps1)\n\n" +
            "Usage:\n  node scripts/azure/cli.mjs verify\n",
      );
      return { ok: true, help: true };
    }
    const { resolveVariables } = modules.variables ?? (await importFn("./variables.mjs"));
    const cfg = await resolveVariables();
    return mod.run(cfg, { log });
  }

  if (command === "deploy-from-local") {
    if (rest.includes("-h") || rest.includes("--help")) {
      log.info(mod.HELP_TEXT ?? HELP_TEXT);
      return { ok: true, help: true };
    }
    const { resolveVariables } = modules.variables ?? (await importFn("./variables.mjs"));
    const env = await resolveDeployEnv(rest, { importFn, modules, log });
    const cfg = await resolveVariables({ env });
    const allowDirty = rest.includes("--allow-dirty");
    return mod.run(cfg, { log, allowDirty });
  }

  if (command === "deploy-from-commit" || command === "deploy-from-release") {
    if (rest.includes("-h") || rest.includes("--help")) {
      log.info(mod.HELP_TEXT ?? HELP_TEXT);
      return { ok: true, help: true };
    }
    // Same per-user params.<username>.json auto-load as deploy-from-local -- these
    // subcommands also deploy real infrastructure and previously required every
    // variable (e.g. KEYVAULT_NAME) to be set by hand in the shell.
    const env = await resolveDeployEnv(rest, { importFn, modules, log });
    return mod.run({ argv: rest, log, env });
  }

  return mod.run({ argv: rest, log });
}

/* c8 ignore start -- process.argv entry point, not exercised by unit tests */
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  run(process.argv.slice(2)).catch((err) => {
    // Expected failures (missing prereqs, bad args, etc.) should read like a
    // normal CLI error, not a Node stack trace. Full stack is still
    // available via DEBUG=1 / AGENTWEAVER_DEBUG=1, matching log.debug()'s
    // existing gating convention.
    const showStack = Boolean(process.env.DEBUG || process.env.AGENTWEAVER_DEBUG);
    logDefault.error((showStack && err?.stack) || err?.message || String(err));
    process.exitCode = 1;
  });
}
/* c8 ignore stop */
