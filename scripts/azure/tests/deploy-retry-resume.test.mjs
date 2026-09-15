// deploy-retry-resume.test.mjs -- Covers the transient-failure retry layer and
// the stage checkpoint/resume behaviour of a release deployment.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { isTransientExecError, withRetry } from "../lib/retry.mjs";
import {
  checkpointKey,
  clearCheckpoint,
  completedStage,
  loadCheckpoint,
  newCheckpoint,
  recordStage,
  saveCheckpoint,
} from "../lib/deploy-checkpoint.mjs";
import { parseArgs, run } from "../deploy-from-release.mjs";
import { retagImage } from "../steps/20-build-push-images.mjs";

const log = {
  info() {}, section() {}, field() {}, ok() {}, skip() {}, warn() {},
  error() {}, debug() {}, command() {},
};

const noSleep = async () => {};

function scratchDir(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

// --- classification -------------------------------------------------------

test("isTransientExecError recognises the failures that broke real deployments", () => {
  const connectionReset = new Error(
    "ConnectionResetError(10054, 'An existing connection was forcibly closed by the remote host')",
  );
  assert.equal(isTransientExecError(connectionReset), true);

  const timeout = Object.assign(new Error("Command timed out after 60000ms"), {
    name: "ExecTimeoutError",
  });
  assert.equal(isTransientExecError(timeout), true);

  assert.equal(isTransientExecError({ stderr: "Too Many Requests" }), true);
  assert.equal(isTransientExecError({ stderr: "503 Service Unavailable" }), true);
});

test("isTransientExecError does not retry deterministic failures", () => {
  assert.equal(isTransientExecError(new Error("ResourceNotFound: registry 'nope' not found")), false);
  assert.equal(isTransientExecError(new Error("AuthenticationFailed: invalid credentials")), false);
  assert.equal(isTransientExecError(null), false);
});

test("isTransientExecError is not fooled by substrings of unrelated text", () => {
  // "ResourceNotFound" contains "enotfound"; a naive substring match would
  // retry a permanent error three times before failing.
  assert.equal(isTransientExecError(new Error("ResourceNotFound")), false);
  // A digest can contain any status-code-looking run of digits.
  assert.equal(
    isTransientExecError(new Error(`manifest sha256:${"503".repeat(10)}429500 is unknown`)),
    false,
  );
  assert.equal(isTransientExecError(new Error("TagAlreadyExists")), false);
});

// --- withRetry ------------------------------------------------------------

test("withRetry retries a transient failure and reports the eventual success", async () => {
  let attempts = 0;
  const retries = [];
  const result = await withRetry(
    async () => {
      attempts += 1;
      if (attempts < 3) throw new Error("Connection aborted");
      return "ok";
    },
    { attempts: 3, sleep: noSleep, onRetry: (info) => retries.push(info.attempt) },
  );

  assert.equal(result, "ok");
  assert.equal(attempts, 3);
  assert.deepEqual(retries, [1, 2]);
});

test("withRetry fails fast on a deterministic error without burning attempts", async () => {
  let attempts = 0;
  await assert.rejects(
    withRetry(
      async () => {
        attempts += 1;
        throw new Error("ResourceNotFound");
      },
      { attempts: 5, sleep: noSleep },
    ),
    /ResourceNotFound/,
  );
  assert.equal(attempts, 1, "a non-transient failure must not be retried");
});

test("withRetry surfaces the last error once attempts are exhausted", async () => {
  let attempts = 0;
  await assert.rejects(
    withRetry(
      async () => {
        attempts += 1;
        throw new Error(`Connection aborted (try ${attempts})`);
      },
      { attempts: 3, sleep: noSleep },
    ),
    /try 3/,
  );
  assert.equal(attempts, 3);
});

test("withRetry backoff is bounded and jittered", async () => {
  const delays = [];
  await assert.rejects(
    withRetry(
      async () => { throw new Error("timed out"); },
      {
        attempts: 5,
        baseDelayMs: 1_000,
        maxDelayMs: 4_000,
        random: () => 1,
        sleep: async (ms) => { delays.push(ms); },
      },
    ),
    /timed out/,
  );
  assert.deepEqual(delays, [1_000, 2_000, 4_000, 4_000], "delay must double then clamp at maxDelayMs");
});

// --- ACR call sites -------------------------------------------------------

test("retagImage retries a transient ACR import instead of failing the deployment", async () => {
  let calls = 0;
  const exec = {
    isDryRun: () => false,
    capture: async () => {
      calls += 1;
      if (calls === 1) {
        throw new Error("ConnectionResetError(10054, 'forcibly closed by the remote host')");
      }
      return { code: 0, stdout: "" };
    },
  };

  await retagImage(
    "agentweaver-api",
    "v1.2.3",
    "latest-release",
    { ACR_NAME: "acr", RESOURCE_GROUP: "rg", ACR_LOGIN_SERVER: "acr.azurecr.io" },
    { exec },
  );

  assert.equal(calls, 2, "the transient import failure should have been retried");
});

test("retagImage still fails on a deterministic ACR error", async () => {
  let calls = 0;
  const exec = {
    isDryRun: () => false,
    capture: async () => {
      calls += 1;
      throw new Error("ResourceNotFound: source image does not exist");
    },
  };

  await assert.rejects(
    retagImage(
      "agentweaver-api",
      "v1.2.3",
      "latest-release",
      { ACR_NAME: "acr", RESOURCE_GROUP: "rg", ACR_LOGIN_SERVER: "acr.azurecr.io" },
      { exec },
    ),
    /ResourceNotFound/,
  );
  assert.equal(calls, 1);
});

// --- checkpoint store -----------------------------------------------------

test("checkpointKey separates releases and deployment targets", () => {
  const base = {
    tag: "v1.2.3",
    subscriptionId: "sub",
    resourceGroup: "rg",
    acrName: "acr",
    clusterName: "aks",
    namespace: "agentweaver",
    imageSource: "ghcr",
  };
  const key = checkpointKey(base);
  assert.notEqual(key, checkpointKey({ ...base, tag: "v1.2.4" }));
  assert.notEqual(key, checkpointKey({ ...base, resourceGroup: "other-rg" }));
  assert.notEqual(key, checkpointKey({ ...base, imageSource: "acr-build" }));
  assert.equal(key, checkpointKey({ ...base }), "the same target must reuse the same key");
  assert.match(key, /^v1\.2\.3-[0-9a-f]{16}$/);
});

test("checkpoint state round-trips and a corrupt file is ignored", () => {
  const dir = scratchDir("deploy-checkpoint-");
  try {
    const key = checkpointKey({ tag: "v1.2.3", resourceGroup: "rg" });
    assert.equal(loadCheckpoint(key, { dir }), null);

    const digests = { "agentweaver-agent-host": `sha256:${"a".repeat(64)}` };
    saveCheckpoint(key, recordStage(newCheckpoint({ tag: "v1.2.3", key }), "build", { expectedImageDigests: digests }), { dir });

    const loaded = loadCheckpoint(key, { dir });
    assert.deepEqual(completedStage(loaded, "build").result.expectedImageDigests, digests);
    assert.equal(completedStage(loaded, "deploy"), null);

    fs.writeFileSync(path.join(dir, `${key}.json`), "{not json", "utf8");
    assert.equal(loadCheckpoint(key, { dir }), null, "a corrupt checkpoint must never block a deploy");

    clearCheckpoint(key, { dir });
    assert.equal(loadCheckpoint(key, { dir }), null);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// --- orchestration --------------------------------------------------------

test("parseArgs exposes --resume/--restart and rejects the contradictory pair", () => {
  assert.equal(parseArgs(["v1.2.3", "--resume"]).resume, true);
  assert.equal(parseArgs(["v1.2.3", "--restart"]).restart, true);
  assert.throws(
    () => parseArgs(["v1.2.3", "--resume", "--restart"]),
    /mutually exclusive/,
  );
});

function fakeExec() {
  return {
    setDryRun() {},
    async run() { return { code: 0, stdout: "" }; },
    async capture(cmd, args) {
      if (cmd === "git" && args[0] === "tag") return { code: 0, stdout: "v1.2.3\nv1.2.2\n" };
      if (cmd === "kubectl") return { code: 1, stdout: "", json: null };
      return { code: 0, stdout: "" };
    },
  };
}

function deployOpts({ dir, argv, order, failVerify = false, buildResult }) {
  return {
    argv,
    repoRoot: "/repo",
    exec: fakeExec(),
    log,
    kubectl: { async currentImageTag() { return null; } },
    validatedRelease: { tag: "v1.2.3", version: "1.2.3", commit: "abc" },
    resolveVariables: async () => ({
      IMAGE_TAG: "v1.2.3",
      ACR_NAME: "acr",
      ACR_LOGIN_SERVER: "acr.azurecr.io",
      RESOURCE_GROUP: "rg",
      NAMESPACE: "agentweaver",
    }),
    resolveGitHubRepository: async () => ({ owner: "sabbour", repo: "agentweaver" }),
    checkpointIo: { dir },
    env: {},
    steps: {
      buildImages: {
        run: async () => {
          order.push("build");
          return buildResult ?? { expectedImageDigests: { "agentweaver-agent-host": `sha256:${"a".repeat(64)}` } };
        },
      },
      deployStep: { run: async () => { order.push("deploy"); return { applied: true }; } },
      verifyProvenance: {
        run: async () => {
          order.push("provenance");
          if (failVerify) throw new Error("provenance verification failed");
          return { results: [{ status: "ok" }] };
        },
      },
      verifyStep: { run: async () => { order.push("health"); return { ok: true, pass: 1, fail: 0 }; } },
    },
  };
}

test("a failed deployment checkpoints its completed stages and --resume skips them", async () => {
  const dir = scratchDir("deploy-resume-");
  try {
    // First run: build + deploy succeed, provenance verification fails.
    const firstOrder = [];
    await assert.rejects(
      run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr"], order: firstOrder, failVerify: true })),
      /provenance verification failed/,
    );
    assert.deepEqual(firstOrder, ["build", "deploy", "provenance"]);

    // Second run with --resume: the expensive stages must not repeat.
    const resumedOrder = [];
    const result = await run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr", "--resume"], order: resumedOrder }));

    assert.equal(result.ok, true);
    assert.deepEqual(
      resumedOrder,
      ["provenance", "health"],
      "resume must skip build/deploy but always re-verify",
    );
    // The AgentHost digest resolved by the skipped build stage must survive.
    assert.equal(
      result.build.expectedImageDigests["agentweaver-agent-host"],
      `sha256:${"a".repeat(64)}`,
    );
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("a successful deployment clears its checkpoint so the next run is complete", async () => {
  const dir = scratchDir("deploy-resume-clear-");
  try {
    const firstOrder = [];
    const first = await run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr"], order: firstOrder }));
    assert.equal(first.ok, true);
    assert.deepEqual(fs.readdirSync(dir), [], "a verified deployment must leave no checkpoint behind");

    const secondOrder = [];
    await run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr", "--resume"], order: secondOrder }));
    assert.deepEqual(
      secondOrder,
      ["build", "deploy", "provenance", "health"],
      "--resume without a checkpoint must run every stage",
    );
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("--restart discards an existing checkpoint and re-runs every stage", async () => {
  const dir = scratchDir("deploy-restart-");
  try {
    const firstOrder = [];
    await assert.rejects(
      run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr"], order: firstOrder, failVerify: true })),
      /provenance verification failed/,
    );

    const restartOrder = [];
    const result = await run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr", "--restart"], order: restartOrder }));
    assert.equal(result.ok, true);
    assert.deepEqual(restartOrder, ["build", "deploy", "provenance", "health"]);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("a dry run never writes checkpoint state", async () => {
  const dir = scratchDir("deploy-dryrun-");
  try {
    const order = [];
    const result = await run(deployOpts({ dir, argv: ["v1.2.3", "--image-source", "ghcr", "--dry-run"], order }));
    assert.equal(result.ok, true);
    assert.equal(fs.existsSync(dir) ? fs.readdirSync(dir).length : 0, 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// --- timeout actually terminates the process tree --------------------------

// Windows-only: this reproduces the exact mechanism that hung deployments for
// hours. `az` is a `.cmd` shim run through `cmd.exe`; signalling only the
// wrapper leaves the real process alive holding the inherited stdout pipe, so
// the capture promise never settles and `timeoutMs` has no effect.
test("a timed-out .cmd wrapper does not leave its grandchild running", { skip: process.platform !== "win32" }, async () => {
  const exec = await import("../lib/exec.mjs");
  const { execSync } = await import("node:child_process");

  const dir = scratchDir("exec-treekill-");
  try {
    const marker = path.join(dir, "pid.txt");
    const shim = path.join(dir, "slow.cmd");
    fs.writeFileSync(
      shim,
      "@echo off\r\nnode -e \"require('fs').writeFileSync(process.argv[1], String(process.pid)); setTimeout(()=>{}, 120000)\" " +
        `"${marker.replace(/\\/g, "\\\\")}"\r\n`,
      "utf8",
    );

    await assert.rejects(
      exec.capture(shim, [], { timeoutMs: 3_000 }),
      (error) => error.name === "ExecTimeoutError",
      "the timeout must fire instead of hanging on the inherited pipe",
    );

    await new Promise((resolve) => setTimeout(resolve, 1_500));
    const pid = fs.existsSync(marker) ? fs.readFileSync(marker, "utf8").trim() : "";
    assert.notEqual(pid, "", "the grandchild should have recorded its pid");

    let alive;
    try {
      alive = execSync(`tasklist /FI "PID eq ${pid}" /NH`, { encoding: "utf8" }).includes(pid);
    } catch {
      alive = false;
    }
    if (alive) {
      try { execSync(`taskkill /pid ${pid} /t /f`, { stdio: "ignore" }); } catch { /* best-effort cleanup */ }
    }
    assert.equal(alive, false, "the timed-out command leaked a live grandchild process");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
