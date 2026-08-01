// ghcr-plan.mjs -- turns a GitHub Actions trigger context into the concrete
// plan for publishing Agentweaver container images to the GitHub Container
// Registry (ghcr.io), the "GitHub artifact registry" side of the pipeline.
//
// The list of buildable images is NOT redefined here: it is imported from
// scripts/azure/image-spec.mjs, which the deploy toolchain already treats as
// the single source of truth ("Do NOT duplicate this list anywhere else").
// This module only answers two questions the Azure toolchain never has to:
//
//   1. Which publishing *channel* does this trigger represent?
//        dev      -- push to the protected `dev` integration branch
//        rc       -- push to a `release/vX.Y.Z` soak branch
//        release  -- a published GitHub Release (annotated vX.Y.Z tag)
//        main     -- push to `main` (post-promotion, pre-tag)
//        commit   -- any other ref, i.e. a manual workflow_dispatch build of
//                    an arbitrary commit/branch
//   2. Which ghcr.io tags does each image get for that channel?
//
// Every channel always produces the immutable `sha-<short>` tag, so any build
// -- including a manual one-off -- is addressable by the exact commit it was
// built from, exactly like the short-SHA image tags used by
// `npm run azure:deploy-from-local` / `deploy-from-commit`. Moving tags
// (`dev`, `main`, `rc-X.Y.Z`, `X.Y.Z`, `vX.Y.Z`, `latest`) are layered on top
// where they are meaningful.

import { appendFileSync } from "node:fs";
import { IMAGES } from "../azure/image-spec.mjs";

/** Registry host for GitHub's container/artifact registry. */
export const REGISTRY = "ghcr.io";

/**
 * Normalizes a git ref (`refs/heads/dev`, `refs/tags/v1.2.3`, `dev`, ...) to
 * its short form.
 *
 * @param {string} ref
 * @returns {string}
 */
export function shortRef(ref = "") {
  return ref.replace(/^refs\/heads\//, "").replace(/^refs\/tags\//, "");
}

/**
 * Extracts `X.Y.Z` from a `release/vX.Y.Z` branch name or a `vX.Y.Z` tag.
 * Returns null when the ref carries no semver.
 *
 * @param {string} ref
 * @returns {string|null}
 */
export function versionFromRef(ref = "") {
  const name = shortRef(ref);
  const match = /^(?:release\/)?v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$/.exec(name);
  return match ? match[1] : null;
}

/**
 * Classifies a trigger into a publishing channel.
 *
 * @param {object} ctx
 * @param {string} ctx.eventName GitHub `github.event_name`.
 * @param {string} ctx.ref GitHub `github.ref`.
 * @param {string} [ctx.releaseTag] Tag of the published release (`release` event).
 * @returns {'dev'|'rc'|'release'|'main'|'commit'}
 */
export function resolveChannel({ eventName, ref = "", releaseTag = "" } = {}) {
  if (eventName === "release") return "release";
  if (releaseTag) return "release";
  const name = shortRef(ref);
  if (name === "dev") return "dev";
  if (name === "main") return "main";
  if (name.startsWith("release/v")) return "rc";
  return "commit";
}

/**
 * Computes the tag suffixes (registry- and image-name-independent) for a
 * channel. Order matters only for readability; the workflow passes them all
 * to a single `docker/build-push-action` invocation.
 *
 * @param {object} ctx
 * @param {'dev'|'rc'|'release'|'main'|'commit'} ctx.channel
 * @param {string} ctx.sha Full or short commit SHA the build corresponds to.
 * @param {string} [ctx.ref] Git ref, used to derive `X.Y.Z` for rc builds.
 * @param {string} [ctx.releaseTag] `vX.Y.Z` tag for release builds.
 * @param {boolean} [ctx.prerelease] True when the GitHub Release is a prerelease.
 * @returns {string[]}
 */
export function tagsForChannel({ channel, sha = "", ref = "", releaseTag = "", prerelease = false } = {}) {
  const short = sha.slice(0, 7);
  if (!short) throw new Error("ghcr-plan: a commit sha is required to compute image tags");
  const tags = [`sha-${short}`];

  if (channel === "dev") tags.push("dev");
  if (channel === "main") tags.push("main");
  if (channel === "rc") {
    const version = versionFromRef(ref);
    if (!version) throw new Error(`ghcr-plan: could not derive a version from release branch ref '${ref}'`);
    tags.push(`rc-${version}`);
  }
  if (channel === "release") {
    const version = versionFromRef(releaseTag) ?? versionFromRef(ref);
    if (!version) throw new Error(`ghcr-plan: could not derive a version from release tag '${releaseTag || ref}'`);
    tags.push(version, `v${version}`);
    // `latest` must only ever mean "newest stable release": a prerelease
    // (or a release cut from a -rc/-beta tag) never moves it.
    if (!prerelease && !version.includes("-")) tags.push("latest");
  }

  return [...new Set(tags)];
}

/**
 * Picks the tag that best identifies a build for image metadata purposes: the
 * semver tag for a release, otherwise the immutable short-sha tag. Feeds the
 * `IMAGE_TAG` build arg, i.e. `org.opencontainers.image.version` and the
 * runtime provenance env vars every Dockerfile declares.
 *
 * @param {string[]} tags Output of {@link tagsForChannel}.
 * @returns {string}
 */
export function primaryTag(tags = []) {
  return tags.find((tag) => /^v\d+\.\d+\.\d+/.test(tag)) ?? tags[0];
}

/**
 * Builds the full `ghcr.io/<owner>/<image>:<tag>` reference list for one image.
 *
 * @param {string} owner Repository owner (`github.repository_owner`).
 * @param {string} image GHCR repository name, e.g. `agentweaver-api`.
 * @param {string[]} tags
 * @returns {string[]}
 */
export function imageReferences(owner, image, tags) {
  const namespace = `${REGISTRY}/${String(owner).toLowerCase()}/${image}`;
  return tags.map((tag) => `${namespace}:${tag}`);
}

/**
 * Produces the complete publishing plan: one matrix entry per image, each
 * carrying everything the build job needs (dockerfile, context, whether the
 * frontend assets must be built first, and its fully-qualified tag list).
 *
 * @param {object} ctx See {@link resolveChannel} / {@link tagsForChannel}.
 * @param {string} ctx.owner Repository owner.
 * @returns {{channel: string, tags: string[], images: object[]}}
 */
export function buildPlan(ctx = {}) {
  const channel = resolveChannel(ctx);
  const tags = tagsForChannel({ ...ctx, channel });
  const images = IMAGES.map((image) => ({
    name: image.name,
    dockerfile: image.dockerfile,
    context: image.context,
    frontendBuild: image.frontendBuild,
    tags: imageReferences(ctx.owner, image.name, tags).join("\n"),
  }));
  return { channel, tags, primary: primaryTag(tags), images };
}

/**
 * CLI entry point used by .github/workflows/publish-images.yml: reads the
 * trigger context from the environment and appends `channel`, `tags` and
 * `matrix` to $GITHUB_OUTPUT (falling back to stdout when run locally).
 *
 * @param {object} [opts]
 * @param {NodeJS.ProcessEnv} [opts.env]
 * @param {(line: string) => void} [opts.write]
 * @returns {{channel: string, tags: string[], images: object[]}}
 */
export function main({ env = process.env, write } = {}) {
  const plan = buildPlan({
    owner: env.GITHUB_REPOSITORY_OWNER ?? "",
    eventName: env.GITHUB_EVENT_NAME ?? "",
    ref: env.GITHUB_REF ?? "",
    sha: env.GITHUB_SHA ?? "",
    releaseTag: env.RELEASE_TAG ?? "",
    prerelease: env.RELEASE_PRERELEASE === "true",
  });

  const emit =
    write ??
    (env.GITHUB_OUTPUT
      ? (line) => appendFileSync(env.GITHUB_OUTPUT, `${line}\n`)
      : (line) => process.stdout.write(`${line}\n`));

  emit(`channel=${plan.channel}`);
  emit(`tags=${plan.tags.join(",")}`);
  emit(`primary_tag=${plan.primary}`);
  emit(`matrix=${JSON.stringify({ include: plan.images })}`);
  return plan;
}

if (process.argv[1] && import.meta.url === `file://${process.argv[1]}`) {
  main();
}
