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
import { githubReleaseExists, resolveGitHubRepository } from "../lib/github.mjs";
import { IMAGES, buildArgsFor } from "../image-spec.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

const CUSTOM_IMAGE_FIELDS = Object.freeze({
  "agentweaver-api": "IMAGE_API",
  "agentweaver-frontend": "IMAGE_FRONTEND",
  "agentweaver-mcp": "IMAGE_MCP",
  "agentweaver-agent-host": "IMAGE_AGENT_HOST",
});

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

export const GHCR_RELEASE_TAG_RE = /^v\d+\.\d+\.\d+$/;
export const GHCR_SHA_TAG_RE = /^sha-[0-9a-f]{7,40}$/;
const GHCR_MOVING_TAG_RE = /^(?:dev|main|latest|rc-.+)$/;

/**
 * Validates that a GHCR import ref is immutable and one of the supported
 * forms: a published release tag (`vX.Y.Z`) or a `sha-<hex>` image tag.
 *
 * @param {string} ref
 * @returns {{ kind: 'release'|'sha', ref: string, commitish: string }}
 */
export function validateGhcrRef(ref) {
  const value = String(ref ?? "").trim();
  if (GHCR_RELEASE_TAG_RE.test(value)) {
    return { kind: "release", ref: value, commitish: value };
  }
  if (GHCR_SHA_TAG_RE.test(value)) {
    return { kind: "sha", ref: value, commitish: value.slice(4) };
  }
  if (GHCR_MOVING_TAG_RE.test(value)) {
    throw new Error(
      `GHCR ref '${value}' is not allowed: moving tags such as dev, main, latest, and rc-* are forbidden for release imports. Use an immutable vX.Y.Z release tag or sha-<hex>.`,
    );
  }
  throw new Error(`GHCR ref '${value}' is invalid. Use an immutable vX.Y.Z release tag or sha-<hex>.`);
}

export function ghcrImageReference(owner, image, ref) {
  return `ghcr.io/${String(owner).toLowerCase()}/${image}:${ref}`;
}

const ACR_TAG_DIGEST_POLL_INITIAL_DELAY_MS = 2_000;
const ACR_TAG_DIGEST_POLL_MAX_DELAY_MS = 15_000;
const ACR_TAG_DIGEST_POLL_BUDGET_MS = 5 * 60_000;
// `show-manifests` is read-only, so bounding this local CLI query cannot
// duplicate a build/import. A query timeout simply counts as "not visible
// yet" and the existing bounded backoff continues.
const ACR_QUERY_TIMEOUT_MS = 60_000;
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
      { allowFailure: true, timeoutMs: cfg.ACR_QUERY_TIMEOUT_MS || ACR_QUERY_TIMEOUT_MS },
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

/** Looks up the digest a concrete ACR image tag currently resolves to via `az acr repository show`. */
export async function acrRepositoryDigestForImage(image, tag, cfg, { exec = execDefault } = {}) {
  const { stdout, code } = await exec.capture(
    "az",
    [
      "acr",
      "repository",
      "show",
      "--name",
      cfg.ACR_NAME,
      "--image",
      `${image}:${tag}`,
      "--query",
      "digest",
      "--output",
      "tsv",
    ],
    { allowFailure: true, timeoutMs: cfg.ACR_QUERY_TIMEOUT_MS || ACR_QUERY_TIMEOUT_MS },
  );
  if (code !== 0) return null;
  return stdout.trim() || null;
}

export async function waitForAcrRepositoryDigest(image, tag, cfg, { exec = execDefault, sleep = defaultSleep } = {}) {
  const initialDigest = await acrRepositoryDigestForImage(image, tag, cfg, { exec });
  if (initialDigest) return initialDigest;

  for (const delay of ACR_TAG_DIGEST_POLL_DELAYS_MS) {
    await sleep(delay);
    const digest = await acrRepositoryDigestForImage(image, tag, cfg, { exec });
    if (digest) return digest;
  }
  return null;
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

  const sourceDigest = await log.withTiming(
    `Waiting for ACR digest ${image}:${tag}`,
    () => waitForAcrTagDigest(image, tag, cfg, { exec }),
  );
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

  await log.withTiming(`ACR provenance import ${image}:${provTag}`, () =>
    exec.capture("az", [
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
    ], { timeoutMs: cfg.ACR_IMPORT_TIMEOUT_MS || undefined }),
  );

  const stampedDigest = await log.withTiming(
    `Waiting for ACR digest ${image}:${provTag}`,
    () => waitForAcrTagDigest(image, provTag, cfg, { exec }),
  );
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
  const lockResult = await log.withTiming(
    `ACR provenance lock ${image}:${provTag}`,
    () => exec.capture(
      "az",
      ["acr", "repository", "update", "--name", cfg.ACR_NAME, "--image", `${image}:${provTag}`, "--write-enabled", "false"],
      { allowFailure: true, timeoutMs: cfg.ACR_IMPORT_TIMEOUT_MS || undefined },
    ),
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
  await log.withTiming(`ACR retag ${image}:${sourceTag} -> ${targetTag}`, () =>
    exec.capture("az", [
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
    ], { timeoutMs: cfg.ACR_IMPORT_TIMEOUT_MS || undefined }),
  );
  log.ok(`${cfg.ACR_LOGIN_SERVER}/${image}:${targetTag}`);
}

function ghcrImportAuthArgs(cfg) {
  if (!cfg.GHCR_TOKEN) return [];
  return ["--username", cfg.GHCR_OWNER, "--password", cfg.GHCR_TOKEN];
}

async function importIntoAcr(source, image, targetTag, cfg, { exec = execDefault, force = false, ghcrAuth = false } = {}) {
  const args = [
    "acr",
    "import",
    "--name",
    cfg.ACR_NAME,
    "--resource-group",
    cfg.RESOURCE_GROUP,
    "--source",
    source,
    "--image",
    `${image}:${targetTag}`,
    ...(force ? ["--force"] : []),
    ...(ghcrAuth ? ghcrImportAuthArgs(cfg) : []),
    "--output",
    "none",
  ];
  await log.withTiming(`ACR import ${image}:${targetTag}`, () =>
    exec.capture("az", args, { timeoutMs: cfg.ACR_IMPORT_TIMEOUT_MS || undefined }),
  );
}

async function untagImage(image, tag, cfg, { exec = execDefault } = {}) {
  await exec.capture(
    "az",
    ["acr", "repository", "untag", "--name", cfg.ACR_NAME, "--image", `${image}:${tag}`],
    { allowFailure: true, timeoutMs: cfg.ACR_IMPORT_TIMEOUT_MS || undefined },
  );
}

function ghcrStageTag(targetTag) {
  return `${targetTag}-ghcr-preflight-${process.pid}-${Date.now()}`;
}

function customStageTag(targetTag) {
  return `${targetTag}-custom-preflight-${process.pid}-${Date.now()}`;
}

function customImageReferenceFor(imageSpec, cfg) {
  const field = CUSTOM_IMAGE_FIELDS[imageSpec.name];
  if (!field) {
    throw new Error(`No custom image field configured for ${imageSpec.name}.`);
  }
  const sourceImage = cfg[field];
  if (!sourceImage) {
    throw new Error(`IMAGE_SOURCE=custom requires ${field} for ${imageSpec.name}.`);
  }
  return sourceImage;
}

export async function resolveGhcrSource(cfg, { exec = execDefault, git = gitDefault, fetchImpl = globalThis.fetch } = {}) {
  const ref = validateGhcrRef(cfg.GHCR_REF);

  if (ref.kind === "sha") {
    const sourceCommit = await git.revParseCommit(ref.commitish, { cwd: cfg.repoRoot });
    if (!sourceCommit) {
      throw new Error(`GHCR ref '${cfg.GHCR_REF}' does not resolve to a local git commit.`);
    }
    return { kind: ref.kind, sourceRef: ref.ref, sourceCommit };
  }

  const repo = cfg.GHCR_REPOSITORY
    ? { owner: cfg.GHCR_OWNER, repo: cfg.GHCR_REPOSITORY }
    : await resolveGitHubRepository({ repoRoot: cfg.repoRoot, exec });
  if (!repo?.owner || !repo?.repo) {
    throw new Error(`Cannot verify GHCR release ref '${cfg.GHCR_REF}': the repo's GitHub origin remote could not be resolved.`);
  }

  const releaseExists = await githubReleaseExists(repo.owner, repo.repo, cfg.GHCR_REF, { fetchImpl });
  if (!releaseExists) {
    throw new Error(`GHCR ref '${cfg.GHCR_REF}' is not a published GitHub Release tag for ${repo.owner}/${repo.repo}.`);
  }

  const sourceCommit =
    (await git.revParseCommit(cfg.GHCR_REF, { cwd: cfg.repoRoot })) ||
    (await releaseRefForTag(cfg.GHCR_REF, { cwd: cfg.repoRoot, git }));
  if (!sourceCommit) {
    throw new Error(`GHCR release ref '${cfg.GHCR_REF}' was found on GitHub but could not be resolved to a local source commit.`);
  }

  return { kind: ref.kind, sourceRef: ref.ref, sourceCommit, repository: repo };
}

export async function importImagesFromGhcr(cfg, deps = {}) {
  const exec = deps.exec ?? execDefault;
  const git = deps.git ?? gitDefault;
  const fetchImpl = deps.fetchImpl ?? globalThis.fetch;
  const sleep = deps.sleep ?? defaultSleep;
  const resolvedCfg = { ...cfg, repoRoot: cfg.repoRoot ?? DEFAULT_REPO_ROOT };
  const ghcrSource = await resolveGhcrSource(resolvedCfg, { exec, git, fetchImpl });

  log.section("Importing Agentweaver images from GHCR");
  log.field("GHCR owner", resolvedCfg.GHCR_OWNER);
  log.field("GHCR ref", ghcrSource.sourceRef);
  log.info("Preflight strategy: import all four GHCR images into temporary ACR staging tags first, then promote final tags only after every staging import succeeds.");

  const stagedPlans = IMAGES.map((imageSpec) => ({
    image: imageSpec,
    targetTag: resolvedCfg[imageSpec.tagField],
    stageTag: ghcrStageTag(resolvedCfg[imageSpec.tagField]),
    ghcrRef: ghcrImageReference(resolvedCfg.GHCR_OWNER, imageSpec.name, ghcrSource.sourceRef),
  }));

  const stageResults = await Promise.allSettled(
    stagedPlans.map(async (plan) => {
      await importIntoAcr(plan.ghcrRef, plan.image.name, plan.stageTag, resolvedCfg, { exec, ghcrAuth: true });
      const stageDigest = await waitForAcrRepositoryDigest(plan.image.name, plan.stageTag, resolvedCfg, { exec, sleep });
      if (!stageDigest) {
        throw new Error(`imported staging image ${plan.image.name}:${plan.stageTag} has no resolvable ACR digest`);
      }
      log.field(`${plan.image.name} staged digest`, stageDigest);
      return { ...plan, stageDigest };
    }),
  );

  const stageFailures = [];
  const successfulStages = [];
  for (const result of stageResults) {
    if (result.status === "fulfilled") {
      successfulStages.push(result.value);
    } else {
      stageFailures.push(result.reason);
    }
  }

  if (stageFailures.length > 0) {
    await Promise.all(successfulStages.map((stage) => untagImage(stage.image.name, stage.stageTag, resolvedCfg, { exec })));
    throw new Error(
      `GHCR preflight failed before any final tags were updated: ${stageFailures.map((failure) => failure?.message ?? failure).join("; ")}`,
    );
  }

  const promotionPlans = await Promise.all(successfulStages.map(async (stage) => ({
    ...stage,
    existingDigest: await acrRepositoryDigestForImage(stage.image.name, stage.targetTag, resolvedCfg, { exec }),
  })));
  const conflictingPromotions = promotionPlans.filter(
    (stage) => stage.existingDigest && stage.existingDigest !== stage.stageDigest && !resolvedCfg.FORCE,
  );
  if (conflictingPromotions.length > 0) {
    throw new Error(
      `Refusing to overwrite conflicting existing ACR tags without --force: ${conflictingPromotions.map((stage) => (
        `${stage.image.name}:${stage.targetTag} already exists in ACR with digest ${stage.existingDigest}; requested digest ${stage.stageDigest}`
      )).join("; ")}`,
    );
  }

  const expectedImageDigests = {};
  const importedImageSources = {};

  try {
    for (const stage of promotionPlans) {
      const { existingDigest } = stage;
      if (existingDigest === stage.stageDigest) {
        log.skip(`${stage.image.name}:${stage.targetTag} already resolves to imported digest ${stage.stageDigest}`);
      } else {
        await importIntoAcr(
          `${resolvedCfg.ACR_LOGIN_SERVER}/${stage.image.name}@${stage.stageDigest}`,
          stage.image.name,
          stage.targetTag,
          resolvedCfg,
          { exec, force: Boolean(existingDigest) && Boolean(resolvedCfg.FORCE) },
        );
      }

      const finalDigest = await waitForAcrRepositoryDigest(stage.image.name, stage.targetTag, resolvedCfg, { exec, sleep });
      if (!finalDigest) {
        throw new Error(`final imported image ${stage.image.name}:${stage.targetTag} has no resolvable ACR digest`);
      }
      if (finalDigest !== stage.stageDigest) {
        throw new Error(
          `${stage.image.name}:${stage.targetTag} resolved to ${finalDigest} after import; expected ${stage.stageDigest}.`,
        );
      }

      log.field(`${stage.image.name} final digest`, finalDigest);
      expectedImageDigests[stage.image.name] = finalDigest;
      importedImageSources[stage.image.name] = {
        digest: finalDigest,
        sourceCommit: ghcrSource.sourceCommit,
        sourceRef: ghcrSource.sourceRef,
      };

      await retagImage(stage.image.name, stage.targetTag, "latest-release", resolvedCfg, { exec });
      await stampProvenance(stage.image.name, stage.targetTag, ghcrSource.sourceCommit, resolvedCfg, { exec, git });
    }
  } finally {
    await Promise.all(successfulStages.map((stage) => untagImage(stage.image.name, stage.stageTag, resolvedCfg, { exec })));
  }

  log.section("IMAGES READY IN ACR");
  for (const imageSpec of IMAGES) {
    log.field(
      imageSpec.name,
      `${resolvedCfg.ACR_LOGIN_SERVER}/${imageSpec.name}:${resolvedCfg[imageSpec.tagField]} @ ${expectedImageDigests[imageSpec.name]}`,
    );
  }

  return {
    imageSource: "ghcr",
    targetCommit: ghcrSource.sourceCommit,
    plans: successfulStages.map((stage) => ({ action: "import", image: stage.image, targetTag: stage.targetTag })),
    expectedImageDigests,
    importedImageSources,
  };
}

export async function importImagesFromCustomSources(cfg, deps = {}) {
  const exec = deps.exec ?? execDefault;
  const sleep = deps.sleep ?? defaultSleep;
  const resolvedCfg = { ...cfg, repoRoot: cfg.repoRoot ?? DEFAULT_REPO_ROOT };

  log.section("Importing operator-specified images into ACR");
  log.warn(
    "IMAGE_SOURCE=custom is an explicit trust boundary override: the deploy will import exactly the image refs you supplied. Use only registries and images you trust.",
  );
  log.info("Preflight strategy: import all four custom images into temporary ACR staging tags first, then promote final tags only after every staging import succeeds.");

  const stagedPlans = IMAGES.map((imageSpec) => ({
    image: imageSpec,
    targetTag: resolvedCfg[imageSpec.tagField],
    stageTag: customStageTag(resolvedCfg[imageSpec.tagField]),
    sourceImage: customImageReferenceFor(imageSpec, resolvedCfg),
  }));

  const stageResults = await Promise.allSettled(
    stagedPlans.map(async (plan) => {
      await importIntoAcr(plan.sourceImage, plan.image.name, plan.stageTag, resolvedCfg, { exec });
      const stageDigest = await waitForAcrRepositoryDigest(plan.image.name, plan.stageTag, resolvedCfg, { exec, sleep });
      if (!stageDigest) {
        throw new Error(`imported staging image ${plan.image.name}:${plan.stageTag} has no resolvable ACR digest`);
      }
      log.field(`${plan.image.name} staged digest`, stageDigest);
      return { ...plan, stageDigest };
    }),
  );

  const stageFailures = [];
  const successfulStages = [];
  for (const result of stageResults) {
    if (result.status === "fulfilled") {
      successfulStages.push(result.value);
    } else {
      stageFailures.push(result.reason);
    }
  }

  if (stageFailures.length > 0) {
    await Promise.all(successfulStages.map((stage) => untagImage(stage.image.name, stage.stageTag, resolvedCfg, { exec })));
    throw new Error(
      `Custom image import preflight failed before any final tags were updated: ${stageFailures.map((failure) => failure?.message ?? failure).join("; ")}`,
    );
  }

  const promotionPlans = await Promise.all(successfulStages.map(async (stage) => ({
    ...stage,
    existingDigest: await acrRepositoryDigestForImage(stage.image.name, stage.targetTag, resolvedCfg, { exec }),
  })));
  const conflictingPromotions = promotionPlans.filter(
    (stage) => stage.existingDigest && stage.existingDigest !== stage.stageDigest && !resolvedCfg.FORCE,
  );
  if (conflictingPromotions.length > 0) {
    throw new Error(
      `Refusing to overwrite conflicting existing ACR tags without --force: ${conflictingPromotions.map((stage) => (
        `${stage.image.name}:${stage.targetTag} already exists in ACR with digest ${stage.existingDigest}; requested digest ${stage.stageDigest} from ${stage.sourceImage}`
      )).join("; ")}`,
    );
  }

  const expectedImageDigests = {};
  const importedImageSources = {};

  try {
    for (const stage of promotionPlans) {
      const { existingDigest } = stage;
      if (existingDigest === stage.stageDigest) {
        log.skip(`${stage.image.name}:${stage.targetTag} already resolves to imported digest ${stage.stageDigest}`);
      } else {
        await importIntoAcr(
          `${resolvedCfg.ACR_LOGIN_SERVER}/${stage.image.name}@${stage.stageDigest}`,
          stage.image.name,
          stage.targetTag,
          resolvedCfg,
          { exec, force: Boolean(existingDigest) && Boolean(resolvedCfg.FORCE) },
        );
      }

      const finalDigest = await waitForAcrRepositoryDigest(stage.image.name, stage.targetTag, resolvedCfg, { exec, sleep });
      if (!finalDigest) {
        throw new Error(`final imported image ${stage.image.name}:${stage.targetTag} has no resolvable ACR digest`);
      }
      if (finalDigest !== stage.stageDigest) {
        throw new Error(
          `${stage.image.name}:${stage.targetTag} resolved to ${finalDigest} after import; expected ${stage.stageDigest}.`,
        );
      }

      log.field(`${stage.image.name} final digest`, finalDigest);
      expectedImageDigests[stage.image.name] = finalDigest;
      importedImageSources[stage.image.name] = {
        digest: finalDigest,
        sourceImage: stage.sourceImage,
      };
    }
  } finally {
    await Promise.all(successfulStages.map((stage) => untagImage(stage.image.name, stage.stageTag, resolvedCfg, { exec })));
  }

  log.section("IMAGES READY IN ACR");
  for (const imageSpec of IMAGES) {
    log.field(
      imageSpec.name,
      `${resolvedCfg.ACR_LOGIN_SERVER}/${imageSpec.name}:${resolvedCfg[imageSpec.tagField]} @ ${expectedImageDigests[imageSpec.name]}`,
    );
  }

  return {
    imageSource: "custom",
    plans: successfulStages.map((stage) => ({ action: "import", image: stage.image, targetTag: stage.targetTag })),
    expectedImageDigests,
    importedImageSources,
  };
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

  await log.withTiming(`ACR build ${image}:${tag}`, () => exec.run(
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
    { cwd: cfg.repoRoot, timeoutMs: cfg.ACR_BUILD_TIMEOUT_MS || undefined },
  ));
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
  await log.withTiming("Frontend dependency install", () => exec.run("npm", ["ci", "--legacy-peer-deps"], { cwd: webDir }));
  await log.withTiming("Frontend asset build", () => exec.run("npm", ["run", "build"], {
    cwd: webDir,
    env: { VITE_API_URL: "", VITE_API_KEY: "" },
  }));
  stashFrontendNodeModules(cfg.repoRoot);
}

function frontendNodeModulesPaths(repoRoot) {
  const nodeModulesDir = path.join(repoRoot, "apps", "web", "node_modules");
  const backupDir = `${repoRoot}.frontend-node_modules.${process.pid}`;
  return { nodeModulesDir, backupDir };
}

function removeStaleFrontendNodeModulesStashes(repoRoot, backupDir, fsImpl) {
  const parentDir = path.dirname(repoRoot);
  const prefix = `${path.basename(repoRoot)}.frontend-node_modules.`;
  for (const entry of fsImpl.readdirSync(parentDir, { withFileTypes: true })) {
    const candidate = path.join(parentDir, entry.name);
    if (entry.isDirectory() && entry.name.startsWith(prefix) && candidate !== backupDir) {
      fsImpl.rmSync(candidate, { recursive: true, force: true });
    }
  }
}

/** Moves apps/web/node_modules out of the ACR build context (repo root). */
export function stashFrontendNodeModules(repoRoot, { fsImpl = fs } = {}) {
  const { nodeModulesDir, backupDir } = frontendNodeModulesPaths(repoRoot);
  removeStaleFrontendNodeModulesStashes(repoRoot, backupDir, fsImpl);
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
  return log.withTiming(`${plan.action === "build" ? "Image build lifecycle" : "Image retag lifecycle"} ${image}:${plan.targetTag}`, async () => {
    if (plan.action === "build") {
      log.info(`  [build]  ${image} (${plan.reason})`);
      await buildImage(plan.image, plan.targetTag, targetCommit, cfg, { exec, git });
    } else {
      log.info(`  [retag]  ${image} (${plan.reason})`);
      await retagImage(image, plan.sourceTag, plan.targetTag, cfg, { exec });
      await stampProvenance(image, plan.targetTag, plan.sourceCommit, cfg, { exec, git });
    }
    return { image, tag: plan.targetTag };
  });
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
export async function runAcrBuild(cfg, deps = {}) {
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
  const planSummary = plans.reduce(
    (summary, plan) => ({ ...summary, [plan.action]: (summary[plan.action] ?? 0) + 1 }),
    {},
  );
  log.info(`Image plan: ${planSummary.build ?? 0} build, ${planSummary.retag ?? 0} retag.`);

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

export async function run(cfg, deps = {}) {
  const resolvedCfg = { ...cfg, repoRoot: cfg.repoRoot ?? DEFAULT_REPO_ROOT };
  if (resolvedCfg.IMAGE_SOURCE === "ghcr") {
    return importImagesFromGhcr(resolvedCfg, deps);
  }
  if (resolvedCfg.IMAGE_SOURCE === "custom") {
    return importImagesFromCustomSources(resolvedCfg, deps);
  }
  return runAcrBuild(resolvedCfg, deps);
}

export { IMAGES };
