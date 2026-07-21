// upgrade.mjs -- The Phase 4 core deliverable: build a NEW immutable image
// tag, redeploy, and cycle the AgentHost warm pool by REAPPLYING the
// SandboxTemplate/SandboxWarmPool and WAITING for readiness -- never by
// deleting pods.
//
// BINDING SEMANTICS (per the "Full Node port of deploy toolchain" decision
// log's rubber-duck corrections -- these override the naive "just reuse
// VERSION's tag" plan):
//
//   1. Mints a NEW immutable tag: defaults to the current HEAD short git SHA
//      (via lib/git.mjs's currentGitSha()). NEVER reuses the VERSION-file-
//      derived semver tag -- that belongs to `release`, not `upgrade`.
//      Reusing it can no-op the ACR retag-vs-build decision and ship a stale
//      AgentHost image (this is the exact #251 failure class).
//   2. Refuses a dirty working tree by default (uncommitted changes present)
//      -- fails fast with a clear, actionable error. `allowDirty: true` is an
//      explicit dev/test opt-in escape hatch, never the default.
//   3. Delegates to steps/20-build-push-images.mjs's run(cfg) with the new
//      tag to build+push (IMAGE_TAG/AGENTHOST_IMAGE_TAG set to the new tag).
//   4. Delegates to steps/30-deploy.mjs's run(cfg) to redeploy (re-applies
//      SandboxTemplate + SandboxWarmPool; updateStrategy: Recreate means the
//      controller replaces stale warm pods once the template's pod spec
//      changes) -- BEFORE provenance verification (see step 5): steps/25 is
//      documented as a POST-DEPLOY safety net (it checks the digest actually
//      running live in the cluster), so it must run after the new image is
//      deployed, not before -- otherwise it always compares the still-old
//      live pods against the new target commit and reports a false STALE
//      IMAGE failure on every real upgrade (found + fixed during Phase 7
//      staging verification).
//   5. Delegates to steps/25-verify-image-provenance.mjs's run(cfg) with
//      VERIFY_GIT_REF left UNSET (defaults to HEAD inside that module) --
//      never default it to IMAGE_TAG (the historical #251/30-deploy.sh bug).
//   6. Warm-pool cycle = reapply-and-wait, NEVER manual pod deletion: after
//      30-deploy's apply, polls `kubectl get sandboxwarmpool
//      agentweaver-agent-host -o json` until
//      status.readyReplicas == spec.replicas (timeout ~180s), then verifies
//      every warm pod (selector `app=agentweaver-sandbox,
//      app.kubernetes.io/component=agent-host` -- see image-spec.mjs's
//      agent-host `provenance.podSelector`, and k8s/sandbox-template-
//      agenthost.yaml's podTemplate labels + k8s/sandbox-warmpool-
//      agenthost.yaml) is running the expected image digest/tag. This
//      function NEVER calls `kubectl delete pod`.
//   7. Prints a clear summary (image tag, digests, warm-pool status). Never
//      logs secrets (every value logged here is a tag/digest/count, not a
//      credential).

import * as execDefault from "./lib/exec.mjs";
import * as logDefault from "./lib/log.mjs";
import * as gitDefault from "./lib/git.mjs";
import * as kubectlDefault from "./lib/kubectl.mjs";
import * as buildStepDefault from "./steps/20-build-push-images.mjs";
import * as provenanceStepDefault from "./steps/25-verify-image-provenance.mjs";
import * as deployStepDefault from "./steps/30-deploy.mjs";
import { imageDigestFromId } from "./steps/25-verify-image-provenance.mjs";
import { validateImageTag } from "./variables.mjs";

const AGENTHOST_IMAGE_NAME = "agentweaver-agent-host";

export const HELP_TEXT = `dev/test-to-Azure shipping -- build, redeploy, and cycle the AgentHost warm pool

Primary package command:
  npm run azure:devtest [-- --allow-dirty]

Compatibility alias:
  npm run azure:upgrade [-- --allow-dirty]

Direct CLI usage:
  node scripts/azure/cli.mjs upgrade [--allow-dirty]

Ships current local work to an existing Azure dev/test environment without a
PR. A clean HEAD is required by default; --allow-dirty explicitly permits
uncommitted dev/test work. This does not version, tag, or publish a release:
use azure:deploy for provisioning and azure:release for a real versioned
release from merged main.

Flags:
  --allow-dirty   Dev/test escape hatch: skip the dirty-working-tree check.
                  Do not use for a shared/production environment.
`;

export const WARM_POOL_NAME = "agentweaver-agent-host";
export const WARM_POOL_POD_SELECTOR = "app=agentweaver-sandbox,app.kubernetes.io/component=agent-host";
export const WARM_POOL_WAIT_TIMEOUT_MS = 180_000;
export const WARM_POOL_POLL_INTERVAL_MS = 3_000;

export class DirtyWorkingTreeError extends Error {}

/** True if `git status --porcelain` reports any uncommitted changes (staged, unstaged, or untracked). */
export async function isWorkingTreeDirty({ cwd, capture } = {}) {
  const { stdout } = await capture("git", ["status", "--porcelain"], { cwd });
  return stdout.trim().length > 0;
}

/**
 * Mints the new immutable upgrade tag: the current HEAD short git SHA.
 * Never derives from the VERSION file (that is `release`'s tag, not
 * `upgrade`'s) -- see the module header's binding decision #1.
 */
export async function mintUpgradeTag({ cwd, git = gitDefault } = {}) {
  const { short } = await git.currentGitSha({ cwd });
  if (!short) {
    throw new Error("Could not resolve the current HEAD git SHA to mint an upgrade image tag.");
  }
  validateImageTag(short, "IMAGE_TAG");
  return short;
}

/**
 * Fetches the SandboxWarmPool's current status via
 * `kubectl get sandboxwarmpool <name> -o json`. Returns
 * `{ found, readyReplicas, replicas, raw }`; `found: false` if the resource
 * or CRD does not exist (never throws for that case -- callers decide how to
 * treat an absent pool).
 */
export async function getWarmPoolStatus(namespace, { exec = execDefault } = {}) {
  const result = await exec.capture(
    "kubectl",
    ["get", "sandboxwarmpool", WARM_POOL_NAME, "--namespace", namespace, "-o", "json"],
    { allowFailure: true, json: true },
  );
  if (result.code !== 0 || !result.json) {
    return { found: false, readyReplicas: 0, replicas: 0, raw: null };
  }
  const spec = result.json.spec || {};
  const status = result.json.status || {};
  return {
    found: true,
    readyReplicas: Number(status.readyReplicas || 0),
    replicas: Number(spec.replicas ?? 0),
    raw: result.json,
  };
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Polls the SandboxWarmPool until `status.readyReplicas == spec.replicas`,
 * or throws on timeout. THIS IS THE ONLY warm-pool-cycle mechanism: reapply
 * (already done by steps/30-deploy.mjs before this is called) then wait.
 * Never deletes pods -- the SandboxWarmPool controller (updateStrategy:
 * Recreate) is solely responsible for replacing stale warm pods once the
 * referenced SandboxTemplate's pod spec changes.
 *
 * @param {string} namespace
 * @param {object} [opts]
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {number} [opts.timeoutMs] Defaults to WARM_POOL_WAIT_TIMEOUT_MS (~180s).
 * @param {number} [opts.pollIntervalMs] Defaults to WARM_POOL_POLL_INTERVAL_MS (3s).
 * @param {(ms: number) => Promise<void>} [opts.sleep] Injectable for tests.
 */
export async function waitForWarmPoolReady(namespace, opts = {}) {
  const {
    exec = execDefault,
    log = logDefault,
    timeoutMs = WARM_POOL_WAIT_TIMEOUT_MS,
    pollIntervalMs = WARM_POOL_POLL_INTERVAL_MS,
    sleep = defaultSleep,
  } = opts;

  const deadline = Date.now() + timeoutMs;
  let lastStatus = null;
  while (Date.now() <= deadline) {
    const status = await getWarmPoolStatus(namespace, { exec });
    lastStatus = status;
    if (!status.found) {
      log.skip(`SandboxWarmPool '${WARM_POOL_NAME}' not found in namespace '${namespace}' -- agent-sandbox CRD likely not installed; skipping warm-pool wait.`);
      return { ...status, ready: true, skipped: true };
    }
    log.info(`  SandboxWarmPool '${WARM_POOL_NAME}': ${status.readyReplicas}/${status.replicas} ready`);
    if (status.replicas > 0 && status.readyReplicas >= status.replicas) {
      return { ...status, ready: true, skipped: false };
    }
    await sleep(pollIntervalMs);
  }
  throw new Error(
    `Timed out after ${timeoutMs}ms waiting for SandboxWarmPool '${WARM_POOL_NAME}' to become ready ` +
      `(last observed: ${lastStatus?.readyReplicas ?? 0}/${lastStatus?.replicas ?? 0}). ` +
      "Refusing to manually delete pods -- inspect `kubectl describe sandboxwarmpool " +
      `${WARM_POOL_NAME} -n ${namespace}\` and the referenced SandboxTemplate for the root cause.`,
  );
}

/**
 * Resolves the manifest digest ACR has for `image:tag`. Returns null (never
 * throws) if the tag doesn't exist yet or `az` fails -- callers fall back to
 * tag-string comparison in that case.
 */
export async function resolveAcrDigestForTag(acrName, image, tag, { exec = execDefault } = {}) {
  try {
    const { stdout } = await exec.capture(
      "az",
      ["acr", "repository", "show", "--name", acrName, "--image", `${image}:${tag}`, "--query", "digest", "--output", "tsv"],
      { allowFailure: true },
    );
    const digest = stdout.trim();
    return digest ? `sha256:${digest.replace(/^sha256:/, "")}` : null;
  } catch {
    return null;
  }
}

/**
 * Verifies every warm pod (selector WARM_POOL_POD_SELECTOR) runs the
 * expected AgentHost image, using lib/kubectl.mjs's podStatusForSelector().
 * Returns `{ ok, pods, mismatched }`.
 *
 * IMPORTANT (found in Phase 7 staging re-verification): compares by DIGEST,
 * not tag string. A retag-forward build (source unchanged -- e.g. this
 * commit didn't touch agent-host's watched paths) can legitimately push the
 * SAME manifest digest under a NEW tag string; warm pods that haven't
 * churned yet may still show the OLD tag string while running byte-identical
 * content. Comparing tag strings alone would falsely report these as
 * mismatched/stale and abort a correct upgrade. Only fall back to tag-string
 * comparison if the expected digest can't be resolved from ACR (e.g. offline
 * test doubles, or a transient ACR read failure) -- log clearly when that
 * happens since it's a weaker check.
 *
 * @param {string} namespace
 * @param {string} expectedTag The AgentHost tag just built/deployed.
 * @param {object} [opts]
 * @param {string} [opts.acrName] Required for digest-aware comparison; if
 *   omitted, falls back to tag-string comparison (with a warning).
 * @param {string} [opts.imageName] Defaults to AGENTHOST_IMAGE_NAME.
 */
export async function verifyWarmPoolImage(namespace, expectedTag, opts = {}) {
  const { kubectl = kubectlDefault, log = logDefault, exec = execDefault, acrName, imageName = AGENTHOST_IMAGE_NAME } = opts;
  const pods = await kubectl.podStatusForSelector(WARM_POOL_POD_SELECTOR, namespace);

  const expectedDigest = acrName ? await resolveAcrDigestForTag(acrName, imageName, expectedTag, { exec }) : null;
  if (acrName && !expectedDigest) {
    log.warn(
      `  could not resolve ACR digest for '${imageName}:${expectedTag}' -- falling back to weaker tag-string comparison for the warm-pool check.`,
    );
  }

  const mismatched = [];
  for (const pod of pods) {
    const tag = pod.imageRef && pod.imageRef.includes(":") ? pod.imageRef.slice(pod.imageRef.lastIndexOf(":") + 1) : "";
    if (expectedDigest) {
      const podDigest = imageDigestFromId(pod.imageId);
      if (podDigest !== expectedDigest) {
        mismatched.push({ name: pod.name, tag, digest: podDigest, imageRef: pod.imageRef });
      }
    } else if (tag !== expectedTag) {
      mismatched.push({ name: pod.name, tag, imageRef: pod.imageRef });
    }
  }
  if (mismatched.length > 0) {
    for (const m of mismatched) {
      const expectedDisplay = expectedDigest ? expectedDigest.slice(0, 19) : expectedTag;
      const actualDisplay = expectedDigest ? m.digest || "<unresolved digest>" : m.tag || "<unknown>";
      log.warn(`  warm pod ${m.name} runs ${actualDisplay} (${m.imageRef || "<no image>"}), expected ${expectedDisplay}`);
    }
  } else if (pods.length > 0) {
    log.ok(`All ${pods.length} warm pod(s) run the expected AgentHost image (${expectedDigest ? "digest-verified" : `tag '${expectedTag}'`}).`);
  } else {
    log.skip(`No warm pods found for selector '${WARM_POOL_POD_SELECTOR}' yet (pool may still be scheduling).`);
  }
  return { ok: mismatched.length === 0, pods, mismatched };
}

/**
 * Main entry point: mints a new immutable tag, builds+pushes images,
 * verifies provenance, redeploys, and cycles the warm pool
 * (reapply-and-wait; never manual pod deletion).
 *
 * @param {Record<string, unknown>} cfg Resolved variables.mjs output (RESOURCE_GROUP, NAMESPACE, ACR_NAME, ACR_LOGIN_SERVER, ...). IMAGE_TAG/AGENTHOST_IMAGE_TAG are OVERWRITTEN with the newly minted tag.
 * @param {object} [opts]
 * @param {boolean} [opts.allowDirty] Explicit dev/test escape hatch to skip the dirty-working-tree check. NOT the default.
 * @param {typeof execDefault} [opts.exec]
 * @param {typeof logDefault} [opts.log]
 * @param {typeof gitDefault} [opts.git]
 * @param {typeof kubectlDefault} [opts.kubectl]
 * @param {typeof buildStepDefault} [opts.buildStep]
 * @param {typeof provenanceStepDefault} [opts.provenanceStep]
 * @param {typeof deployStepDefault} [opts.deployStep]
 * @param {string} [opts.cwd] Repo root for git operations; defaults to cfg.repoRoot or process.cwd().
 */
export async function run(cfg, opts = {}) {
  const {
    allowDirty = false,
    exec = execDefault,
    log = logDefault,
    git = gitDefault,
    kubectl = kubectlDefault,
    buildStep = buildStepDefault,
    provenanceStep = provenanceStepDefault,
    deployStep = deployStepDefault,
    cwd = cfg.repoRoot,
  } = opts;

  log.section("Agentweaver upgrade: build + redeploy + warm-pool cycle");

  if (!allowDirty) {
    const dirty = await isWorkingTreeDirty({ cwd, capture: exec.capture });
    if (dirty) {
      throw new DirtyWorkingTreeError(
        "Refusing to upgrade from a dirty working tree (uncommitted changes present). " +
          "Commit or stash your changes, or pass --allow-dirty to explicitly opt out of this safety check " +
          "(dev/test escape hatch only -- do not use for a real staging upgrade).",
      );
    }
  } else {
    log.warn("--allow-dirty was passed: skipping the dirty-working-tree safety check. This is a dev/test escape hatch, not for real upgrades.");
  }

  const newTag = await mintUpgradeTag({ cwd, git });
  log.field("Minted upgrade tag", newTag);

  const upgradeCfg = {
    ...cfg,
    IMAGE_TAG: newTag,
    AGENTHOST_IMAGE_TAG: newTag,
    repoRoot: cwd,
  };

  log.info("");
  log.info("Step 1/4: Building + pushing images...");
  const buildResult = await buildStep.run(upgradeCfg, { exec, git, kubectl });

  log.info("");
  log.info("Step 2/4: Redeploying (re-applies SandboxTemplate + SandboxWarmPool)...");
  const deployResult = await deployStep.run(upgradeCfg, { run: exec.run, capture: exec.capture, log, repoRoot: cwd });

  log.info("");
  log.info("Step 3/4: Verifying image provenance...");
  // steps/25 is a POST-DEPLOY safety net (see its module header): it checks
  // the digest ACTUALLY running in the cluster right now, so it must run
  // after deploy -- running it before deploy compares the still-old live
  // pods against the new target commit and always reports false STALE
  // IMAGE failures. VERIFY_GIT_REF is deliberately left unset here so
  // steps/25 defaults it to HEAD -- never default it to IMAGE_TAG (see
  // module header, decision #4).
  const provenanceResult = await provenanceStep.run({ ...upgradeCfg, VERIFY_GIT_REF: undefined }, { exec, git, kubectl });

  log.info("");
  log.info("Step 4/4: Cycling the AgentHost warm pool (reapply-and-wait; no manual pod deletion)...");
  const warmPoolStatus = await waitForWarmPoolReady(upgradeCfg.NAMESPACE, { exec, log });
  const warmPoolImageCheck = warmPoolStatus.skipped
    ? { ok: true, pods: [], mismatched: [] }
    : await verifyWarmPoolImage(upgradeCfg.NAMESPACE, newTag, { kubectl, log, exec, acrName: upgradeCfg.ACR_NAME });

  log.info("");
  log.section("UPGRADE SUMMARY");
  log.field("Image tag", newTag);
  log.field("AgentHost tag", newTag);
  log.field("ACR", upgradeCfg.ACR_LOGIN_SERVER);
  log.field("Target commit", buildResult?.targetCommit ?? "<unknown>");
  log.field("Provenance", `${provenanceResult.results.filter((r) => r.status === "ok").length}/${provenanceResult.results.length} images verified`);
  log.field("Deployed host", deployResult?.HOST ?? "<unknown>");
  if (warmPoolStatus.skipped) {
    log.field("Warm pool", "skipped (SandboxWarmPool/CRD not present)");
  } else {
    log.field("Warm pool", `${warmPoolStatus.readyReplicas}/${warmPoolStatus.replicas} ready, image ${warmPoolImageCheck.ok ? "verified" : "MISMATCHED -- see warnings above"}`);
  }

  if (!warmPoolImageCheck.ok) {
    throw new Error(
      `Warm pool is ready but ${warmPoolImageCheck.mismatched.length} pod(s) do not run the expected AgentHost tag '${newTag}'. ` +
        "The SandboxWarmPool controller (updateStrategy: Recreate) should replace these automatically as they cycle -- " +
        "re-run the upgrade's warm-pool wait step if this persists, but do NOT manually delete these pods.",
    );
  }

  return {
    imageTag: newTag,
    targetCommit: buildResult?.targetCommit,
    plans: buildResult?.plans,
    provenance: provenanceResult,
    deploy: deployResult,
    warmPool: { ...warmPoolStatus, imageCheck: warmPoolImageCheck },
  };
}
