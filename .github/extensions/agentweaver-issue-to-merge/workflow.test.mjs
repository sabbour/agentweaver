import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { runIssueToMerge } from "./workflow.mjs";
import "../agentweaver-workflow-canvas/canvas.test.mjs";

const args = {
  task: "Implement issue and regression coverage",
  issueNumber: 1742,
  milestone: "Squad",
  labels: ["type:chore", "area:workflows"],
};

function fixture(t) {
  const cwd = mkdtempSync(join(tmpdir(), "agentweaver-workflow-"));
  t.after(() => rmSync(cwd, { recursive: true, force: true }));
  const git = (...parts) => execFileSync("git", parts, { cwd, encoding: "utf8", windowsHide: true }).trim();
  git("init", "-b", "feature");
  git("config", "user.email", "workflow@example.invalid");
  git("config", "user.name", "Workflow Test");
  writeFileSync(join(cwd, "sample.txt"), "start\n");
  git("add", "sample.txt");
  git("commit", "-qm", "initial");
  return { cwd, git };
}

function context(options = {}) {
  const journal = new Map();
  const calls = [];
  const ctx = {
    args: { ...args, ...options.args },
    runId: "test-run",
    phase: () => {},
    log: () => {},
    step: async (key, producer, stepOptions) => {
      if (!stepOptions?.volatile && journal.has(key)) return journal.get(key);
      const value = await producer();
      if (!stepOptions?.volatile) journal.set(key, value);
      return value;
    },
    parallel: (thunks) => Promise.all(thunks.map((thunk) => thunk().catch(() => null))),
    agent: async (prompt, agentOptions) => {
      calls.push({ prompt, ...agentOptions });
      return options.agent?.(prompt, agentOptions, calls.length);
    },
  };
  return { ctx, calls, journal };
}

test("rehearsal visits eight phases without git, agents or mutations", async () => {
  const { ctx, calls } = context();
  const result = await runIssueToMerge(ctx, "nonexistent-worktree");
  assert.equal(result.status, "rehearsed");
  assert.equal(result.phases.length, 8);
  assert.equal(result.agentCalls, 0);
  assert.equal(result.mutations, false);
  assert.deepEqual(calls, []);
});

test("invalid arguments are rejected before any workflow activity", async () => {
  for (const invalid of [
    { issueNumber: 0 }, { issueNumber: 1.5 }, { issueNumber: Number.MAX_SAFE_INTEGER + 1 },
    { labels: ["type:chore"] }, { milestone: " " }, { task: "" },
    { baseBranch: "-bad" }, { repository: "bad" }, { mode: "ship" },
  ]) {
    const { ctx, calls, journal } = context({ args: invalid });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Require|Invalid/);
    assert.equal(calls.length, 0);
    assert.equal(journal.size, 0);
  }
});

function deliveryAgent({ cwd, git }, options = {}) {
  const tree = () => git("write-tree");
  let reviewedTree;
  let admissionCount = 0;
  let publicationCount = 0;
  const agent = async (_prompt, { label }) => {
    if (label === "v3:scope") return { status: "passed", treeSha: tree(), evidence: "issue scoped" };
    if (label === "v3:implementation") {
      writeFileSync(join(cwd, "sample.txt"), "implemented\n");
      git("add", "sample.txt");
      reviewedTree = tree();
      return { status: "passed", treeSha: tree(), evidence: "implementation staged" };
    }
    if (label.startsWith("v3:validation:")) return { status: "passed", treeSha: tree(), evidence: "targeted tests passed" };
    if (label.includes("review:")) {
      if (options.review === "null" && label.includes("code-review")) return null;
      if (options.review === "blocked" && label.includes("code-review")) {
        return { status: "blocked", treeSha: tree(), findings: [] };
      }
      if (options.review === "inconsistent" && label.includes("code-review")) {
        return { status: "approved", treeSha: tree(), findings: ["unresolved"] };
      }
      return {
        status: options.correct ? "findings" : "approved",
        treeSha: reviewedTree,
        findings: options.correct ? ["sample.txt:1 correct behavior"] : [],
      };
    }
    if (label.startsWith("v3:corrections:")) {
      writeFileSync(join(cwd, "sample.txt"), "corrected\n");
      git("add", "sample.txt");
      return { status: "passed", treeSha: tree(), evidence: "correction validated" };
    }
    if (label.startsWith("v3:publication:")) {
      publicationCount++;
      if (publicationCount === 1) git("commit", "-qm", "feature");
      if (options.publicationNullOnce && publicationCount === 1) return null;
      return {
        status: "published", headSha: options.wrongHead ? "a".repeat(40) : git("rev-parse", "HEAD"),
        treeSha: tree(), prNumber: 42, prUrl: "https://github.com/example/repo/pull/42",
        commentUrl: "https://github.com/example/repo/pull/42#issuecomment-1",
        evidence: "PR and timeline comment published",
      };
    }
    if (label.startsWith("v3:admission-owner:")) {
      if (options.ownerBlocked) return { status: "blocked", treeSha: tree() };
      return {
        status: "approved", treeSha: tree(), coordinatorSessionId: "parent-session",
        authorizationId: "message-123", evidence: "parent approved exclusive admission at this SHA",
      };
    }
    if (label.startsWith("v3:live-admission:")) {
      admissionCount++;
      if (options.blockFirstAdmission && admissionCount === 1) {
        return { status: "blocked", headSha: git("rev-parse", "HEAD"), treeSha: tree(), prNumber: 42, evidence: "exclusive admission not granted" };
      }
      return {
        status: "merged", headSha: git("rev-parse", "HEAD"), treeSha: tree(), prNumber: 42,
        evidence: "fresh CI and merge verified",
      };
    }
    if (label.startsWith("v3:cleanup:")) return { status: "passed", treeSha: tree(), evidence: "parent must clean checked-out branch" };
    throw new Error(`Unexpected agent label: ${label}`);
  };
  return { agent, admissionCount: () => admissionCount, publicationCount: () => publicationCount };
}

function mergedGh({ git }, options = {}) {
  return (...parts) => {
    if (parts[0] === "pr") {
      return JSON.stringify({
        state: options.notMerged ? "OPEN" : "MERGED",
        headRefOid: git("rev-parse", "HEAD"), baseRefName: "v1",
        mergeCommit: { oid: git("rev-parse", "HEAD") },
        url: "https://github.com/example/repo/pull/42",
      });
    }
    if (parts[0] === "api") return options.wrongTree ? "a".repeat(40) : git("rev-parse", "HEAD^{tree}");
    throw new Error("Unexpected gh call: " + parts.join(" "));
  };
}

test("null, blocked and inconsistent review cannot authorize publication", async (t) => {
  for (const review of ["null", "blocked", "inconsistent"]) {
    await t.test(review, async (subtest) => {
      const repo = fixture(subtest);
      const { ctx, calls } = context({ args: { mode: "deliver" }, agent: deliveryAgent(repo, { review }).agent });
      await assert.rejects(runIssueToMerge(ctx, repo.cwd), /Both actual local reviews|Blocked|Review result/);
      assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
    });
  }
});

test("resume after correction and commit reuses historical trees but refreshes admission", async (t) => {
  const repo = fixture(t);
  const handler = deliveryAgent(repo, { correct: true, blockFirstAdmission: true });
  const { ctx, calls, journal } = context({ args: { mode: "deliver" }, agent: handler.agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo)), /Blocked/);
  const finalTree = repo.git("rev-parse", "HEAD^{tree}");
  assert.notEqual(finalTree, journal.get("v3:implementation").treeSha);
  const result = await runIssueToMerge(ctx, repo.cwd, mergedGh(repo));
  assert.equal(result.status, "merged");
  assert.equal(result.reviewEvidence.finalTree, finalTree);
  assert.equal(handler.admissionCount(), 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:publication:")).length, 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:admission-owner:")).length, 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:review:")).length, 2);
  const admissions = calls.filter((c) => c.label.startsWith("v3:live-admission:"));
  assert.equal(admissions.length, 2);
  assert.notEqual(admissions[0].label, admissions[1].label);
});

test("publication must match exact committed head before live admission", async (t) => {
  const repo = fixture(t);
  const { ctx, calls } = context({ args: { mode: "deliver" }, agent: deliveryAgent(repo, { wrongHead: true }).agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd), /Publication is not bound/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:live-admission:")), false);
});

test("publication retry reconciles a commit after a null agent result", async (t) => {
  const repo = fixture(t);
  const handler = deliveryAgent(repo, { publicationNullOnce: true });
  const { ctx, calls } = context({ args: { mode: "deliver" }, agent: handler.agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo)), /Blocked or invalid agent output/);
  const commit = repo.git("rev-parse", "HEAD");
  const result = await runIssueToMerge(ctx, repo.cwd, mergedGh(repo));
  assert.equal(result.status, "merged");
  assert.equal(repo.git("rev-parse", "HEAD"), commit);
  assert.equal(handler.publicationCount(), 2);
  const publications = calls.filter((c) => c.label.startsWith("v3:publication:"));
  assert.notEqual(publications[0].label, publications[1].label);
});

test("coordinator denial blocks merge before admission agent", async (t) => {
  const repo = fixture(t);
  const { ctx, calls } = context({ args: { mode: "deliver" }, agent: deliveryAgent(repo, { ownerBlocked: true }).agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo)), /Blocked/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:live-admission:")), false);
});

test("live PR state and merge tree must match final reviewed candidate", async (t) => {
  for (const mismatch of [{ notMerged: true }, { wrongTree: true }]) {
    await t.test(JSON.stringify(mismatch), async (subtest) => {
      const repo = fixture(subtest);
      const { ctx } = context({ args: { mode: "deliver" }, agent: deliveryAgent(repo).agent });
      await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo, mismatch)), /Live GitHub merge does not match/);
    });
  }
});

test("external tree changes after publication block resumed admission", async (t) => {
  const repo = fixture(t);
  const handler = deliveryAgent(repo, { blockFirstAdmission: true });
  const { ctx } = context({ args: { mode: "deliver" }, agent: handler.agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo)), /Blocked/);
  writeFileSync(join(repo.cwd, "sample.txt"), "external\n");
  repo.git("add", "sample.txt");
  await assert.rejects(runIssueToMerge(ctx, repo.cwd, mergedGh(repo)), /Current tree differs/);
  assert.equal(handler.admissionCount(), 1);
});
