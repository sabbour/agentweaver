import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
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
  const root = join(process.cwd(), ".workflow-test-fixtures");
  mkdirSync(root, { recursive: true });
  const cwd = mkdtempSync(join(root, "agentweaver-workflow-"));
  t.after(() => rmSync(cwd, { recursive: true, force: true }));
  const git = (...parts) => execFileSync("git", parts, { cwd, encoding: "utf8", windowsHide: true }).trim();
  git("init", "-b", "feature");
  git("config", "core.autocrlf", "false");
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
  let validationCount = 0;
  const agent = async (prompt, { label }) => {
    if (label === "v3:scope") return { status: "passed", treeSha: tree(), evidence: "issue scoped" };
    if (label === "v3:implementation") {
      writeFileSync(join(cwd, "sample.txt"), "implemented\n");
      git("add", "sample.txt");
      reviewedTree = tree();
      return { status: "passed", treeSha: tree(), evidence: "implementation staged" };
    }
    if (label.startsWith("v3:validation:")) {
      validationCount++;
      if (options.blockValidationOnce && validationCount === 1) {
        return { status: "blocked", treeSha: tree(), evidence: "deployed proof requested for library" };
      }
      if (options.requireFoundationPolicy) {
        assert.match(prompt, /contract\/adapter-only foundation with no deployable service/);
        assert.match(prompt, /report absent deployed Azure proof as a limitation, not a blocker/);
      }
      reviewedTree = tree();
      return { status: "passed", treeSha: tree(), evidence: "targeted tests passed" };
    }
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

test("non-deployable foundation validation resumes after a staged workflow policy fix", async (t) => {
  const repo = fixture(t);
  const handler = deliveryAgent(repo, { blockValidationOnce: true, requireFoundationPolicy: true, ownerBlocked: true });
  const { ctx, calls, journal } = context({ args: { mode: "deliver" }, agent: handler.agent });
  await assert.rejects(runIssueToMerge(ctx, repo.cwd), /Blocked/);
  const originalTree = journal.get("v3:implementation").treeSha;
  const extensionDir = join(repo.cwd, ".github", "extensions", "agentweaver-issue-to-merge");
  mkdirSync(extensionDir, { recursive: true });
  writeFileSync(join(extensionDir, "workflow.mjs"), "validation policy correction\n");
  writeFileSync(join(extensionDir, "workflow.test.mjs"), "regression test\n");
  repo.git("add", ".github/extensions/agentweaver-issue-to-merge");
  const correctedTree = repo.git("write-tree");
  assert.notEqual(correctedTree, originalTree);
  await assert.rejects(runIssueToMerge(ctx, repo.cwd), /Blocked/);
  assert.equal(calls.filter((c) => c.label === "v3:implementation").length, 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:validation:")).length, 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:review:")).length, 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:publication:")).length, 1);
  assert.equal(repo.git("rev-parse", "HEAD^{tree}"), correctedTree);
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

function correctedFixture(t, overrides = {}) {
  const repo = fixture(t);
  repo.git("remote", "add", "origin", "https://github.com/sabbour/agentweaver.git");
  const sourceHead = repo.git("rev-parse", "HEAD");
  const sourceTree = repo.git("rev-parse", "HEAD^{tree}");
  writeFileSync(join(repo.cwd, "sample.txt"), "corrected\n");
  repo.git("add", "sample.txt");
  let candidateTree = repo.git("write-tree");
  const source = {
    runId: "old-run-id", headSha: sourceHead, treeSha: sourceTree,
    branch: "feature", prNumber: 42, publicationCommentId: 101, denialCommentId: 102,
    paths: ["sample.txt"], ...overrides.source,
  };
  let publishedHead;
  let publicationCount = 0;
  let admissions = 0;
  let validations = 0;
  let baseTip = "e".repeat(40);
  const oldBody = `Workflow run ${source.runId}\nLocal review 1: approved tree ${sourceTree}\nLocal review 2: ${overrides.reviewSameTree ? "approved the same tree; findings: none" : `approved tree ${sourceTree}`}\nFinal HEAD: \`${sourceHead}\`\nFinal tree: \`${sourceTree}\``;
  const deniedBody = `ADMISSION DENIED for head ${sourceHead} / tree ${sourceTree}. Coordinator decision ID: parent-denied. ${overrides.denialDetail ?? ""}`;
  const prUrl = "https://github.com/sabbour/agentweaver/pull/42";
  const gh = (...parts) => {
    if (parts[0] === "pr") {
      const merged = parts.some((part) => part.includes("mergeCommit"));
      return JSON.stringify({
        number: 42, state: merged ? "MERGED" : "OPEN",
        headRefOid: overrides.wrongLiveHead ? "f".repeat(40) : publishedHead ?? sourceHead,
        headRefName: overrides.wrongBranch ? "elsewhere" : "feature",
        baseRefName: overrides.wrongBase ? "dev" : "v1",
        url: prUrl, body: overrides.wrongIssue ? "Closes #9999" : "Closes #1742", author: { login: "owner" },
        files: [{ path: "sample.txt" }], mergeCommit: { oid: publishedHead },
        statusCheckRollup: overrides.badCi ? [{ conclusion: "FAILURE" }] : [{ conclusion: "SUCCESS" }],
      });
    }
    if (parts[0] === "api" && parts[1].startsWith("repos/")) {
      if (parts[1].includes("/branches/")) return baseTip;
      if (parts[1].includes("/git/commits/")) return candidateTree;
      const id = Number(parts[1].split("/").at(-1));
      return JSON.stringify({
        html_url: `${id === 103 && overrides.wrongCommentPr ? "https://github.com/sabbour/agentweaver/pull/99" : prUrl}#issuecomment-${id}`,
        user: { login: overrides.wrongAuthor || (id === 103 && overrides.wrongCommentAuthor) ? "other" : "owner" },
        body: id === 101 ? (overrides.spoofReceipt ?? oldBody) :
          id === 102 ? (overrides.spoofDenial ?? deniedBody) :
            `Workflow run test-run\nOld tree ${sourceTree}; denial #issuecomment-102\nFinal HEAD: \`${publishedHead}\`\nFinal tree: \`${candidateTree}\``,
      });
    }
    throw new Error("unexpected gh " + parts.join(" "));
  };
  const agent = async (_prompt, { label }) => {
    if (label.startsWith("v3:correct-validation:")) {
      validations++;
      candidateTree = repo.git("write-tree");
      return overrides.badValidation || (overrides.failValidationOnce && validations === 1)
        ? { status: "blocked", treeSha: candidateTree, evidence: "failed" }
        : { status: "passed", treeSha: candidateTree, evidence: "affected tests passed" };
    }
    if (label.startsWith("v3:publication:")) {
      publicationCount++;
      if (overrides.blockPublicationOnce && publicationCount === 1)
        return { status: "blocked", treeSha: candidateTree, evidence: "publication unavailable" };
      if (!publishedHead) {
        repo.git("commit", "-qm", "corrected");
        publishedHead = repo.git("rev-parse", "HEAD");
      }
      return {
        status: "published", headSha: publishedHead, treeSha: candidateTree,
        prNumber: overrides.wrongPublishedPr ? 43 : 42, prUrl,
        commentUrl: `${prUrl}#issuecomment-103`, evidence: "corrected publication",
      };
    }
    if (label.startsWith("v3:admission-owner:")) {
      admissions++;
      if (overrides.blockOwner && admissions === 1)
        return { status: "blocked", treeSha: candidateTree };
      if (overrides.advanceBaseAfterOwner && admissions === 1) baseTip = "d".repeat(40);
      return { status: "approved", treeSha: candidateTree, coordinatorSessionId: "parent",
        authorizationId: "new-grant", evidence: "exclusive grant for corrected head" };
    }
    if (label.startsWith("v3:live-admission:"))
      return { status: "merged", headSha: publishedHead, treeSha: candidateTree,
        prNumber: 42, evidence: "fresh CI, base and corrected exclusive merge verified" };
    if (label === `v3:cleanup:${publishedHead}`)
      return { status: "passed", treeSha: candidateTree, evidence: "cleanup handoff" };
    throw new Error(`Unexpected corrected agent: ${label}`);
  };
  return { repo, source, candidateTree, gh, agent, options: overrides, admissions: () => admissions,
    publications: () => publicationCount, head: () => publishedHead, baseTip: () => baseTip };
}

test("corrected route adopts staged scope, retains old-tree review and refreshes admission on replay", async (t) => {
  const f = correctedFixture(t, { blockOwner: true,
    source: { paths: ["sample.txt", "packages/Agentweaver.Abstractions/Lifetime.cs"] },
    denialDetail: "Abstractions needs metadata-only lifetime limiting." });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.includes("review:")).length, 0);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh);
  assert.equal(result.status, "merged");
  assert.equal(result.reviewEvidence.reviewedTree, f.source.treeSha);
  assert.equal(result.reviewEvidence.finalTree, f.candidateTree);
  assert.equal(f.publications(), 1);
  assert.equal(f.admissions(), 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 1);
});

test("source review two may explicitly approve the same exact tree as review one", async (t) => {
  const f = correctedFixture(t, { reviewSameTree: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), true);
});

test("failed corrected validation can adopt a newly staged scoped tree before publication", async (t) => {
  const f = correctedFixture(t, { failValidationOnce: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  writeFileSync(join(f.repo.cwd, "sample.txt"), "corrected again\n");
  f.repo.git("add", "sample.txt");
  const nextTree = f.repo.git("write-tree");
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  assert.notEqual(nextTree, f.candidateTree);
  assert.equal(f.repo.git("rev-parse", "HEAD^{tree}"), nextTree);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 2);
});

test("validated corrected tree cannot change before publication", async (t) => {
  const f = correctedFixture(t, { blockPublicationOnce: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  assert.equal(f.publications(), 1);
  writeFileSync(join(f.repo.cwd, "sample.txt"), "later\n");
  f.repo.git("add", "sample.txt");
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Already validated corrected candidate changed/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("replay refuses a PR body relinked to another issue after publication", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Blocked/);
  f.options.wrongIssue = true;
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Published correction replay is not bound/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("fresh corrected comment must belong to the source PR and its trusted author", async (t) => {
  for (const flag of ["wrongCommentPr", "wrongCommentAuthor"]) {
    await t.test(flag, async (subtest) => {
      const f = correctedFixture(subtest, { [flag]: true });
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Fresh corrected publication comment/);
      assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
    });
  }
});

test("base advance after ownership blocks admission until a fresh grant", async (t) => {
  const f = correctedFixture(t, { advanceBaseAfterOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Target tip changed after coordinator grant/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
  assert.equal(f.admissions(), 1);
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh);
  assert.equal(result.status, "merged");
  assert.equal(f.admissions(), 2);
});

test("corrected route rejects spoofed receipts, scope and failed validation before publication", async (t) => {
  for (const [name, options, mutate] of [
    ["source head", { source: { headSha: "a".repeat(40) } }],
    ["source tree", { source: { treeSha: "b".repeat(40) } }],
    ["wrong PR", { source: { prNumber: 43 } }],
    ["source run", { source: { runId: "wrong-run" }, spoofReceipt: "Workflow run old-run-id\nNo matching reviews" }],
    ["publication receipt", { spoofReceipt: "unrelated comment" }],
    ["denial", { spoofDenial: "approved" }],
    ["author", { wrongAuthor: true }],
    ["branch", { wrongBranch: true }],
    ["base", { wrongBase: true }],
    ["head", { wrongLiveHead: true }],
    ["issue", { wrongIssue: true }],
    ["unowned path", { source: { paths: ["unrelated.txt"] } }],
    ["unrelated component", { source: { paths: ["sample.txt", "packages/Agentweaver.Unrelated/Extra.cs"] } }],
    ["wrong origin", {}, (r) => r.git("remote", "set-url", "origin", "https://evil.example/sabbour/agentweaver.git")],
    ["unstaged", {}, (r) => writeFileSync(join(r.cwd, "sample.txt"), "unstaged\n")],
    ["untracked", {}, (r) => writeFileSync(join(r.cwd, "other.txt"), "untracked\n")],
    ["validation", { badValidation: true }],
  ]) {
    await t.test(name, async (subtest) => {
      const f = correctedFixture(subtest, options);
      mutate?.(f.repo);
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh));
      assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
    });

  }
});

test("corrected route requires green current-head CI before coordinator admission", async (t) => {
  const f = correctedFixture(t, { badCi: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /CI is not green/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
});

test("corrected publication cannot redirect to another PR", async (t) => {
  const f = correctedFixture(t, { wrongPublishedPr: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh), /Publication is not bound/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
});

test("source receipt cannot be supplied to rehearsal or ordinary delivery", async () => {
  for (const mode of ["rehearse", "deliver"]) {
    const { ctx, calls } = context({ args: { mode, source: {} } });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Source receipt requires/);
    assert.equal(calls.length, 0);
  }
});
