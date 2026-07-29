// build-provenance.test.mjs -- parity + edge-case tests for
// steps/20-build-push-images.mjs and steps/25-verify-image-provenance.mjs,
// with az/git/kubectl fully stubbed (no real Azure/git/kubectl calls).
// Covers the decision-log-flagged risk areas: dirty/shallow/diverged git
// history, missing prov tags, and the VERIFY_GIT_REF/IMAGE_TAG bugfix.

import test from "node:test";
import assert from "node:assert/strict";
import {
  releaseRefForTag,
  pathsChanged,
  planImage,
  buildImage,
  retagImage,
  acrDigestForTag,
  waitForAcrTagDigest,
  stampProvenance,
  run as runBuild,
} from "../steps/20-build-push-images.mjs";
import {
  imageTagFromRef,
  imageDigestFromId,
  liveDigestStateForSelector,
  verifyImage,
  run as runProvenance,
} from "../steps/25-verify-image-provenance.mjs";
import { getImage } from "../image-spec.mjs";

const CFG = Object.freeze({
  RESOURCE_GROUP: "agentweaver-rg",
  ACR_NAME: "agentweaverregistry",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  NAMESPACE: "agentweaver",
  IMAGE_TAG: "v1.2.3",
  AGENTHOST_IMAGE_TAG: "v1.2.3",
  repoRoot: "C:\\fake\\repo",
});

function fakeExec({ captureImpl, runImpl, dryRun = false } = {}) {
  const calls = { capture: [], run: [] };
  return {
    calls,
    isDryRun: () => dryRun,
    async capture(cmd, args, opts) {
      calls.capture.push({ cmd, args, opts });
      if (captureImpl) return captureImpl(cmd, args, opts);
      return { stdout: "", stderr: "", code: 0 };
    },
    async run(cmd, args, opts) {
      calls.run.push({ cmd, args, opts });
      if (runImpl) return runImpl(cmd, args, opts);
      return { code: 0 };
    },
  };
}

// -------------------- releaseRefForTag / pathsChanged (20) --------------------

test("releaseRefForTag: a tag that is a real git ref resolves directly", async () => {
  const git = {
    revParseCommit: async (ref) => (ref === "v1.2.3" ? "a".repeat(40) : null),
  };
  const commit = await releaseRefForTag("v1.2.3", { git });
  assert.equal(commit, "a".repeat(40));
});

test("releaseRefForTag: shallow repository refuses VERSION-based resolution (forces rebuild)", async () => {
  const git = {
    revParseCommit: async () => null,
    isShallowRepository: async () => true,
  };
  const commit = await releaseRefForTag("v1.2.3", { git });
  assert.equal(commit, null);
});

test("releaseRefForTag: resolves via VERSION-history when no direct tag match, single matching commit", async () => {
  const git = {
    revParseCommit: async () => null,
    isShallowRepository: async () => false,
    logAllCommitsForPath: async () => ["c1", "c2"],
    showFileAtCommit: async (commit) => (commit === "c1" ? "1.2.3\n" : "9.9.9\n"),
  };
  const commit = await releaseRefForTag("v1.2.3", { git });
  assert.equal(commit, "c1");
});

test("releaseRefForTag: diverged VERSION history (candidate not an ancestor of newest) refuses to guess", async () => {
  const git = {
    revParseCommit: async () => null,
    isShallowRepository: async () => false,
    logAllCommitsForPath: async () => ["newest", "older-diverged"],
    showFileAtCommit: async () => "1.2.3",
    isAncestor: async () => false, // simulates diverged/poisoned history (#251 hardening)
  };
  const commit = await releaseRefForTag("v1.2.3", { git });
  assert.equal(commit, null, "diverged VERSION-writing commits must force a rebuild, not a guess");
});

test("releaseRefForTag: linear VERSION history (candidate IS an ancestor) resolves to newest", async () => {
  const git = {
    revParseCommit: async () => null,
    isShallowRepository: async () => false,
    logAllCommitsForPath: async () => ["newest", "older-linear"],
    showFileAtCommit: async () => "1.2.3",
    isAncestor: async () => true,
  };
  const commit = await releaseRefForTag("v1.2.3", { git });
  assert.equal(commit, "newest");
});

test("releaseRefForTag: no matching VERSION-history commit returns null", async () => {
  const git = {
    revParseCommit: async () => null,
    isShallowRepository: async () => false,
    logAllCommitsForPath: async () => [],
  };
  assert.equal(await releaseRefForTag("v9.9.9", { git }), null);
});

test("pathsChanged: missing old/new ref is treated as changed (safe default, forces build)", async () => {
  const git = { diffIsQuiet: async () => true };
  assert.equal(await pathsChanged("", "head", ["x"], { git }), true);
  assert.equal(await pathsChanged("old", "", ["x"], { git }), true);
});

test("pathsChanged: delegates to git.diffIsQuiet and negates it", async () => {
  const git = { diffIsQuiet: async (a, b, paths) => paths.includes("apps/Agentweaver.Api") };
  assert.equal(await pathsChanged("old", "new", ["apps/Agentweaver.Api"], { git }), false);
  assert.equal(await pathsChanged("old", "new", ["apps/Agentweaver.Mcp"], { git }), true);
});

// -------------------- planImage build-vs-retag decision (20) --------------------

test("planImage: forces build when FORCE_REBUILD is set even if unchanged", async () => {
  const image = getImage("agentweaver-mcp");
  // Mirrors the bash script: source_commit is resolved unconditionally before
  // the FORCE_REBUILD check, so revParseCommit must still be stubbed here.
  const git = { revParseCommit: async () => "sourcecommit", diffIsQuiet: async () => true }; // would be "unchanged"
  const kubectl = { currentDeploymentTag: async () => "v1.0.0" };
  const plan = await planImage(image, "targetcommit", { ...CFG, FORCE_REBUILD: true }, { git, kubectl });
  assert.equal(plan.action, "build");
});

test("planImage: builds when there is no previously deployed tag", async () => {
  const image = getImage("agentweaver-mcp");
  const kubectl = { currentDeploymentTag: async () => "" };
  const git = {};
  const plan = await planImage(image, "targetcommit", CFG, { git, kubectl });
  assert.equal(plan.action, "build");
  assert.match(plan.reason, /no previous image tag/);
});

test("planImage: builds when the previous tag has no resolvable source commit", async () => {
  const image = getImage("agentweaver-mcp");
  const kubectl = { currentDeploymentTag: async () => "v1.0.0" };
  const git = { revParseCommit: async () => null, isShallowRepository: async () => true };
  const plan = await planImage(image, "targetcommit", CFG, { git, kubectl });
  assert.equal(plan.action, "build");
  assert.match(plan.reason, /no resolvable VERSION commit/);
});

test("planImage: builds when watched paths changed since the previous tag's source commit", async () => {
  const image = getImage("agentweaver-mcp");
  const kubectl = { currentDeploymentTag: async () => "v1.0.0" };
  const git = {
    revParseCommit: async () => "sourcecommit",
    diffIsQuiet: async () => false, // changed
  };
  const plan = await planImage(image, "targetcommit", CFG, { git, kubectl });
  assert.equal(plan.action, "build");
  assert.match(plan.reason, /changed since/);
});

test("planImage: retags when watched paths are unchanged since the previous tag's source commit", async () => {
  const image = getImage("agentweaver-mcp");
  const kubectl = { currentDeploymentTag: async () => "v1.0.0" };
  const git = {
    revParseCommit: async () => "sourcecommit",
    diffIsQuiet: async () => true, // unchanged
  };
  const plan = await planImage(image, "targetcommit", CFG, { git, kubectl });
  assert.equal(plan.action, "retag");
  assert.equal(plan.sourceTag, "v1.0.0");
});

test("planImage: PREVIOUS_IMAGE_TAG overrides the cluster-detected deployed tag", async () => {
  const image = getImage("agentweaver-api");
  const kubectl = { currentDeploymentTag: async () => "v-should-be-ignored" };
  const git = { revParseCommit: async () => "c", diffIsQuiet: async () => true };
  const plan = await planImage(image, "targetcommit", { ...CFG, PREVIOUS_IMAGE_TAG: "v-explicit" }, { git, kubectl });
  assert.equal(plan.sourceTag, "v-explicit");
});

// -------------------- build-arg construction (20) --------------------

test("buildImage: az acr build invocation includes --build-arg IMAGE_TAG and GIT_SHA", async () => {
  const image = getImage("agentweaver-mcp");
  const exec = fakeExec();
  const git = { revParseCommit: async () => "prov-commit-sha", currentGitSha: async () => ({ full: "fullsha" }) };
  // Stub acr digest lookups so stampProvenance succeeds without retries.
  let digestCalls = 0;
  exec.capture = async (cmd, args, opts) => {
    exec.calls.capture.push({ cmd, args, opts });
    digestCalls += 1;
    if (args.includes("show-manifests")) return { stdout: "sha256:" + "a".repeat(64), stderr: "", code: 0 };
    return { stdout: "", stderr: "", code: 0 };
  };
  await buildImage(image, "v1.2.3", "targetcommit", CFG, { exec, git });

  const buildCall = exec.calls.run.find((c) => c.args.includes("build"));
  assert.ok(buildCall, "expected an `az acr build` invocation");
  const argStr = buildCall.args.join(" ");
  assert.match(argStr, /--build-arg IMAGE_TAG=v1\.2\.3/);
  assert.match(argStr, /--build-arg GIT_SHA=targetcommit/);
  assert.ok(digestCalls > 0);
});

test("acrDigestForTag/waitForAcrTagDigest: retries then returns null when the tag never resolves", async () => {
  const exec = fakeExec({ captureImpl: async () => ({ stdout: "", stderr: "", code: 0 }) });
  const sleeps = [];
  const digest = await waitForAcrTagDigest("agentweaver-api", "v1.2.3", CFG, {
    exec,
    sleep: async (ms) => {
      sleeps.push(ms);
    }, // skip real delays in tests
  });
  assert.equal(digest, null);
  assert.deepEqual(sleeps.slice(0, 4), [2000, 4000, 8000, 15000]);
  assert.ok(
    sleeps.reduce((sum, ms) => sum + ms, 0) >= 5 * 60 * 1000,
    "waitForAcrTagDigest must keep polling long enough for ACR's eventual-consistency lag",
  );
});

test("acrDigestForTag: parses the first non-empty tsv line as the digest", async () => {
  const exec = fakeExec({ captureImpl: async () => ({ stdout: "\nsha256:" + "b".repeat(64) + "\n", stderr: "", code: 0 }) });
  const digest = await acrDigestForTag("agentweaver-api", "v1.2.3", CFG, { exec });
  assert.equal(digest, "sha256:" + "b".repeat(64));
});

test("stampProvenance: imports the source digest into prov-<sha> and then locks it read-only", async () => {
  const git = { revParseCommit: async () => "c".repeat(40) };
  const sourceDigest = "sha256:" + "d".repeat(64);
  const exec = fakeExec({
    captureImpl: async (cmd, args) => {
      if (args.includes("show-manifests")) {
        // First lookup (source tag) resolves; second lookup (prov tag,
        // pre-import) does not exist yet; third lookup (prov tag,
        // post-import) resolves to the same digest as the source.
        const showCalls = exec.calls.capture.filter((c) => c.args.includes("show-manifests")).length;
        if (showCalls <= 1) return { stdout: sourceDigest, stderr: "", code: 0 };
        if (showCalls === 2) return { stdout: "", stderr: "", code: 0 };
        return { stdout: sourceDigest, stderr: "", code: 0 };
      }
      return { stdout: "", stderr: "", code: 0 };
    },
  });

  const result = await stampProvenance("agentweaver-api", "v1.2.3", "targetcommit", CFG, {
    exec,
    git,
    sleep: async () => {},
  });

  assert.equal(result.tag, `prov-${"c".repeat(40)}`);
  const importCall = exec.calls.capture.find((c) => c.args.includes("import"));
  assert.ok(importCall, "expected an `az acr import` invocation to stamp the provenance tag");
  const lockCall = exec.calls.capture.find((c) => c.args.includes("update") && c.args.includes("repository"));
  assert.ok(lockCall, "expected an `az acr repository update` invocation to lock the provenance tag");
  assert.ok(lockCall.args.includes("--write-enabled"), "lock call must set --write-enabled");
  assert.ok(lockCall.args.includes("false"), "lock call must set --write-enabled false");
  assert.ok(lockCall.args.includes(`agentweaver-api:prov-${"c".repeat(40)}`), "lock call must target the stamped provenance tag");
});

test("stampProvenance: is a no-op when the provenance tag already points at the expected digest", async () => {
  const git = { revParseCommit: async () => "e".repeat(40) };
  const digest = "sha256:" + "f".repeat(64);
  const exec = fakeExec({
    captureImpl: async () => ({ stdout: digest, stderr: "", code: 0 }),
  });

  await stampProvenance("agentweaver-api", "v1.2.3", "targetcommit", CFG, { exec, git, sleep: async () => {} });

  const importCall = exec.calls.capture.find((c) => c.args.includes("import"));
  assert.equal(importCall, undefined, "must not re-import an already-stamped, matching provenance tag");
  const lockCall = exec.calls.capture.find((c) => c.args.includes("update") && c.args.includes("repository"));
  assert.equal(lockCall, undefined, "must not attempt to re-lock a tag that was never (re-)imported this run");
});

test("retagImage: skips when source and target tags are identical", async () => {
  const exec = fakeExec();
  await retagImage("agentweaver-api", "v1.2.3", "v1.2.3", CFG, { exec });
  assert.equal(exec.calls.capture.length, 0, "must not invoke az acr import when source==target");
});

// -------------------- provenance helpers (25) --------------------

test("imageTagFromRef / imageDigestFromId parse container status fields", () => {
  assert.equal(imageTagFromRef("agentweaverregistry.azurecr.io/agentweaver-api:v1.2.3"), "v1.2.3");
  assert.equal(imageTagFromRef("no-tag-ref"), "");
  assert.equal(
    imageDigestFromId("agentweaverregistry.azurecr.io/agentweaver-api@sha256:" + "c".repeat(64)),
    "sha256:" + "c".repeat(64),
  );
  assert.equal(imageDigestFromId("garbage"), null);
});

test("liveDigestStateForSelector: fails closed when no desired-replica count is known and pods aren't ephemeral", async () => {
  const kubectl = { podStatusForSelector: async () => [] };
  const state = await liveDigestStateForSelector("api", "app=agentweaver-api", "", false, { namespace: "agentweaver", kubectl });
  assert.equal(state.ok, false);
  assert.match(state.failReason, /could not determine desired replica count/);
});

test("liveDigestStateForSelector: agent-host with zero pods is skipped (ephemeral pods allowed)", async () => {
  const kubectl = { podStatusForSelector: async () => [] };
  const state = await liveDigestStateForSelector("agent-host", "app=agentweaver-sandbox", "", true, {
    namespace: "agentweaver",
    kubectl,
  });
  assert.equal(state.ok, true);
  assert.equal(state.skipped, true);
});

test("liveDigestStateForSelector: mixed digests across replicas fails closed", async () => {
  const kubectl = {
    podStatusForSelector: async () => [
      { name: "p1", phase: "Running", ready: "true", imageRef: "img:v1", imageId: "img@sha256:" + "1".repeat(64) },
      { name: "p2", phase: "Running", ready: "true", imageRef: "img:v1", imageId: "img@sha256:" + "2".repeat(64) },
    ],
  };
  const state = await liveDigestStateForSelector("api", "app=agentweaver-api", "2", false, { namespace: "agentweaver", kubectl });
  assert.equal(state.ok, false);
  assert.match(state.failReason, /mixed live digests/);
});

test("liveDigestStateForSelector: excludes terminating (deletionTimestamp) old-generation pods for NON-ephemeral selectors too, not just fails closed", async () => {
  // Found in Phase 7 staging re-verification: `kubectl rollout status` only
  // waits for the NEW ReplicaSet to become available; the OLD ReplicaSet's
  // pods terminate asynchronously afterward (standard k8s behavior). Since
  // provenance now runs immediately post-deploy (see deploy-from-local.mjs reorder
  // fix), a leftover terminating old pod must NOT be treated as an
  // unavailable replica -- it must simply be excluded from the live count.
  const digest = "sha256:" + "3".repeat(64);
  const kubectl = {
    podStatusForSelector: async () => [
      { name: "new-1", phase: "Running", ready: "true", imageRef: "img:v2", imageId: `img@${digest}` },
      { name: "new-2", phase: "Running", ready: "true", imageRef: "img:v2", imageId: `img@${digest}` },
      { name: "old-terminating", phase: "Running", ready: "true", deletionTimestamp: "2026-07-19T00:00:00Z", imageRef: "img:v1", imageId: "img@sha256:" + "9".repeat(64) },
    ],
  };
  const state = await liveDigestStateForSelector("api", "app=agentweaver-api", "2", false, { namespace: "agentweaver", kubectl });
  assert.equal(state.ok, true);
  assert.equal(state.digest, digest);
  assert.equal(state.podCount, 2);
});

test("verifyImage: fails when no prov-<sha> tag exists for the live digest (unstamped/legacy image)", async () => {
  const digest = "sha256:" + "d".repeat(64);
  const kubectl = {
    desiredDeploymentReplicas: async () => "1",
    podStatusForSelector: async () => [
      { name: "p1", phase: "Running", ready: "true", imageRef: "img:v1", imageId: `img@${digest}` },
    ],
  };
  const exec = fakeExec({ captureImpl: async () => ({ stdout: "", stderr: "", code: 0 }) }); // no prov tags found
  const git = {};
  const result = await verifyImage("api", "agentweaver-api", getImage("agentweaver-api").watchedPaths, "verifycommit", CFG, {
    exec,
    git,
    kubectl,
  });
  assert.equal(result.status, "fail");
  assert.match(result.message, /no prov-<sha> tag found/);
});

test("verifyImage: STALE result when the resolved provenance commit has watched-path drift", async () => {
  const digest = "sha256:" + "e".repeat(64);
  const kubectl = {
    desiredDeploymentReplicas: async () => "1",
    podStatusForSelector: async () => [
      { name: "p1", phase: "Running", ready: "true", imageRef: "img:v1", imageId: `img@${digest}` },
    ],
  };
  const exec = fakeExec({
    captureImpl: async (cmd, args) => {
      if (args.includes("show-manifests")) return { stdout: `prov-${"a".repeat(40)}`, stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const git = {
    resolveCommitish: async (c) => c,
    diffIsQuiet: async () => false, // watched paths changed since the provenance commit -> stale
  };
  const result = await verifyImage("api", "agentweaver-api", getImage("agentweaver-api").watchedPaths, "verifycommit", CFG, {
    exec,
    git,
    kubectl,
  });
  assert.equal(result.status, "fail");
  assert.match(result.message, /STALE IMAGE/);
});

test("verifyImage: OK result when the resolved provenance commit shows no watched-path drift", async () => {
  const digest = "sha256:" + "f".repeat(64);
  const kubectl = {
    desiredDeploymentReplicas: async () => "1",
    podStatusForSelector: async () => [
      { name: "p1", phase: "Running", ready: "true", imageRef: "img:v1", imageId: `img@${digest}` },
    ],
  };
  const exec = fakeExec({
    captureImpl: async (cmd, args) => {
      if (args.includes("show-manifests")) return { stdout: `prov-${"a".repeat(40)}`, stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const git = { resolveCommitish: async (c) => c, diffIsQuiet: async () => true };
  const result = await verifyImage("api", "agentweaver-api", getImage("agentweaver-api").watchedPaths, "verifycommit", CFG, {
    exec,
    git,
    kubectl,
  });
  assert.equal(result.status, "ok");
  assert.match(result.message, /provably built from/);
});

test("verifyImage: unresolvable prov tag (shallow clone/rewritten history) fails with a clear reason", async () => {
  const digest = "sha256:" + "0".repeat(64);
  const kubectl = {
    desiredDeploymentReplicas: async () => "1",
    podStatusForSelector: async () => [
      { name: "p1", phase: "Running", ready: "true", imageRef: "img:v1", imageId: `img@${digest}` },
    ],
  };
  const exec = fakeExec({
    captureImpl: async (cmd, args) => {
      if (args.includes("show-manifests")) return { stdout: `prov-${"1".repeat(40)}`, stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  const git = { resolveCommitish: async () => null };
  const result = await verifyImage("api", "agentweaver-api", getImage("agentweaver-api").watchedPaths, "verifycommit", CFG, {
    exec,
    git,
    kubectl,
  });
  assert.equal(result.status, "fail");
  assert.match(result.message, /resolve in local git history/);
});

// -------------------- VERIFY_GIT_REF bugfix preserved (25 via run()) --------------------

test("run() (provenance): defaults VERIFY_GIT_REF to HEAD, never to IMAGE_TAG", async () => {
  const seenRefs = [];
  const git = {
    revParseCommit: async (ref) => {
      seenRefs.push(ref);
      return "headcommit1234567890";
    },
    resolveCommitish: async (c) => c,
    diffIsQuiet: async () => true,
  };
  const kubectl = {
    desiredDeploymentReplicas: async () => "",
    podStatusForSelector: async () => [], // no pods anywhere -> all "fail: no pods" except agent-host (ephemeral-allowed)
  };
  const exec = fakeExec();
  // cfg deliberately has no VERIFY_GIT_REF set, and IMAGE_TAG is a non-ref semver string.
  await assert.rejects(() => runProvenance({ ...CFG }, { exec, git, kubectl }));
  assert.equal(seenRefs[0], "HEAD", "must resolve HEAD, not IMAGE_TAG, when VERIFY_GIT_REF is unset");
});

test("run() (provenance): throws a clear, actionable error when an explicit VERIFY_GIT_REF does not resolve", async () => {
  const git = { revParseCommit: async () => null };
  const kubectl = {};
  const exec = fakeExec();
  await assert.rejects(
    () => runProvenance({ ...CFG, VERIFY_GIT_REF: "v0.9.63" }, { exec, git, kubectl }),
    /does not resolve to a commit/,
  );
});

// -------------------- run() (20): overall build orchestration --------------------

test("run() (build): aggregates a failure from one image without silently swallowing it", async () => {
  const git = {
    revParseCommit: async () => "targetcommit1234567890",
    revParseHead: async () => "targetcommit1234567890",
    diffIsQuiet: async () => false,
    currentGitSha: async () => ({ full: "targetcommit1234567890" }),
  };
  const kubectl = {
    currentDeploymentTag: async () => "",
    currentAgentHostTag: async () => "",
  };
  let buildCount = 0;
  const exec = fakeExec({
    runImpl: async (cmd, args) => {
      if (args.includes("build")) {
        buildCount += 1;
        if (args.includes("agentweaver-mcp:v1.2.3")) throw new Error("simulated az acr build failure");
      }
      return { code: 0 };
    },
    captureImpl: async (cmd, args) => {
      if (args.includes("show-manifests")) return { stdout: "sha256:" + "9".repeat(64), stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    },
  });
  await assert.rejects(() => runBuild({ ...CFG }, { exec, git, kubectl }), /agentweaver-mcp/);
  assert.ok(buildCount >= 4, "expected all 4 images to attempt a build (no previously deployed tag)");
});
