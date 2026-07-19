// 25-verify-image-provenance.mjs -- Node port of
// scripts/aks/25-verify-image-provenance.sh (+ _provenance-functions.ps1).
// Independent, post-deploy safety net for #251 ("release image retag-forward
// can ship stale code"): re-derives, from what is ACTUALLY running in
// AKS/ACR right now (never from 20-build-push-images.mjs's own in-process
// decision), whether every live image is provably built from source with no
// drift in its watched paths (image-spec.mjs -- the SAME watched-path list
// used by the build step, per the decision log's single-source-of-truth fix).
//
// BUGFIX PRESERVED (do not reintroduce): 30-deploy.sh historically defaulted
// VERIFY_GIT_REF to IMAGE_TAG (a VERSION-file-derived semver string, not
// necessarily a git ref/tag -- releases since v0.9.36 stopped creating a
// matching git tag on every VERSION bump). That could throw "fatal: Needed a
// single revision" mid-deploy. The fix (already applied in 30-deploy.sh) is
// to default VERIFY_GIT_REF to HEAD. This port defaults `cfg.VERIFY_GIT_REF`
// to 'HEAD' (see run()) and fails with a clear, actionable error (not a raw
// git error) if an explicitly supplied ref does not resolve -- exactly
// mirroring the bash script's defensive check, so this bug cannot resurface
// through this port.

import * as log from "../lib/log.mjs";
import * as execDefault from "../lib/exec.mjs";
import * as gitDefault from "../lib/git.mjs";
import * as kubectlDefault from "../lib/kubectl.mjs";
import { IMAGES } from "../image-spec.mjs";
import { DEFAULT_REPO_ROOT } from "../variables.mjs";

const DIGEST_RE = /(sha256:[0-9a-f]{64})/;
const PROV_TAG_RE = /^prov-(?:[0-9a-f]{12}|[0-9a-f]{40})$/;

/** Extracts the tag (portion after the final ':') from an image ref, or ''. */
export function imageTagFromRef(imageRef) {
  if (!imageRef) return "";
  const lastSegment = imageRef.slice(imageRef.lastIndexOf("/") + 1);
  return lastSegment.includes(":") ? lastSegment.slice(lastSegment.lastIndexOf(":") + 1) : "";
}

/** Extracts the sha256 digest from a container status imageID, or null. */
export function imageDigestFromId(imageId) {
  const match = DIGEST_RE.exec(imageId || "");
  return match ? match[1] : null;
}

/**
 * Determines the single live image digest/tag currently running for a pod
 * selector, mirroring live_digest_state_for_selector()/
 * Get-LiveDigestStateForSelector(). Returns `{ ok, skipped, digest, tag,
 * podCount, failReason }`.
 *
 * IMPORTANT (#351): for agent-host, `allowEphemeralPods` must stay true and
 * the caller's podSelector MUST already exclude claimed per-run sandbox pods
 * (see image-spec.mjs's agent-host `provenance.podSelector`) -- claimed
 * run sandboxes are ephemeral and can legitimately keep running an older
 * image after a release ships. This function itself is selector-agnostic.
 */
export async function liveDigestStateForSelector(
  label,
  selector,
  expectedReplicas,
  allowEphemeralPods,
  { namespace, kubectl = kubectlDefault } = {},
) {
  if (!allowEphemeralPods && !expectedReplicas) {
    return { ok: false, failReason: `${label}: could not determine desired replica count for selector '${selector}'` };
  }

  const pods = await kubectl.podStatusForSelector(selector, namespace);
  if (pods.length === 0) {
    if (allowEphemeralPods) return { ok: true, skipped: true };
    return { ok: false, failReason: `${label}: no pods found for selector '${selector}'` };
  }

  let digest = null;
  let tag = null;
  let podCount = 0;

  for (const pod of pods) {
    if (pod.deletionTimestamp) {
      // A pod with deletionTimestamp set is an OLD-generation pod on its way
      // out post-rollout (kubectl rollout status only waits for the NEW
      // ReplicaSet to become available; the OLD ReplicaSet's pods terminate
      // asynchronously afterward, per terminationGracePeriodSeconds -- this
      // is standard Kubernetes behavior, not a sign of an unstable rollout).
      // Always exclude it from the live-state count, regardless of
      // allowEphemeralPods -- treating it as fatal here caused a real
      // false-positive race in Phase 7 staging verification: provenance now
      // runs immediately post-deploy, so this window is hit on every real
      // upgrade, not just occasionally.
      log.info(`${label}: ignoring terminating (old-generation) pod ${pod.name}`);
      continue;
    }
    if (pod.phase !== "Running") {
      if (allowEphemeralPods) {
        log.info(`${label}: ignoring pod ${pod.name} in state='${pod.phase}'`);
        continue;
      }
      return {
        ok: false,
        failReason: `${label}: pod ${pod.name} is phase='${pod.phase}' (expected Running); refusing provenance check while replicas are unavailable`,
      };
    }
    podCount += 1;
    if (pod.ready !== "true") {
      return {
        ok: false,
        failReason: `${label}: pod ${pod.name} is not Ready; refusing provenance check while replicas are unavailable`,
      };
    }

    const podDigest = imageDigestFromId(pod.imageId);
    if (!podDigest) {
      return {
        ok: false,
        failReason: `${label}: pod ${pod.name} has no resolvable imageID digest yet; refusing provenance check while replicas are unavailable`,
      };
    }
    const podTag = imageTagFromRef(pod.imageRef);
    if (!digest) {
      digest = podDigest;
      tag = podTag;
      continue;
    }
    if (podDigest !== digest) {
      return {
        ok: false,
        failReason: `${label}: mixed live digests across replicas (${digest} vs ${podDigest}); rollout/retag state is not uniform, refusing provenance check`,
      };
    }
  }

  if (podCount === 0) {
    if (allowEphemeralPods) return { ok: true, skipped: true };
    return { ok: false, failReason: `${label}: no pods found for selector '${selector}'` };
  }
  if (!allowEphemeralPods && podCount !== Number(expectedReplicas)) {
    return {
      ok: false,
      failReason: `${label}: expected ${expectedReplicas} pod(s) for selector '${selector}', found ${podCount}; refusing provenance check while replicas are unavailable`,
    };
  }

  return { ok: true, digest, tag, podCount };
}

/** Finds unique 'prov-<sha>' tag(s) on `image` whose manifest digest equals `digest`. */
export async function provenanceTagsForDigest(image, digest, cfg, { exec = execDefault } = {}) {
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
        `[?digest=='${digest}'].tags[]`,
        "--output",
        "tsv",
      ],
      { allowFailure: true },
    );
    const tags = stdout
      .split(/[\t\n]/)
      .map((t) => t.trim())
      .filter((t) => PROV_TAG_RE.test(t));
    return [...new Set(tags)].sort();
  } catch {
    return [];
  }
}

/** Resolves a prov-<sha> tag's sha suffix to a full commit, via lib/git.mjs's resolveCommitish(). */
export async function resolveProvenanceCommit(commitish, { cwd, git = gitDefault } = {}) {
  return git.resolveCommitish(commitish, { cwd });
}

/**
 * Verifies one image's live provenance: the digest currently running must
 * carry a 'prov-<sha>' tag whose commit shows no diff in `paths` vs
 * `verifyCommit`. Mirrors verify_image()/Invoke-VerifyImage(). Returns
 * `{ status: 'ok'|'fail', message }`.
 */
export async function verifyImage(label, image, paths, verifyCommit, cfg, deps = {}) {
  const { exec = execDefault, git = gitDefault, kubectl = kubectlDefault } = deps;
  const imageSpec = IMAGES.find((i) => i.name === image);
  const { podSelector, allowEphemeralPods, deployment } = imageSpec.provenance;
  const expectedReplicas = deployment
    ? await kubectl.desiredDeploymentReplicas(deployment, cfg.NAMESPACE)
    : "";

  const liveState = await liveDigestStateForSelector(label, podSelector, expectedReplicas, allowEphemeralPods, {
    namespace: cfg.NAMESPACE,
    kubectl,
  });
  if (!liveState.ok) return { status: "fail", message: liveState.failReason };
  if (liveState.skipped) {
    return { status: "ok", message: `${label}: no Running pods found for selector '${podSelector}'; no ephemeral pod image to verify` };
  }
  if (!liveState.digest) {
    return { status: "fail", message: `${label}: could not determine live digest from running pods` };
  }

  const provTags = await provenanceTagsForDigest(image, liveState.digest, cfg, { exec });
  if (provTags.length === 0) {
    return {
      status: "fail",
      message: `${label}: no prov-<sha> tag found for live digest ${liveState.digest.slice(0, 19)} -- image predates the #251/#303 provenance fix, or was pushed by a route other than 20-build-push-images. Cannot verify provenance; treat as unverified, not passing.`,
    };
  }

  // An unchanged image can accumulate multiple prov-<sha> tags across
  // successive releases (each retag-forward stamps a fresh prov tag onto the
  // SAME already-existing digest). Not ambiguous: it's sufficient for ANY one
  // accumulated commit to show no drift vs verifyCommit.
  const resolvedOk = [];
  const resolvedStale = [];
  const resolvedUnresolvable = [];
  for (const provTag of provTags) {
    const candidateCommit = await resolveProvenanceCommit(provTag.replace(/^prov-/, ""), { cwd: cfg.repoRoot, git });
    if (!candidateCommit) {
      resolvedUnresolvable.push(provTag);
      continue;
    }
    const quiet = await git.diffIsQuiet(candidateCommit, verifyCommit, paths, { cwd: cfg.repoRoot });
    if (quiet) {
      resolvedOk.push(candidateCommit);
    } else {
      resolvedStale.push(candidateCommit);
    }
  }

  const tagDisplay = liveState.tag || "<digest-only>";
  if (resolvedOk.length > 0) {
    const resolvedCommit = resolvedOk[0];
    const extraNote =
      provTags.length > 1
        ? ` (${provTags.length} prov tags accumulated on this unchanged digest across releases; using ${resolvedCommit.slice(0, 12)})`
        : "";
    return {
      status: "ok",
      message: `${label}: ${liveState.podCount} live pod(s) run ${image}:${tagDisplay} at ${liveState.digest.slice(0, 19)}, provably built from ${resolvedCommit.slice(0, 12)}; no drift in watched paths vs ${verifyCommit.slice(0, 12)}${extraNote}`,
    };
  }

  if (resolvedStale.length > 0) {
    return {
      status: "fail",
      message: `${label}: ${liveState.podCount} live pod(s) run ${image}:${tagDisplay} at ${liveState.digest.slice(0, 19)}, built from ${resolvedStale[0].slice(0, 12)}, but watched paths changed since then vs ${verifyCommit.slice(0, 12)} -- STALE IMAGE (this is exactly the #251 failure mode). Re-run the build step with FORCE_REBUILD=true for this image.`,
    };
  }

  return {
    status: "fail",
    message: `${label}: none of the ${provTags.length} prov tag(s) for live digest ${liveState.digest.slice(0, 19)} resolve in local git history (shallow clone or rewritten history?): ${resolvedUnresolvable.join(", ")}`,
  };
}

const LABELS = Object.freeze({
  "agentweaver-api": "api",
  "agentweaver-frontend": "frontend",
  "agentweaver-mcp": "mcp",
  "agentweaver-agent-host": "agent-host",
});

/**
 * Main entry point: verifies all 4 images' live provenance against
 * `cfg.VERIFY_GIT_REF` (defaults to 'HEAD' -- see the module header bugfix
 * note; never default this to IMAGE_TAG). Throws if the ref does not
 * resolve, or if any image fails verification (mirrors the bash script's
 * `[[ "${FAIL}" -eq 0 ]]` exit-code contract).
 *
 * @param {Record<string, unknown>} cfg Resolved variables.mjs output (ACR_NAME, NAMESPACE, ...).
 * @param {{ exec?: typeof execDefault, git?: typeof gitDefault, kubectl?: typeof kubectlDefault }} [deps]
 */
export async function run(cfg, deps = {}) {
  const exec = deps.exec ?? execDefault;
  const git = deps.git ?? gitDefault;
  const kubectl = deps.kubectl ?? kubectlDefault;
  const repoRoot = cfg.repoRoot ?? DEFAULT_REPO_ROOT;
  const resolvedCfg = { ...cfg, repoRoot };

  const verifyGitRef = resolvedCfg.VERIFY_GIT_REF || "HEAD";
  const verifyCommit = await git.revParseCommit(verifyGitRef, { cwd: repoRoot });
  if (!verifyCommit) {
    throw new Error(
      `VERIFY_GIT_REF='${verifyGitRef}' does not resolve to a commit in this repository. This is usually because ` +
        "VERIFY_GIT_REF was derived from IMAGE_TAG (a VERSION-file semver string), which is not necessarily a " +
        "git tag/ref. Pass an explicit, resolvable commit/ref via VERIFY_GIT_REF, or leave it unset to default to HEAD.",
    );
  }

  log.section(`Image provenance verification (against ${verifyGitRef} = ${verifyCommit.slice(0, 12)})`);

  const results = [];
  for (const imageSpec of IMAGES) {
    const label = LABELS[imageSpec.name] ?? imageSpec.name;
    const result = await verifyImage(label, imageSpec.name, imageSpec.watchedPaths, verifyCommit, resolvedCfg, {
      exec,
      git,
      kubectl,
    });
    results.push({ image: imageSpec.name, ...result });
    if (result.status === "ok") {
      log.ok(result.message);
    } else {
      log.error(result.message);
    }
  }

  const passed = results.filter((r) => r.status === "ok").length;
  const failed = results.length - passed;

  log.section(`PROVENANCE VERIFICATION SUMMARY: ${passed} passed, ${failed} failed`);
  if (failed === 0) {
    log.info(" ALL IMAGES VERIFIED AGAINST SOURCE");
  } else {
    log.info(" SOME IMAGES FAILED PROVENANCE CHECK -- see output above");
  }

  if (failed > 0) {
    throw new Error(`image provenance verification failed for ${failed} image(s): ${results.filter((r) => r.status !== "ok").map((r) => r.image).join(", ")}`);
  }

  return { verifyCommit, results };
}
