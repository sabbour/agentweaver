// upgrade.test.mjs -- Tests for upgrade.mjs: dirty-tree rejection (+
// allowDirty override), tag minting (HEAD short SHA, never the VERSION-
// derived tag), the orchestration call sequence (20 -> 25 -> 30 -> warm-pool
// wait), and the warm-pool wait/verify logic (not-ready-then-ready polling,
// digest/tag match and mismatch). All collaborators are injected fakes -- no
// real git/kubectl/az/exec calls.

import test from "node:test";
import assert from "node:assert/strict";
import {
  run,
  isWorkingTreeDirty,
  mintUpgradeTag,
  getWarmPoolStatus,
  waitForWarmPoolReady,
  verifyWarmPoolImage,
  DirtyWorkingTreeError,
  WARM_POOL_NAME,
  WARM_POOL_POD_SELECTOR,
} from "../upgrade.mjs";

const CFG = Object.freeze({
  RESOURCE_GROUP: "agentweaver-rg",
  CLUSTER_NAME: "agentweaver-aks-2",
  ACR_NAME: "agentweaverregistry",
  NAMESPACE: "agentweaver",
  ACR_LOGIN_SERVER: "agentweaverregistry.azurecr.io",
  IMAGE_TAG: "v0.9.71",
  AGENTHOST_IMAGE_TAG: "v0.9.71",
  repoRoot: "C:\\fake\\repo",
});

function noopLog() {
  const calls = [];
  const rec = (type) => (msg) => calls.push({ type, msg });
  return {
    calls,
    info: rec("info"),
    section: rec("section"),
    field: (label, value) => calls.push({ type: "field", label, value }),
    ok: rec("ok"),
    skip: rec("skip"),
    warn: rec("warn"),
    error: rec("error"),
    debug: rec("debug"),
    command: rec("command"),
  };
}

// -------------------- isWorkingTreeDirty / mintUpgradeTag --------------------

test("isWorkingTreeDirty: true when `git status --porcelain` reports changes", async () => {
  const capture = async () => ({ stdout: " M scripts/azure/upgrade.mjs\n", stderr: "", code: 0 });
  assert.equal(await isWorkingTreeDirty({ cwd: "/repo", capture }), true);
});

test("isWorkingTreeDirty: false on a clean tree (empty porcelain output)", async () => {
  const capture = async () => ({ stdout: "", stderr: "", code: 0 });
  assert.equal(await isWorkingTreeDirty({ cwd: "/repo", capture }), false);
});

test("mintUpgradeTag: mints the HEAD short SHA, never a VERSION-derived semver tag", async () => {
  const git = {
    currentGitSha: async () => ({ full: "a".repeat(40), short: "aaaaaaa" }),
  };
  const tag = await mintUpgradeTag({ cwd: "/repo", git });
  assert.equal(tag, "aaaaaaa");
  assert.notEqual(tag, "v0.9.71"); // never the VERSION-file semver tag
});

test("mintUpgradeTag: throws if HEAD cannot be resolved", async () => {
  const git = { currentGitSha: async () => ({ full: null, short: null }) };
  await assert.rejects(() => mintUpgradeTag({ cwd: "/repo", git }), /HEAD git SHA/);
});

test("mintUpgradeTag: rejects an invalid/short-of-7-chars SHA via validateImageTag", async () => {
  const git = { currentGitSha: async () => ({ full: "abc", short: "abc" }) };
  await assert.rejects(() => mintUpgradeTag({ cwd: "/repo", git }));
});

// -------------------- getWarmPoolStatus / waitForWarmPoolReady --------------------

test("getWarmPoolStatus: found=false when kubectl returns non-zero (CRD/resource absent)", async () => {
  const exec = { capture: async () => ({ code: 1, stdout: "", stderr: "not found", json: null }) };
  const status = await getWarmPoolStatus("agentweaver", { exec });
  assert.equal(status.found, false);
});

test("getWarmPoolStatus: parses spec.replicas/status.readyReplicas from kubectl JSON", async () => {
  const exec = {
    capture: async () => ({
      code: 0,
      stdout: "",
      stderr: "",
      json: { spec: { replicas: 2 }, status: { readyReplicas: 1 } },
    }),
  };
  const status = await getWarmPoolStatus("agentweaver", { exec });
  assert.deepEqual(status, { found: true, readyReplicas: 1, replicas: 2, raw: { spec: { replicas: 2 }, status: { readyReplicas: 1 } } });
});

test("waitForWarmPoolReady: polls not-ready then ready, and resolves once readyReplicas==replicas", async () => {
  let call = 0;
  const responses = [
    { code: 0, stdout: "", stderr: "", json: { spec: { replicas: 2 }, status: { readyReplicas: 0 } } },
    { code: 0, stdout: "", stderr: "", json: { spec: { replicas: 2 }, status: { readyReplicas: 1 } } },
    { code: 0, stdout: "", stderr: "", json: { spec: { replicas: 2 }, status: { readyReplicas: 2 } } },
  ];
  const exec = { capture: async () => responses[Math.min(call++, responses.length - 1)] };
  const sleeps = [];
  const sleep = async (ms) => sleeps.push(ms);
  const log = noopLog();

  const result = await waitForWarmPoolReady("agentweaver", { exec, log, sleep, pollIntervalMs: 3000 });
  assert.equal(result.ready, true);
  assert.equal(result.readyReplicas, 2);
  assert.equal(result.replicas, 2);
  assert.equal(call, 3);
  assert.equal(sleeps.length, 2); // slept between the two not-ready polls
});

test("waitForWarmPoolReady: throws (never deletes pods) on timeout while stuck not-ready", async () => {
  const exec = {
    capture: async () => ({ code: 0, stdout: "", stderr: "", json: { spec: { replicas: 2 }, status: { readyReplicas: 1 } } }),
  };
  const log = noopLog();
  let now = 0;
  const sleep = async (ms) => {
    now += ms;
  };
  await assert.rejects(
    () =>
      waitForWarmPoolReady("agentweaver", {
        exec,
        log,
        sleep,
        timeoutMs: 10,
        pollIntervalMs: 5,
      }),
    /Timed out.*SandboxWarmPool/s,
  );
  // Confirm the rejection message explicitly refuses manual pod deletion.
  try {
    await waitForWarmPoolReady("agentweaver", { exec, log, sleep, timeoutMs: 10, pollIntervalMs: 5 });
    assert.fail("expected rejection");
  } catch (err) {
    assert.match(err.message, /do NOT manually delete pods|Refusing to manually delete pods/i);
  }
});

test("waitForWarmPoolReady: skips cleanly when the SandboxWarmPool/CRD is not found", async () => {
  const exec = { capture: async () => ({ code: 1, stdout: "", stderr: "not found", json: null }) };
  const log = noopLog();
  const result = await waitForWarmPoolReady("agentweaver", { exec, log, sleep: async () => {} });
  assert.equal(result.skipped, true);
  assert.equal(result.ready, true);
});

// -------------------- verifyWarmPoolImage --------------------

test("verifyWarmPoolImage: ok=true when every warm pod runs the expected tag", async () => {
  const kubectl = {
    podStatusForSelector: async (selector, namespace) => {
      assert.equal(selector, WARM_POOL_POD_SELECTOR);
      assert.equal(namespace, "agentweaver");
      return [
        { name: "agentweaver-agent-host-abc", imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:abc1234" },
        { name: "agentweaver-agent-host-def", imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:abc1234" },
      ];
    },
  };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "abc1234", { kubectl, log });
  assert.equal(result.ok, true);
  assert.equal(result.mismatched.length, 0);
});

test("verifyWarmPoolImage: ok=false and lists mismatched pods running a stale digest/tag", async () => {
  const kubectl = {
    podStatusForSelector: async () => [
      { name: "agentweaver-agent-host-abc", imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:abc1234" },
      { name: "agentweaver-agent-host-stale", imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:oldtag9" },
    ],
  };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "abc1234", { kubectl, log });
  assert.equal(result.ok, false);
  assert.equal(result.mismatched.length, 1);
  assert.equal(result.mismatched[0].name, "agentweaver-agent-host-stale");
});

test("verifyWarmPoolImage: no pods found is reported as ok (nothing to mismatch), not a failure", async () => {
  const kubectl = { podStatusForSelector: async () => [] };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "abc1234", { kubectl, log });
  assert.equal(result.ok, true);
  assert.equal(result.pods.length, 0);
});

test("verifyWarmPoolImage: digest-aware comparison treats a retag-forward (same digest, old tag string) as OK, not mismatched", async () => {
  // Found in Phase 7 staging re-verification: agent-host unchanged since the
  // prior release retags forward to a new tag string pointing at the SAME
  // manifest digest. Warm pods that haven't churned yet legitimately still
  // show the OLD tag string. Tag-string comparison alone would falsely flag
  // this as stale; digest comparison must recognize it as correct.
  const kubectl = {
    podStatusForSelector: async () => [
      {
        name: "agentweaver-agent-host-1",
        imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:oldtag9",
        imageId: "agentweaverregistry.azurecr.io/agentweaver-agent-host@sha256:" + "a".repeat(64),
      },
    ],
  };
  const exec = {
    capture: async () => ({ stdout: "sha256:" + "a".repeat(64) + "\n", stderr: "", code: 0 }),
  };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "newtag1", { kubectl, log, exec, acrName: "agentweaverregistry" });
  assert.equal(result.ok, true);
  assert.equal(result.mismatched.length, 0);
});

test("verifyWarmPoolImage: digest-aware comparison still fails a genuinely stale pod (different digest)", async () => {
  const kubectl = {
    podStatusForSelector: async () => [
      {
        name: "agentweaver-agent-host-1",
        imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:oldtag9",
        imageId: "agentweaverregistry.azurecr.io/agentweaver-agent-host@sha256:" + "b".repeat(64),
      },
    ],
  };
  const exec = {
    capture: async () => ({ stdout: "sha256:" + "a".repeat(64) + "\n", stderr: "", code: 0 }),
  };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "newtag1", { kubectl, log, exec, acrName: "agentweaverregistry" });
  assert.equal(result.ok, false);
  assert.equal(result.mismatched.length, 1);
});

test("verifyWarmPoolImage: falls back to tag-string comparison when the ACR digest can't be resolved", async () => {
  const kubectl = {
    podStatusForSelector: async () => [
      { name: "agentweaver-agent-host-1", imageRef: "agentweaverregistry.azurecr.io/agentweaver-agent-host:newtag1" },
    ],
  };
  const exec = { capture: async () => ({ stdout: "", stderr: "not found", code: 1 }) };
  const log = noopLog();
  const result = await verifyWarmPoolImage("agentweaver", "newtag1", { kubectl, log, exec, acrName: "agentweaverregistry" });
  assert.equal(result.ok, true);
});

// -------------------- run() orchestration --------------------

function makeOrchestrationFakes({ dirty = false, warmPoolReplicas = 2, warmPoolReady = 2, imageMatch = true } = {}) {
  const calls = [];
  const exec = {
    capture: async (cmd, args, opts) => {
      calls.push({ type: "capture", cmd, args });
      if (cmd === "git" && args[0] === "status") {
        return { stdout: dirty ? " M file.mjs\n" : "", stderr: "", code: 0 };
      }
      if (cmd === "kubectl" && args[0] === "get" && args[1] === "sandboxwarmpool") {
        return {
          code: 0,
          stdout: "",
          stderr: "",
          json: { spec: { replicas: warmPoolReplicas }, status: { readyReplicas: warmPoolReady } },
        };
      }
      return { stdout: "", stderr: "", code: 0 };
    },
    run: async (cmd, args) => {
      calls.push({ type: "run", cmd, args });
      return { code: 0 };
    },
  };

  const git = {
    currentGitSha: async () => ({ full: "b".repeat(40), short: "bbbbbbb" }),
  };

  const kubectl = {
    podStatusForSelector: async () => [
      {
        name: "agentweaver-agent-host-1",
        imageRef: `agentweaverregistry.azurecr.io/agentweaver-agent-host:${imageMatch ? "bbbbbbb" : "stale99"}`,
      },
    ],
  };

  const buildStep = {
    run: async (cfg) => {
      calls.push({ type: "step20", cfg });
      return { targetCommit: "b".repeat(40), plans: [] };
    },
  };
  const provenanceStep = {
    run: async (cfg) => {
      calls.push({ type: "step25", cfg });
      return { verifyCommit: "b".repeat(40), results: [{ image: "agentweaver-api", status: "ok", message: "ok" }] };
    },
  };
  const deployStep = {
    run: async (cfg) => {
      calls.push({ type: "step30", cfg });
      return { HOST: "agentweaver.example.com" };
    },
  };

  return { calls, exec, git, kubectl, buildStep, provenanceStep, deployStep, log: noopLog(), sleep: async () => {} };
}

test("run(): refuses a dirty working tree by default", async () => {
  const fakes = makeOrchestrationFakes({ dirty: true });
  await assert.rejects(() => run(CFG, fakes), DirtyWorkingTreeError);
});

test("run(): --allow-dirty (allowDirty: true) explicitly opts out of the dirty-tree check", async () => {
  const fakes = makeOrchestrationFakes({ dirty: true });
  const result = await run(CFG, { ...fakes, allowDirty: true });
  assert.equal(result.imageTag, "bbbbbbb");
});

test("run(): mints HEAD short SHA and never reuses cfg.IMAGE_TAG (the VERSION-derived tag)", async () => {
  const fakes = makeOrchestrationFakes();
  const result = await run(CFG, fakes);
  assert.equal(result.imageTag, "bbbbbbb");
  assert.notEqual(result.imageTag, CFG.IMAGE_TAG);
});

test("run(): calls steps in order 20 -> 30 -> 25 -> warm-pool-wait, passing the minted tag through", async () => {
  // 30 (deploy) must run BEFORE 25 (provenance verify): steps/25 is a
  // post-deploy safety net that checks the digest actually running live in
  // the cluster, so it must observe the NEW deployment, not the stale
  // pre-upgrade one. Reordering this was a real bug found in Phase 7 staging
  // verification (running 25 before 30 always reported false STALE IMAGE
  // failures against the still-old live pods).
  const fakes = makeOrchestrationFakes();
  await run(CFG, fakes);

  const stepOrder = fakes.calls.filter((c) => ["step20", "step25", "step30"].includes(c.type)).map((c) => c.type);
  assert.deepEqual(stepOrder, ["step20", "step30", "step25"]);

  const step20Call = fakes.calls.find((c) => c.type === "step20");
  assert.equal(step20Call.cfg.IMAGE_TAG, "bbbbbbb");
  assert.equal(step20Call.cfg.AGENTHOST_IMAGE_TAG, "bbbbbbb");

  const step25Call = fakes.calls.find((c) => c.type === "step25");
  // VERIFY_GIT_REF must be left unset (defaults to HEAD inside steps/25) --
  // never defaulted to IMAGE_TAG here (the historical #251 bug).
  assert.equal(step25Call.cfg.VERIFY_GIT_REF, undefined);

  const warmPoolPoll = fakes.calls.filter((c) => c.type === "capture" && c.cmd === "kubectl" && c.args[1] === "sandboxwarmpool");
  assert.ok(warmPoolPoll.length >= 1);
});

test("run(): succeeds end-to-end when the warm pool becomes ready and runs the expected image", async () => {
  const fakes = makeOrchestrationFakes({ imageMatch: true });
  const result = await run(CFG, fakes);
  assert.equal(result.warmPool.ready, true);
  assert.equal(result.warmPool.imageCheck.ok, true);
});

test("run(): throws when the warm pool is ready but running a mismatched image (never silently succeeds)", async () => {
  const fakes = makeOrchestrationFakes({ imageMatch: false });
  await assert.rejects(() => run(CFG, fakes), /do not run the expected AgentHost tag/);
});

test("run(): never issues a `kubectl delete pod` command during the warm-pool cycle", async () => {
  const fakes = makeOrchestrationFakes();
  await run(CFG, fakes);
  const deletePodCalls = fakes.calls.filter(
    (c) => c.type === "run" && c.cmd === "kubectl" && c.args.includes("delete") && c.args.includes("pod"),
  );
  assert.equal(deletePodCalls.length, 0);
});
