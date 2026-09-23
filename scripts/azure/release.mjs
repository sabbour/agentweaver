// release.mjs -- Publish and deploy a prepared Agentweaver release.
//
// This is the first-shipment convenience orchestration. Durable repository
// identity belongs to release-publish.mjs; deployment of an existing release
// belongs to deploy-from-release.mjs.

import * as logDefault from "./lib/log.mjs";
import * as publishDefault from "./release-publish.mjs";
import * as deployFromReleaseDefault from "./deploy-from-release.mjs";

export function parseArgs(argv = []) {
  let resumeTag;
  let dryRun = false;
  let help = false;
  let featureManifestPath;
  let acceptanceBundlePath;

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--dry-run") {
      dryRun = true;
    } else if (["-h", "--help", "help"].includes(arg)) {
      help = true;
    } else if (arg === "--resume") {
      resumeTag = argv[index + 1];
      if (!resumeTag || resumeTag.startsWith("-")) {
        throw new Error("Missing release tag after --resume.");
      }
      index += 1;
    } else if (arg === "--feature-manifest") {
      featureManifestPath = argv[index + 1];
      if (!featureManifestPath || featureManifestPath.startsWith("-")) {
        throw new Error("Missing path after --feature-manifest.");
      }
      index += 1;
    } else if (arg === "--acceptance-bundle") {
      acceptanceBundlePath = argv[index + 1];
      if (!acceptanceBundlePath || acceptanceBundlePath.startsWith("-")) {
        throw new Error("Missing path after --acceptance-bundle.");
      }
      index += 1;
    } else {
      throw new Error(
        `Unknown argument: ${arg}. release accepts --dry-run, --resume vX.Y.Z, `
        + "--feature-manifest <path>, and --acceptance-bundle <path>.",
      );
    }
  }

  return { resumeTag, dryRun, help, featureManifestPath, acceptanceBundlePath };
}

export const HELP_TEXT = `release -- publish and deploy a prepared Agentweaver release

Usage:
  node scripts/azure/cli.mjs release --feature-manifest <path>
  node scripts/azure/cli.mjs release [--dry-run]
  node scripts/azure/cli.mjs release --resume vX.Y.Z \
    --feature-manifest <path> --acceptance-bundle <path>

Composes publish-release followed by deploy-from-release. Publication creates
the annotated tag and GitHub Release; deployment builds or retags that exact
release, deploys it, and verifies the running environment. A non-dry-run release
only completes after the post-deployment acceptance manifests pass the fail-closed
release acceptance gate.
`;

export async function run(opts = {}) {
  const {
    argv = [],
    log = logDefault,
    publish = publishDefault,
    deployFromRelease = deployFromReleaseDefault,
  } = opts;
  const { resumeTag, dryRun, help, featureManifestPath, acceptanceBundlePath } = parseArgs(argv);

  if (help) {
    log.info(HELP_TEXT);
    return { ok: true, help: true };
  }
  if (!dryRun && !featureManifestPath) {
    throw new Error(
      "Release deployment requires --feature-manifest <path> before publication and deployment.",
    );
  }
  const publishArgs = [];
  if (resumeTag) publishArgs.push("--resume", resumeTag);
  if (dryRun) publishArgs.push("--dry-run");
  const published = await publish.run({ ...opts, argv: publishArgs, log });

  const deployArgs = [published.tag];
  if (dryRun) deployArgs.push("--dry-run");
  if (featureManifestPath) deployArgs.push("--feature-manifest", featureManifestPath);
  if (acceptanceBundlePath) deployArgs.push("--acceptance-bundle", acceptanceBundlePath);
  const deployed = await deployFromRelease.run({
    ...opts,
    argv: deployArgs,
    log,
    validatedRelease: {
      tag: published.tag,
      version: published.version,
      commit: published.commit,
    },
  });

  return {
    ...deployed,
    published,
    deployed,
    acceptance: deployed.releaseAcceptance,
  };
}
