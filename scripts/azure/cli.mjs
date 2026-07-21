#!/usr/bin/env node
// cli.mjs -- Single Node entry point for the Agentweaver Azure toolchain.
// Routes the repository's local, infrastructure, and release deployment
// commands to their respective modules.

import { pathToFileURL } from "node:url";
import * as logDefault from "./lib/log.mjs";

const SUBCOMMANDS = Object.freeze([
  "provision-infra",
  "deploy-from-local",
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
  deploy-from-local    Deploy current local HEAD using a SHA image identifier.
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
      case "deploy-from-local":
        return importFn("./deploy-from-local.mjs");
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
    const cfg = await resolveVariables();
    const allowDirty = rest.includes("--allow-dirty");
    return mod.run(cfg, { log, allowDirty });
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
