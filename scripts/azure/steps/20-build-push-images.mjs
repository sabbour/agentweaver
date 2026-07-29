// 20-build-push-images.mjs -- Node port of scripts/aks/20-build-push-images.sh
// (+ _image-functions.ps1). Builds/retags/provenance-stamps the 4 images
// described in ../image-spec.mjs using `az acr build` (NOT docker buildx /
// NOT multi-arch -- verified against source: the legacy script's build_image()
// calls plain `az acr build --file <dockerfile> .`, nothing multi-platform).
//
// Faithful port of:
//   - current_deployment_tag()/current_agenthost_tag()  -> lib/kubectl.mjs
//   - release_ref_for_tag() / Get-ReleaseRefForTag()      -> releaseRefForTag()
//   - paths_changed() / Test-PathsChanged()               -> lib/git.mjs diffIsQuiet()
//   - stamp_provenance() / Invoke-StampProvenance()        -> stampProvenance()
//   - build_image()/retag_image()/wait_for_acr_tag_digest()-> buildImage()/retagImage()/waitForAcrTagDigest()
//   - schedule_image()'s build-vs-retag decision           -> planImage()
//
// Bugfixes applied while porting (see image-spec.mjs header + decision log):
//   - watchedPaths now include 'VERSION', 'nuget.config' (case-fixed), and,
//     for the api image, 'apps/Agentweaver.Api.Data' +
//     'apps/Agentweaver.Api.Migrations.Postgres'.
//   - `az acr build` now receives `--build-arg IMAGE_TAG=... --build-arg
//     GIT_SHA=...` (previously missing entirely).
//
// Known simplification vs the legacy bash/PowerShell scripts: those use
// background jobs (`&` / Start-Job) and kill siblings the instant one job
// fails ("terminate_remaining_jobs"/"Wait-ForImageJobs"). lib/exec.mjs's
// run()/capture() do not expose a child-process handle to cancel an in-flight
// `az acr build`, so this port runs all 4 image jobs concurrently via
// Promise.allSettled and reports every failure, but cannot forcibly abort an
// already-started sibling `az acr build`. Overall pass/fail semantics (any
// job failing fails the whole step) are preserved.

import fs from "node:fs";
import path from "node:path";
import * as log from "../lib/log.mjs";
import * as execDefault from "../lib/exec.mjs";
import * as gitDefault from "../lib/git.mjs";
import * as kubectlDefault from "../lib/kubectl.mjs";
import { IMAGES, buildArgsFor } from "../image-spec.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

/**
 * Resolves a release image tag to the commit that wrote that version to
 * VERSION, mirroring release_ref_for_tag()/Get-ReleaseRefForTag(). Returns
 * null if the tag/commit cannot be safely resolved (shallow clone, diverged
 * VERSION history, or no match at all) -- callers must treat null as "force
 * rebuild", never as "assume unchanged".
 */
export async function releaseRefForTag(tag, { cwd, git = gitDefault } = {}) {
  const direct = await git.revParseCommit(tag, { cwd });
  if (direct) return direct;

  if (await git.isShallowRepository({ cwd })) {
    log.warn(`tag ${tag}: repository is shallow; refusing VERSION-based source resolution (forcing rebuild)`);
    return null;
  }

  const version = tag.startsWith("v") ? tag.slice(1) : tag;
  const candidates = await git.logAllCommitsForPath("VERSION", { cwd });
  const matches = [];
  for (const commit of candidates) {
    const content = await git.showFileAtCommit(commit, "VERSION", { cwd });
    if (content !== null && content.replace(/\s+/g, "") === version) {
      matches.push(commit);
    }
  }

  if (matches.length === 0) return null;

  // git log --all lists newest-first: matches[0] is the newest. Every other
  // VERSION-writing commit must be its ancestor, or VERSION history is
  // ambiguous/diverged and we must refuse to guess (see #251 hardening note
  // in the legacy scripts).
  const newest = matches[0];
  for (const candidate of matches.slice(1)) {
    if (!(await git.isAncestor(candidate, newest, { cwd }))) {
      log.warn(
        `tag ${tag}: multiple diverged commits wrote VERSION=${version}; refusing to guess source commit (forcing rebuild)`,
      );
      return null;
    }
  }
  return newest;
}

/** Resolves the previous image tag's source commit, or null. Mirrors source_commit_for_tag(). */
export async function sourceCommitForTag(tag, { cwd, git = gitDefault } = {}) {
  if (!tag) return null;
  return releaseRefForTag(tag, { cwd, git });
}

/** True if any of `paths` differ between `oldRef` and `newRef`. Mirrors paths_changed(). */
export async function pathsChanged(oldRef, newRef, paths, { cwd, git = gitDefault } = {}) {
  if (!oldRef || !newRef) return true;
  const quiet = await git.diffIsQuiet(oldRef, newRef, paths, { cwd });
  return !quiet;
}

/** Reads the tag currently live in the cluster for one image's currentTag descriptor. */
export async function currentTagFor(image, namespace, { kubectl = kubectlDefault } = {}) {
  if (image.currentTag.kind === "agenthost") {
    return kubectl.currentAgentHostTag(namespace);
  }
  return kubectl.currentDeploymentTag(image.currentTag.name, namespace);
}

const ACR_TAG_DIGEST_POLL_INITIAL_DELAY_MS = 2_000;
const ACR_TAG_DIGEST_POLL_MAX_DELAY_MS = 15_000;
const ACR_TAG_DIGEST_POLL_BUDGET_MS = 5 * 60_000;
const ACR_TAG_DIGEST_POLL_DELAYS_MS = Object.freeze(buildAcrTagDigestPollDelays());

function buildAcrTagDigestPollDelays() {
  const delays = [];
  for (let elapsed = 0, nextDelay = ACR_TAG_DIGEST_POLL_INITIAL_DELAY_MS; elapsed < ACR_TAG_DIGEST_POLL_BUDGET_MS; ) {
    const delay = Math.min(nextDelay, ACR_TAG_DIGEST_POLL_BUDGET_MS - elapsed);
    delays.push(delay);
    elapsed += delay;
    nextDelay = Math.min(nextDelay * 2, ACR_TAG_DIGEST_POLL_MAX_DELAY_MS);
  }
  return delays;
}

/**
 * Polls ACR for the digest a tag currently resolves to.
 *
 * The large backoff window is deliberate: under concurrent multi-image `az acr import`
 * load, ACR's `show-manifests` read path has lagged the successful write by minutes in
 * production, so a short 10s loop causes false "unstamped image" deploy failures.
 */
export async function waitForAcrTagDigest(image, tag, cfg, { exec = execDefault, sleep = defaultSleep } = {}) {
  const initialDigest = await acrDigestForTag(image, tag, cfg, { exec });
  if (initialDigest) return initialDigest;

  for (const delay of ACR_TAG_DIGEST_POLL_DELAYS_MS) {
    await sleep(delay);
    const digest = await acrDigestForTag(image, tag, cfg, { exec });
    if (digest) return digest;
  }
  return null;
}

function defaultSleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Looks up the manifest digest a single ACR tag currently resolves to, or null. */
export async function acrDigestForTag(image, tag, cfg, { exec = execDefault } = {}) {
  try {
    const { stdout } = await exec.capture(
      "az",
      [
        "acr",
        "repository",
        "show-manifests",
        "--name",
        cfg.ACR_NAME,
        "--repository",
        image,
        "--query",
        `[?tags[?@=='${tag}']].digest`,
        "--output",
        "tsv",
      ],
      { allowFailure: true },
    );
    const first = stdout
      .split(/\r?\n/)
      .map((l) => l.trim())
      .find(Boolean);
    return first || null;
  } catch {
    return null;
  }
}

/**
 * Stamps an extra immutable 'prov-<full-sha>' ACR tag pointing at the same
 * digest as `image:tag`, recording the commit its content actually
 * corresponds to. Mandatory: any failure here must fail the image job,
 * mirroring stamp_provenance()/Invoke-StampProvenance()'s "refusing to ship
 * unstamped image" behavior.
 */
export async function stampProvenance(image, tag, commit, cfg, { exec = execDefault, git = gitDefault } = {}) {
  if (!commit) {
    throw new Error(`no resolvable commit for ${image}:${tag}; refusing to ship unstamped image`);
  }
  const resolvedCommit = await git.revParseCommit(commit, { cwd: cfg.repoRoot });
  if (!resolvedCommit) {
    throw new Error(`provenance commit '${commit}' for ${image}:${tag} is not resolvable in local git history`);
  }
  const provTag = `prov-${resolvedCommit}`;
  log.info(`--- Stamping provenance ${image}:${tag} -> ${image}:${provTag} ---`);

  if (exec.isDryRun()) {
    log.info(`  [dry-run] Would run az acr import for ${image}:${tag} -> ${provTag}`);
    return { image, tag: provTag, commit: resolvedCommit, dryRun: true };
  }

  const sourceDigest = await waitForAcrTagDigest(image, tag, cfg, { exec });
  if (!sourceDigest) {
    throw new Error(`source image ${image}:${tag} never resolved to a digest in ACR; refusing to stamp unverifiable provenance`);
  }

  // Idempotency: if this exact commit's provenance tag already points at
  // this exact digest (e.g. a re-run of the same commit's build, or the
  // "retag, unchanged since last build" path re-stamping provenance for an
  // image that was never rebuilt), there is nothing to do. This matters
  // because the tag is locked read-only below on first stamp -- without
  // this early-out, a legitimate re-run would hit `az acr import --force`
  // against an already-locked tag and fail.
  const existingProvDigest = await acrDigestForTag(image, provTag, cfg, { exec });
  if (existingProvDigest === sourceDigest) {
    log.skip(`${image}:${provTag} already stamped and locked at the expected digest`);
    return { image, tag: provTag, commit: resolvedCommit };
  }

  await exec.capture("az", [
    "acr",
    "import",
    "--name",
    cfg.ACR_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--source",
    `${cfg.ACR_LOGIN_SERVER}/${image}@${sourceDigest}`,
    "--image",
    `${image}:${provTag}`,
    "--force",
    "--output",
    "none",
  ]);

  const stampedDigest = await waitForAcrTagDigest(image, provTag, cfg, { exec });
  if (!stampedDigest) {
    throw new Error(`provenance tag ${image}:${provTag} did not appear in ACR after import; refusing to ship unstamped image`);
  }
  if (stampedDigest !== sourceDigest) {
    throw new Error(
      `provenance tag ${image}:${provTag} resolved to ${stampedDigest}, expected ${sourceDigest}; refusing to ship mismatched provenance`,
    );
  }

  // Lock the provenance tag as read-only immediately after it's verified to
  // resolve to the expected digest. Without this, 'prov-<sha>' is just a
  // mutable ACR tag: anyone with registry write access (or a compromised
  // credential) could re-point it at a different, unreviewed digest later,
  // and 25-verify-image-provenance.mjs's tag-based check would have no way
  // to detect that. Locking makes the tag immutable going forward -- a
  // later `az acr import --force` against the *same* tag now fails loudly
  // instead of silently overwriting it, which is the desired behavior: a
  // given commit's provenance tag should only ever point at one digest.
  const lockResult = await exec.capture(
    "az",
    ["acr", "repository", "update", "--name", cfg.ACR_NAME, "--image", `${image}:${provTag}`, "--write-enabled", "false"],
    { allowFailure: true },
  );
  if (lockResult.code !== 0) {
    log.warn(`  could not lock provenance tag ${image}:${provTag} as read-only: ${lockResult.stderr}`);
  }

  log.ok(`${cfg.ACR_LOGIN_SERVER}/${image}:${provTag} (commit ${resolvedCommit})`);
  return { image, tag: provTag, commit: resolvedCommit };
}

/** Retags `image:sourceTag` to `image:targetTag` via `az acr import`. Mirrors retag_image(). */
export async function retagImage(image, sourceTag, targetTag, cfg, { exec = execDefault } = {}) {
  if (sourceTag === targetTag) {
    log.skip(`${image}:${targetTag} already points at the deployed tag`);
    return;
  }
  log.info(`--- Retagging ${image}:${sourceTag} -> ${image}:${targetTag} ---`);
  if (exec.isDryRun()) {
    log.info(`  [dry-run] Would run az acr import for ${image}:${sourceTag} -> ${targetTag}`);
    return;
  }
  await exec.capture("az", [
    "acr",
    "import",
    "--name",
    cfg.ACR_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--source",
    `${cfg.ACR_LOGIN_SERVER}/${image}:${sourceTag}`,
    "--image",
    `${image}:${targetTag}`,
    "--force",
    "--output",
    "none",
  ]);
  log.ok(`${cfg.ACR_LOGIN_SERVER}/${image}:${targetTag}`);
}

/**
 * Builds `image:tag` via `az acr build` (passing IMAGE_TAG/GIT_SHA as
 * --build-arg -- the fix called out in the decision log), then retags it as
 * 'latest-release' and stamps provenance. Mirrors build_image()/Invoke-BuildImage().
 */
export async function buildImage(imageSpec, tag, commit, cfg, { exec = execDefault, git = gitDefault } = {}) {
  const { name: image, dockerfile } = imageSpec;
  log.info(`--- Building ${image}:${tag} (${dockerfile}) ---`);

  const gitSha = commit || (await git.currentGitSha({ cwd: cfg.repoRoot })).full || "unknown";

  if (exec.isDryRun()) {
    log.info(`  [dry-run] Would run az acr build for ${image}:${tag}`);
    await stampProvenance(image, tag, commit, cfg, { exec, git });
    return;
  }

  await exec.run(
    "az",
    [
      "acr",
      "build",
      "--registry",
      cfg.ACR_NAME,
      "--resource-group",
      cfg.RESOURCE_GROUP,
      "--image",
      `${image}:${tag}`,
      "--file",
      dockerfile,
      ...buildArgsFor(tag, gitSha),
      "--output",
      "none",
      ".",
    ],
    { cwd: cfg.repoRoot },
  );
  log.ok(`${cfg.ACR_LOGIN_SERVER}/${image}:${tag}`);
  // Also tag as latest-release so it always points at the most recently built version.
  await retagImage(image, tag, "latest-release", cfg, { exec });
  await stampProvenance(image, tag, commit, cfg, { exec, git });
}

const FRONTEND_WATCHED_PATHS = Object.freeze(["apps/web", "apps/Agentweaver.Web"]);

/**
 * Decides whether the frontend's local `npm run build` needs to run before
 * `az acr build`, using the exact same build-vs-retag decision as any other
 * image (forced / no previous tag / no resolvable source commit / watched
 * paths changed). Mirrors the standalone frontend-dist-prep guard in
 * 20-build-push-images.sh (the `if [[ ... ]]; then prepare_frontend_dist; fi`
 * block preceding schedule_image "agentweaver-frontend").
 */
export async function shouldPrepareFrontendDist(targetCommit, cfg, { git = gitDefault, kubectl = kubectlDefault } = {}) {
  const frontendSpec = IMAGES.find((i) => i.name === "agentweaver-frontend");
  const deployedTag = await currentTagFor(frontendSpec, cfg.NAMESPACE, { kubectl });
  const sourceTag = cfg.PREVIOUS_IMAGE_TAG || deployedTag || "";
  if (cfg.FORCE_REBUILD || !sourceTag) return true;
  const sourceCommit = await sourceCommitForTag(sourceTag, { cwd: cfg.repoRoot, git });
  if (!sourceCommit) return true;
  return pathsChanged(sourceCommit, targetCommit, FRONTEND_WATCHED_PATHS, { cwd: cfg.repoRoot, git });
}

/**
 * Builds local frontend assets (apps/web/dist) before the ACR build context
 * is tarred, then temporarily moves apps/web/node_modules out of the repo
 * root (all images share the repo root as build context; az's context-tar
 * step can choke on broken symlinks inside node_modules even when
 * .dockerignore excludes them). Mirrors prepare_frontend_dist() +
 * stash_frontend_node_modules_outside_acr_context(). Callers MUST call
 * restoreFrontendNodeModules() in a finally block afterwards, mirroring the
 * bash script's EXIT trap.
 */
export async function prepareFrontendDist(cfg, { exec = execDefault } = {}) {
  if (exec.isDryRun()) {
    log.info("  [dry-run] Would build local frontend assets before ACR build");
    return;
  }
  log.info("--- Building local frontend assets for agentweaver-frontend ---");
  const webDir = path.join(cfg.repoRoot, "apps", "web");
  await exec.run("npm", ["ci", "--legacy-peer-deps"], { cwd: webDir });
  await exec.run("npm", ["run", "build"], {
    cwd: webDir,
    env: { VITE_API_URL: "", VITE_API_KEY: "" },
  });
  stashFrontendNodeModules(cfg.repoRoot);
}

function frontendNodeModulesPaths(repoRoot) {
  const nodeModulesDir = path.join(repoRoot, "apps", "web", "node_modules");
  const backupDir = `${repoRoot}.frontend-node_modules.${process.pid}`;
  return { nodeModulesDir, backupDir };
}

/** Moves apps/web/node_modules out of the ACR build context (repo root). */
export function stashFrontendNodeModules(repoRoot, { fsImpl = fs } = {}) {
  const { nodeModulesDir, backupDir } = frontendNodeModulesPaths(repoRoot);
  if (!fsImpl.existsSync(nodeModulesDir)) return;
  fsImpl.rmSync(backupDir, { recursive: true, force: true });
  fsImpl.renameSync(nodeModulesDir, backupDir);
  log.info("  [frontend] Temporarily moved node_modules out of the ACR build context");
}

/** Restores apps/web/node_modules after the ACR build context has been tarred. Mirrors restore_frontend_node_modules(). */
export function restoreFrontendNodeModules(repoRoot, { fsImpl = fs } = {}) {
  const { nodeModulesDir, backupDir } = frontendNodeModulesPaths(repoRoot);
  if (!fsImpl.existsSync(backupDir)) return;
  fsImpl.rmSync(nodeModulesDir, { recursive: true, force: true });
  fsImpl.renameSync(backupDir, nodeModulesDir);
}

/**
 * Decides build-vs-retag for one image (mirrors schedule_image()'s decision
 * tree) and returns a plan object describing what will happen, without
 * executing it -- kept separate from execution so the decision itself is
 * unit-testable without any az/git process calls.
 */
export async function planImage(imageSpec, targetCommit, cfg, { git = gitDefault, kubectl = kubectlDefault } = {}) {
  const deployedTag = await currentTagFor(imageSpec, cfg.NAMESPACE, { kubectl });
  const sourceTag = cfg.PREVIOUS_IMAGE_TAG || deployedTag || "";
  const sourceCommit = sourceTag ? await sourceCommitForTag(sourceTag, { cwd: cfg.repoRoot, git }) : null;
  const targetTag = cfg[imageSpec.tagField];

  if (cfg.FORCE_REBUILD || !sourceTag) {
    return { action: "build", image: imageSpec, targetTag, reason: cfg.FORCE_REBUILD ? "forced" : "no previous image tag" };
  }
  if (!sourceCommit) {
    return { action: "build", image: imageSpec, targetTag, reason: `previous tag ${sourceTag} has no resolvable VERSION commit` };
  }
  const changed = await pathsChanged(sourceCommit, targetCommit, imageSpec.watchedPaths, { cwd: cfg.repoRoot, git });
  if (changed) {
    return { action: "build", image: imageSpec, targetTag, reason: `changed since ${sourceTag} at ${sourceCommit.slice(0, 12)}` };
  }
  return {
    action: "retag",
    image: imageSpec,
    targetTag,
    sourceTag,
    sourceCommit,
    reason: `unchanged since ${sourceTag} at ${sourceCommit.slice(0, 12)}`,
  };
}

/** Executes a single image's plan (build, or retag+provenance-restamp). */
async function executePlan(plan, targetCommit, cfg, deps) {
  const { exec = execDefault, git = gitDefault } = deps;
  const image = plan.image.name;
  if (plan.action === "build") {
    log.info(`  [build]  ${image} (${plan.reason})`);
    await buildImage(plan.image, plan.targetTag, targetCommit, cfg, { exec, git });
  } else {
    log.info(`  [retag]  ${image} (${plan.reason})`);
    await retagImage(image, plan.sourceTag, plan.targetTag, cfg, { exec });
    await stampProvenance(image, plan.targetTag, plan.sourceCommit, cfg, { exec, git });
  }
  return { image, tag: plan.targetTag };
}

/**
 * Main entry point: builds/retags/stamps all 4 images described in
 * image-spec.mjs. cfg is the resolved variables.mjs output (RESOURCE_GROUP,
 * ACR_NAME, ACR_LOGIN_SERVER, NAMESPACE, IMAGE_TAG, AGENTHOST_IMAGE_TAG),
 * plus optional overrides: FORCE_REBUILD (bool), PREVIOUS_IMAGE_TAG
 * (string), TARGET_GIT_REF (string, defaults to IMAGE_TAG), repoRoot.
 *
 * @param {Record<string, unknown>} cfg
 * @param {{ exec?: typeof execDefault, git?: typeof gitDefault, kubectl?: typeof kubectlDefault }} [deps]
 */
export async function run(cfg, deps = {}) {
  const exec = deps.exec ?? execDefault;
  const git = deps.git ?? gitDefault;
  const kubectl = deps.kubectl ?? kubectlDefault;
  const repoRoot = cfg.repoRoot ?? DEFAULT_REPO_ROOT;
  const resolvedCfg = { ...cfg, repoRoot };

  log.section("Building, retagging, and pushing Agentweaver images");
  log.field("ACR", resolvedCfg.ACR_LOGIN_SERVER);
  log.field("Image tag", resolvedCfg.IMAGE_TAG);
  log.field("AgentHost image tag", resolvedCfg.AGENTHOST_IMAGE_TAG);

  const targetGitRef = resolvedCfg.TARGET_GIT_REF || resolvedCfg.IMAGE_TAG;
  const targetCommit =
    (await git.revParseCommit(targetGitRef, { cwd: repoRoot })) ||
    (await releaseRefForTag(resolvedCfg.IMAGE_TAG, { cwd: repoRoot, git })) ||
    (await git.revParseHead({ cwd: repoRoot }));

  const plans = [];
  for (const imageSpec of IMAGES) {
    plans.push(await planImage(imageSpec, targetCommit, resolvedCfg, { git, kubectl }));
  }

  const needsFrontendDist = await shouldPrepareFrontendDist(targetCommit, resolvedCfg, { git, kubectl });
  if (needsFrontendDist) {
    await prepareFrontendDist(resolvedCfg, { exec });
  }
  try {
    const results = await Promise.allSettled(
      plans.map((plan) => executePlan(plan, targetCommit, resolvedCfg, { exec, git })),
    );

    const failures = [];
    results.forEach((result, i) => {
      const image = plans[i].image.name;
      if (result.status === "fulfilled") {
        log.ok(`${image}:${plans[i].targetTag}`);
      } else {
        log.error(`${image}: ${result.reason?.message ?? result.reason}`);
        failures.push({ image, error: result.reason });
      }
    });

    if (failures.length > 0) {
      throw new Error(
        `one or more image jobs failed: ${failures.map((f) => `${f.image} (${f.error?.message ?? f.error})`).join("; ")}`,
      );
    }
  } finally {
    if (needsFrontendDist) {
      restoreFrontendNodeModules(repoRoot);
    }
  }

  log.section("IMAGES READY IN ACR");
  for (const imageSpec of IMAGES) {
    log.field(imageSpec.name, `${resolvedCfg.ACR_LOGIN_SERVER}/${imageSpec.name}:${resolvedCfg[imageSpec.tagField]}`);
  }

  return { targetCommit, plans };
}

export { IMAGES };
