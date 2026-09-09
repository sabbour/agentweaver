#!/usr/bin/env node
// cli.mjs -- Single Node entry point for the Agentweaver Azure toolchain.
// Routes the repository's local, infrastructure, and release deployment
// commands to their respective modules.

import { pathToFileURL, fileURLToPath } from "node:url";
import { existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { userInfo } from "node:os";
import * as logDefault from "./lib/log.mjs";
import { stageRepoAppPrivateKeyFile } from "./lib/repo-app-secret.mjs";

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
 * Resolves the env and forwarded argv a deploy subcommand should use. An explicit
 * `--params-file` flag takes precedence over auto-discovered
 * `params.<username>.json`, and its flag/value tokens are removed before strict
 * subcommand parsers receive argv. Explicit process env vars always win over
 * params-file values (see mergeParamsIntoEnv).
 */
async function resolveDeployInputs(rest, { importFn, modules, log, findParamsFile = findUserParamsFile }) {
  const { loadParamsFile } = modules.config ?? (await importFn("./lib/config.mjs"));
  let paramsFilePath = null;
  let recoverRepoAppPrivateKey = false;
  const argv = [];
  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i];
    if (arg === "--params-file" || arg.startsWith("--params-file=")) {
      const inline = arg.startsWith("--params-file=");
      paramsFilePath = inline ? arg.slice("--params-file=".length) : rest[i + 1];
      if (!paramsFilePath) {
        throw new Error("--params-file requires a value");
      }
      if (!inline) i += 1;
    } else if (arg === "--recover-repo-app-private-key") {
      recoverRepoAppPrivateKey = true;
    } else {
      argv.push(arg);
    }
  }
  if (!paramsFilePath) {
    paramsFilePath = findParamsFile();
    if (paramsFilePath) log.info(`[params] Auto-loading ${paramsFilePath}`);
  }
  const paramsFile = loadParamsFile(paramsFilePath);
  return {
    env: mergeParamsIntoEnv(process.env, paramsFile),
    argv,
    recoverRepoAppPrivateKey,
  };
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
  const {
    log = logDefault,
    modules = {},
    importFn = (specifier) => import(specifier),
    findParamsFile = findUserParamsFile,
  } = opts;

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
            "Usage:\n  node scripts/azure/cli.mjs verify [--params-file <path>]\n",
      );
      return { ok: true, help: true };
    }
    const { resolveVariables } = modules.variables ?? (await importFn("./variables.mjs"));
    const { env, recoverRepoAppPrivateKey } = await resolveDeployInputs(
      rest,
      { importFn, modules, log, findParamsFile },
    );
    if (recoverRepoAppPrivateKey) {
      throw new Error("--recover-repo-app-private-key is valid only for deployment commands.");
    }
    const cfg = await resolveVariables({ env });
    return mod.run(cfg, { log });
  }

  if (command === "deploy-from-local") {
    if (rest.includes("-h") || rest.includes("--help")) {
      log.info(mod.HELP_TEXT ?? HELP_TEXT);
      return { ok: true, help: true };
    }
    const { resolveVariables } = modules.variables ?? (await importFn("./variables.mjs"));
    const {
      env,
      argv: deployArgs,
      recoverRepoAppPrivateKey,
    } = await resolveDeployInputs(
      rest,
      { importFn, modules, log, findParamsFile },
    );
    const stagedRepoAppKey = stageRepoAppPrivateKeyFile(env.REPO_APP_PRIVATE_KEY_FILE);
    try {
      const cfg = {
        ...(await resolveVariables({
          env: {
            ...env,
            REPO_APP_PRIVATE_KEY_FILE: "",
          },
        })),
        REPO_APP_PRIVATE_KEY_STAGED_FILE: stagedRepoAppKey?.filePath ?? "",
        RECOVER_REPO_APP_PRIVATE_KEY: recoverRepoAppPrivateKey,
      };
      const allowDirty = deployArgs.includes("--allow-dirty");
      return await mod.run(cfg, { log, allowDirty });
    } finally {
      stagedRepoAppKey?.cleanup();
    }
  }

  if (command === "deploy-from-commit" || command === "deploy-from-release") {
    if (rest.includes("-h") || rest.includes("--help")) {
      log.info(mod.HELP_TEXT ?? HELP_TEXT);
      return { ok: true, help: true };
    }
    // Same per-user params.<username>.json auto-load as deploy-from-local -- these
    // subcommands also deploy real infrastructure and previously required every
    // variable (e.g. KEYVAULT_NAME) to be set by hand in the shell.
    const {
      env,
      argv: deployArgs,
      recoverRepoAppPrivateKey,
    } = await resolveDeployInputs(
      rest,
      { importFn, modules, log, findParamsFile },
    );
    return mod.run({
      argv: deployArgs,
      log,
      env,
      recoverRepoAppPrivateKey,
    });
  }

  return mod.run({ argv: rest, log });
}

/**
 * Executes the CLI command and maps an explicit unsuccessful command result
 * to a non-zero process exit code.
 */
export async function main(argv = process.argv.slice(2), opts = {}) {
  const {
    processImpl = process,
    log = logDefault,
    ...runOpts
  } = opts;

  try {
    const result = await run(argv, { ...runOpts, log });
    if (result?.ok === false) {
      processImpl.exitCode = 1;
    }
    return result;
  } catch (err) {
    // Expected failures (missing prereqs, bad args, etc.) should read like a
    // normal CLI error, not a Node stack trace. Full stack is still
    // available via DEBUG=1 / AGENTWEAVER_DEBUG=1, matching log.debug()'s
    // existing gating convention.
    const env = processImpl.env ?? process.env;
    const showStack = Boolean(env.DEBUG || env.AGENTWEAVER_DEBUG);
    log.error((showStack && err?.stack) || err?.message || String(err));
    processImpl.exitCode = 1;
    return undefined;
  }
}

/* c8 ignore start -- process.argv entry point */
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  void main();
}
/* c8 ignore stop */
