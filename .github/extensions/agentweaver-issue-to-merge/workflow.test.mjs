import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
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
    session: options.session,
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

test("source chain is exclusive to corrected mode and has no caller-supplied candidate metadata", async () => {
  const chain = { runId: "child-run", parentRunId: "old-run-id", denialCommentId: 105 };
  for (const input of [
    { mode: "deliver", sourceChain: chain },
    { mode: "correct", source: {}, sourceChain: chain },
    { mode: "correct", source: null, sourceChain: chain },
    { mode: "correct", sourceChain: null },
    { mode: "correct", sourceChain: { ...chain, parentRunId: chain.runId } },
    { mode: "correct", sourceChain: { ...chain, denialCommentId: 0 } },
    { mode: "correct", sourceChain: { ...chain, headSha: "a".repeat(40) } },
    { mode: "correct", sourceChain: { ...chain, paths: ["sample.txt"] } },
  ]) {
    const { ctx, calls, journal } = context({ args: input });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Source receipt|exactly one source|Source chain/);
    assert.equal(calls.length, 0);
    assert.equal(journal.size, 0);
  }
});

test("admission continuation accepts only a native run pair and durable grant selector", async () => {
  const source = { runId: "pinned-auth-run", parentRunId: "normal-old-run", grantCommentId: 107 };
  for (const input of [
    { mode: "deliver", admissionSource: source },
    { mode: "correct", admissionSource: source, source: {} },
    { mode: "admit" },
    { mode: "admit", admissionSource: { ...source, parentRunId: source.runId } },
    { mode: "admit", admissionSource: { ...source, grantCommentId: 0 } },
    { mode: "admit", admissionSource: { ...source, headSha: "a".repeat(40) } },
    { mode: "admit", admissionSource: source, sourceChain: {} },
  ]) {
    const { ctx, calls, journal } = context({ args: input });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Admission source|Admission continuation|Source receipt/);
    assert.equal(calls.length, 0);
    assert.equal(journal.size, 0);
  }
});

test("retarget accepts only a native run pair and exact denial selector", async () => {
  const source = { runId: "pinned-relay-run", parentRunId: "normal-relay-run", denialCommentId: 105 };
  for (const input of [
    { mode: "deliver", retargetSource: source },
    { mode: "correct", sourceChain: source, retargetSource: source },
    { mode: "retarget" },
    { mode: "retarget", retargetSource: { ...source, parentRunId: source.runId } },
    { mode: "retarget", retargetSource: { ...source, denialCommentId: 0 } },
    { mode: "retarget", retargetSource: { ...source, headSha: "a".repeat(40) } },
    { mode: "retarget", retargetSource: source, admissionSource: {} },
  ]) {
    const { ctx, calls, journal } = context({ args: input });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Retarget|Admission source|Source receipt/);
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
  const correction = calls.find((c) => c.label.startsWith("v3:corrections:"));
  assert.equal(correction.model, "gpt-6.1-sol");
  assert.equal(correction.reasoningEffort, "medium");
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
  assert.equal(calls.find((c) => c.label === "v3:implementation").model, "gpt-6.1-sol");
  assert.equal(calls.find((c) => c.label === "v3:implementation").reasoningEffort, "medium");
  for (const review of calls.filter((c) => c.label.startsWith("v3:review:"))) {
    assert.equal(review.model, undefined);
    assert.equal(review.reasoningEffort, undefined);
  }
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
  const context = Array.from({ length: 12 }, (_, i) => `line-${i}`);
  const contextText = (index, suffix) => context.map((line, i) =>
    i === index ? line + suffix : line).join("\n") + "\n";
  if (overrides.adjacentContext) {
    writeFileSync(join(repo.cwd, "sample.txt"), contextText(-1, ""));
    repo.git("add", "sample.txt");
    repo.git("commit", "-qm", "baseline context");
  }
  const baseHead = repo.git("rev-parse", "HEAD");
  const baseTree = repo.git("rev-parse", "HEAD^{tree}");
  writeFileSync(join(repo.cwd, "sample.txt"), overrides.adjacentContext ?
    contextText(3, "-published") : "published\n");
  repo.git("add", "sample.txt");
  repo.git("commit", "-qm", "published feature");
  const sourceHead = repo.git("rev-parse", "HEAD");
  const sourceTree = repo.git("rev-parse", "HEAD^{tree}");
  let upstreamTip = baseHead;
  if (overrides.advanceBeforeRun) {
    repo.git("branch", "v1", baseHead);
    repo.git("switch", "v1");
    for (let i = 0; i < (overrides.upstreamSlices ?? 1); i++) {
      const file = overrides.upstreamConflict || overrides.upstreamSameFeature || overrides.adjacentContext
        ? "sample.txt" : `upstream-${i}.txt`;
      writeFileSync(join(repo.cwd, file), overrides.adjacentContext ?
        contextText(0, "-upstream") :
        overrides.upstreamSameFeature ? "published\n" : `new v1 slice ${i}\n`);
      repo.git("add", file);
      repo.git("commit", "-qm", `upstream slice ${i}`);
    }
    upstreamTip = repo.git("rev-parse", "HEAD");
    repo.git("switch", "feature");
  }
  writeFileSync(join(repo.cwd, "sample.txt"), overrides.adjacentContext ?
    contextText(3, "-corrected") : "corrected\n");
  repo.git("add", "sample.txt");
  if (overrides.extraPath) {
    const path = join(repo.cwd, overrides.extraPath);
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, "related corrective test\n");
    repo.git("add", overrides.extraPath);
  }
  let candidateTree = repo.git("write-tree");
  const source = {
    runId: "old-run-id", headSha: sourceHead, treeSha: sourceTree,
    branch: "feature", prNumber: 42, publicationCommentId: 101, denialCommentId: 102,
    paths: overrides.extraPath ? ["sample.txt", overrides.extraPath] : ["sample.txt"],
    ...(overrides.extraPath ? { scopeCommentId: 104 } : {}), ...overrides.source,
  };
  let publishedHead;
  let firstPublishedHead;
  let firstPublishedTree;
  let publicationCount = 0;
  let admissions = 0;
  let validations = 0;
  let baseTip = upstreamTip;
  const fetch = () => repo.git("fetch", ".", baseTip);
  const reviewedTree = source.reviewedTreeSha ?? sourceTree;
  const findings = reviewedTree !== sourceTree;
  const review = { status: findings ? "findings" : "approved",
    treeSha: reviewedTree, findings: findings ? ["specific source finding"] : [] };
  const published = { status: "published", headSha: sourceHead, treeSha: sourceTree,
    prNumber: source.prNumber, prUrl: "https://github.com/sabbour/agentweaver/pull/42",
    commentUrl: "https://github.com/sabbour/agentweaver/pull/42#issuecomment-101",
    evidence: "historical native publication" };
  const entries = [
    { status: "passed", treeSha: baseTree, evidence: "Issue #1742 in sabbour/agentweaver scoped" },
    review, review, review, review, published, published,
    ...(findings ? [
      { status: "passed", treeSha: sourceTree, evidence: "corrected" },
      { status: "passed", treeSha: sourceTree, evidence: "corrected" },
    ] : [])];
  const prior = { runId: source.runId, status: "error", error: "Blocked at admission-owner:old-head",
    snapshot: { journal: entries.map((entry, i) => ({
      journalKey: (i + 1).toString(16).padStart(64, "0"), resultJson: JSON.stringify(entry),
    })) } };
  const detail = {
    runId: source.runId, workflowName: "agentweaver-issue-to-merge", status: "error",
    liveAgentCount: overrides.activeOldAgent ? 1 : 0,
    phases: [3, 4, 5, 6].map((n) => ({
      ordinal: n, id: `p${n}`, title: [
        "Scope and classify", "Implement, document and record release impact",
        "Validate candidate", "Parallel local reviews", "Resolve findings",
        "Publish PR and review evidence", "Monitor CI and admit",
      ][n],
    })),
    agents: [
      ...["rubber-duck", "code-review"].map((kind, i) => ({
        runId: source.runId, agentId: `agent-${i}`, toolCallId: `call-${i}`,
        label: `v3:review:${reviewedTree}:${kind}`, agentType: kind, phaseId: "p3", status: "completed",
      })),
      ...(findings ? [{ runId: source.runId, agentId: "correction", toolCallId: "correct",
        label: `v3:corrections:${reviewedTree}`, agentType: "general-purpose",
        phaseId: "p4", status: "completed" }] : []),
      { runId: source.runId, agentId: "publication", toolCallId: "publish",
        label: `v3:publication:${sourceTree}:id`, agentType: "general-purpose",
        phaseId: "p5", status: "completed" },
    ],
  };
  const session = { workflow: {
    getRun: async () => prior,
    getRunDetail: async () => detail,
  } };
  const oldBody = `Workflow run ${source.runId}\nLocal review 1: ${findings ? "findings" : "approved"} tree ${reviewedTree}\nLocal review 2: ${overrides.reviewSameTree ? "approved the same tree; findings: none" : `${findings ? "findings" : "approved"} tree ${reviewedTree}`}\nFinal HEAD: \`${sourceHead}\`\nFinal tree: \`${sourceTree}\``;
  const deniedBody = `ADMISSION DENIED for head ${sourceHead} / tree ${sourceTree}. Coordinator decision ID: parent-denied. ${overrides.denialDetail ?? ""}`;
  const prUrl = "https://github.com/sabbour/agentweaver/pull/42";
  const scopeBody = `Workflow run ${source.runId} PR #42 issue #1742\nSource: ${sourceHead} / ${sourceTree}\n` +
    `Staged correction tree: \`${overrides.scopeWrongTree ? "a".repeat(40) : candidateTree}\`\nDenial #issuecomment-102\n` +
    source.paths.map((path) => `- \`${path}\``).join("\n") +
    "\nscope confirmation, not admission authorization";
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
        created_at: id === 106 ? "2026-10-03T09:10:00Z" :
          id === 105 ? (overrides.staleNewDenial ? "2026-10-03T08:00:00Z" : "2026-10-03T09:07:00Z") :
          id === 103 ? "2026-10-03T09:05:00Z" :
          id === 104 ? "2026-10-03T09:03:00Z" :
          id === 102 ? "2026-10-03T09:02:00Z" : "2026-10-03T09:00:00Z",
        updated_at: id === 106 ? "2026-10-03T09:10:00Z" :
          id === 105 ? "2026-10-03T09:07:00Z" :
          id === 103 ? "2026-10-03T09:05:00Z" :
          id === 104 ? "2026-10-03T09:03:00Z" : id === 102
          ? (overrides.staleDenial ? "2026-10-03T08:00:00Z" : "2026-10-03T09:02:00Z")
          : "2026-10-03T09:00:00Z",
        body: id === 101 ? (overrides.spoofReceipt ?? oldBody) :
          id === 102 ? (overrides.spoofDenial ?? deniedBody) :
          id === 104 ? (overrides.spoofScope ?? scopeBody) :
          id === 105 ? (overrides.newDenialBody ??
            `ADMISSION DENIED for head ${firstPublishedHead} / tree ${firstPublishedTree}. Coordinator decision ID: root-new-denial.`) :
          id === 106 ? `Workflow run revision-run\nOld tree ${firstPublishedTree}; denial #issuecomment-105\n` +
            `Final pushed HEAD: \`${publishedHead}\`\nFinal committed tree: \`${candidateTree}\`` :
            `Workflow run test-run\nOld tree ${sourceTree}; denial #issuecomment-102\n` +
            `Final${overrides.actualCommentLabels ? " pushed" : ""} HEAD: \`${firstPublishedHead}\`\n` +
            `Final${overrides.actualCommentLabels ? " committed" : ""} tree: \`${firstPublishedTree}\``,
      });
    }
    throw new Error("unexpected gh " + parts.join(" "));
  };
  const agent = async (_prompt, { label }) => {
    if (label.startsWith("v3:correct-validation:")) {
      assert.match(_prompt, /owning workflow already verified native terminal run (?:old-run-id|test-run)/);
      assert.match(_prompt, /Subagents have separate sessions and cannot re-read that workflow run/);
      assert.match(_prompt, /never describe the historical pair as approval of this tree/);
      validations++;
      candidateTree = repo.git("write-tree");
      return overrides.badValidation || (overrides.failValidationOnce && validations === 1)
        ? { status: "blocked", treeSha: candidateTree, evidence: "failed" }
        : { status: "passed", treeSha: candidateTree, evidence: "affected tests passed" };
    }
    if (label.startsWith("v3:publication:")) {
      publicationCount++;
      assert.match(_prompt, /The owning workflow already verified native terminal source run (?:old-run-id|test-run)/);
      assert.match(_prompt, /This publisher subagent has a separate session and cannot read the owning workflow's getRun\/getRunDetail/);
      assert.match(_prompt, /do not block solely because its own SDK lookup says session not found/);
      assert.match(_prompt, /Recheck the live PR, source head\/tree, denial, target and candidate with Git\/GitHub/);
      assert.ok(_prompt.includes(`published head ${firstPublishedHead ?? sourceHead} / tree ${firstPublishedTree ?? sourceTree}`));
      assert.ok(_prompt.includes(`exact coordinator denial ${prUrl}#issuecomment-${firstPublishedHead ? 105 : 102}`));
      if (firstPublishedHead) assert.ok(_prompt.includes("parent native lineage"));
      if (overrides.blockPublicationOnce && publicationCount === 1)
        return { status: "blocked", treeSha: candidateTree, evidence: "publication unavailable" };
      if (!publishedHead || repo.git("status", "--porcelain")) {
        if (repo.git("status", "--porcelain")) repo.git("commit", "-qm", "corrected");
        publishedHead = repo.git("rev-parse", "HEAD");
        if (!firstPublishedHead) {
          firstPublishedHead = publishedHead;
          firstPublishedTree = candidateTree;
        }
      }
      return {
        status: "published", headSha: publishedHead, treeSha: candidateTree,
        prNumber: overrides.wrongPublishedPr ? 43 : 42, prUrl,
        commentUrl: `${prUrl}#issuecomment-${publicationCount === 1 ? 103 : 106}`,
        evidence: "corrected publication",
      };
    }
    if (label.startsWith("v3:admission-owner:")) {
      admissions++;
      assert.match(_prompt, /coordinator-authored, durable top-level comment on THIS PR is an actual response/);
      assert.match(_prompt, /even if a cross-session reply never reaches this child session/);
      assert.match(_prompt, /numeric comment ID\/URL as the stable response ID/);
      assert.match(_prompt, /author and coordinator session ID against the actual coordinator/);
      assert.match(_prompt, /explicit first-line APPROVED decision ID, exclusive sole-slot evidence/);
      assert.match(_prompt, /exact PR, head, tree and current target base/);
      assert.match(_prompt, /re-read for withdrawal or supersession/);
      assert.match(_prompt, /Do not author your own grant, accept quoted or revoked approval/);
      assert.match(_prompt, /live merge executor's fresh head\/base\/CI checks/);
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
  return { repo, source, candidateTree, baseHead, baseTree, gh, fetch, session, prior, detail, agent,
    options: overrides, admissions: () => admissions,
    publications: () => publicationCount, head: () => publishedHead,
    firstHead: () => firstPublishedHead, firstTree: () => firstPublishedTree, baseTip: () => baseTip };
}

test("corrected publisher receives owning-session native proof without a child-session SDK lookup", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:publication:")).length, 1);
});

async function sourceChainFixture(t, overrides = {}) {
  const f = correctedFixture(t, {
    source: { reviewedTreeSha: "a".repeat(40) }, blockOwner: true, actualCommentLabels: true,
    ...overrides,
  });
  const first = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(first.ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  const receipt = first.journal.get("correct-v1:receipt:old-run-id");
  const validated = first.journal.get(`v3:correct-validation:${f.firstTree()}`);
  const published = first.journal.get(`v3:publication:${f.firstTree()}`);
  assert.ok(receipt && validated && published);
  f.detail.completedAt = Date.now();
  f.detail.phases.push(
    { ordinal: 0, id: "p0", title: "Scope and classify" },
    { ordinal: 1, id: "p1", title: "Implement, document and record release impact" },
  );
  f.detail.agents.push({
    runId: "old-run-id", agentId: "scope", toolCallId: "scope",
    label: "v3:scope", agentType: "general-purpose",
    phaseId: "p0", status: "completed",
  }, {
    runId: "old-run-id", agentId: "implementation", toolCallId: "implement",
    label: "v3:implementation", agentType: "general-purpose",
    phaseId: "p1", status: "completed",
  });
  const child = {
    runId: "test-run", status: "error",
    error: "Fresh corrected publication comment does not bind source and new candidate.",
    snapshot: { journal: [receipt, validated, validated, published, published].map((entry, i) => ({
      journalKey: (i + 101).toString(16).padStart(64, "0"), resultJson: JSON.stringify(entry),
    })) },
  };
  const childDetail = {
    runId: "test-run", workflowName: "agentweaver-issue-to-merge", status: "error",
    completedAt: Date.now(), liveAgentCount: 0,
    phases: [
      { ordinal: 2, id: "p2", title: "Validate candidate" },
      { ordinal: 5, id: "p5", title: "Publish PR and review evidence" },
    ],
    agents: [
      { runId: "test-run", agentId: "child-validator", toolCallId: "validate",
        label: `v3:correct-validation:${f.firstTree()}`, agentType: "general-purpose",
        phaseId: "p2", status: "completed" },
      { runId: "test-run", agentId: "child-publisher", toolCallId: "publish-child",
        label: `v3:publication:${f.firstTree()}:id`, agentType: "general-purpose",
        phaseId: "p5", status: "completed" },
    ],
  };
  writeFileSync(join(f.repo.cwd, "sample.txt"), "revised\n");
  f.repo.git("add", "sample.txt");
  const revisionTree = f.repo.git("write-tree");
  const sourceChain = { runId: child.runId, parentRunId: f.source.runId, denialCommentId: 105 };
  const session = { workflow: {
    getRun: async (id) => {
      if (id === child.runId) return child;
      if (id === f.source.runId) return f.prior;
      throw new Error("session not found");
    },
    getRunDetail: async (id) => {
      if (id === child.runId) return childDetail;
      if (id === f.source.runId) return f.detail;
      throw new Error("session not found");
    },
  } };
  const agent = (prompt, options) =>
    options.label.startsWith("v3:admission-owner:")
      ? { status: "blocked", treeSha: f.repo.git("write-tree") }
      : f.agent(prompt, options);
  return { ...f, child, childDetail, sourceChain, session, agent, revisionTree };
}

async function retargetFixture(t, options = {}) {
  const f = await sourceChainFixture(t, { adjacentContext: options.adjacentContext, source: {} });
  f.repo.git("restore", "--staged", "--worktree", "sample.txt");
  f.prior.snapshot.journal.push({
    journalKey: "f".repeat(64),
    resultJson: JSON.stringify({ branch: "feature", headSha: f.baseHead,
      treeSha: f.baseTree, cwd: f.repo.cwd, status: "", unstaged: "" }),
  });
  f.child.snapshot.journal.push({
    journalKey: "e".repeat(64), resultJson: JSON.stringify({ sha: f.baseHead }),
  });
  f.child.error = "Request workflow.execute failed with message: Blocked or invalid agent output at " +
    `admission-owner:${options.wrongAdmissionHead ? "a".repeat(40) : f.firstHead()}: {"status":"blocked"}`;
  f.childDetail.workflowName = options.wrongAlias ??
    "agentweaver-issue-to-merge-pinned-5b8";
  f.childDetail.phases.push({ ordinal: 6, id: "p6", title: "Monitor CI and admit" });
  f.childDetail.agents.push({
    runId: f.child.runId, agentId: "child-owner", toolCallId: "owner",
    label: `v3:admission-owner:${f.firstHead()}:id`, agentType: "general-purpose",
    phaseId: "p6", status: "completed",
  });
  if (options.foreignAgent) f.childDetail.agents.push({
    runId: f.child.runId, agentId: "foreign", toolCallId: "foreign",
    label: "v3:unexpected-writer", agentType: "general-purpose",
    phaseId: "p1", status: "completed",
  });
  f.repo.git("branch", "v1", f.baseHead);
  f.repo.git("switch", "v1");
  for (let i = 0; i < (options.upstreamSlices ?? 1); i++) {
    const path = options.conflict || options.adjacentContext && i === 0
      ? "sample.txt" : `upstream-${i}.txt`;
    writeFileSync(join(f.repo.cwd, path), options.conflict ? "conflict\n" :
      options.adjacentContext && i === 0
        ? readFileSync(join(f.repo.cwd, path), "utf8").replace("line-0\n", "line-0-upstream\n")
        : `upstream ${i}\n`);
    f.repo.git("add", path);
    f.repo.git("commit", "-qm", `upstream ${i}`);
  }
  const target = f.repo.git("rev-parse", "HEAD");
  f.repo.git("switch", "feature");
  let remote = f.firstHead();
  let publishedComment;
  let rootGrant;
  let withdrawal;
  let pushes = 0;
  let comments = 0;
  let validations = 0;
  let merged = false;
  let publishedHead;
  let wrongMergeTree = false;
  let executorCalls = 0;
  let cleanupFailures = options.failCleanupOnce ? 1 : 0;
  let ownRunId;
  let ownJournal;
  const ownAgents = [];
  const ownAgent = (label, ordinal) => {
    if (!ownRunId) return;
    ownAgents.push({ runId: ownRunId, agentId: `native-${ownAgents.length}`,
      toolCallId: `call-${ownAgents.length}`, label, phaseId: `p${ordinal}`,
      agentType: "general-purpose", status: "completed" });
  };
  const ref = "refs/heads/feature";
  const remoteGit = (...parts) => {
    if (merged) throw new Error("Merged replay cannot consult or update the deleted remote ref.");
    if (parts[0] === "ls-remote") return `${remote}\t${ref}`;
    assert.deepEqual(parts, ["push", `--force-with-lease=${ref}:${f.firstHead()}`,
      "origin", `HEAD:${ref}`]);
    if (options.driftRemote) remote = "f".repeat(40);
    if (remote !== f.firstHead()) throw new Error("lease rejected");
    pushes++;
    remote = f.repo.git("rev-parse", "HEAD");
    if (options.failAfterPushOnce) {
      options.failAfterPushOnce = false;
      throw new Error("connection lost after successful push");
    }
    return "";
  };
  const gh = (...parts) => {
    if (parts[0] === "pr") {
      const pr = JSON.parse(f.gh(...parts));
      pr.headRefOid = publishedHead ?? remote;
      pr.state = merged ? "MERGED" : "OPEN";
      pr.headRefName = merged ? null : "feature";
      pr.mergeCommit = merged ? { oid: publishedHead } : null;
      pr.mergedAt = merged ? "2026-10-03T09:15:00Z" : null;
      return JSON.stringify(pr);
    }
    if (parts[0] === "api" && parts[1].includes("/branches/")) {
      if (merged) return publishedHead;
      if (options.currentBase) return options.currentBase;
      return options.driftBaseAfterValidation && pushes ? "a".repeat(40) : target;
    }
    if (parts[0] === "api" && parts[1].includes("/git/commits/") && merged)
      return wrongMergeTree ? "a".repeat(40) : f.repo.git("rev-parse", "HEAD^{tree}");
    if (parts[0] === "api" && parts[1] === "repos/sabbour/agentweaver/issues/42/comments") {
      if (parts.includes("POST")) {
        comments++;
        publishedComment = { id: 108,
          html_url: "https://github.com/sabbour/agentweaver/pull/42#issuecomment-108",
        user: { login: "owner" }, created_at: "2026-10-03T09:11:00Z",
        body: parts.find((part) => part.startsWith("body=")).slice(5) };
        return JSON.stringify(publishedComment);
      }
      return JSON.stringify([[...(publishedComment ? [publishedComment] : []),
        ...(rootGrant ? [rootGrant] : []), ...(withdrawal ? [withdrawal] : [])]]);
    }
    if (parts[0] === "api" && parts[1] ===
        "repos/sabbour/agentweaver/issues/comments/108") return JSON.stringify(publishedComment);
    return f.gh(...parts);
  };
  const fetch = () => f.repo.git("fetch", ".", target);
  const agent = (_prompt, { label }) => {
    if (label.startsWith("v3:retarget-validation:")) {
      validations++;
      ownAgent(label, 2);
      if (options.failValidationOnce && validations === 1)
        return { status: "blocked", treeSha: f.repo.git("write-tree"), evidence: "transient test failure" };
      return { status: "passed", treeSha: f.repo.git("write-tree"), evidence: "affected tests passed" };
    }
    if (label.startsWith("v3:admission-owner:")) {
      ownAgent(label, 6);
      return rootGrant ? { status: "approved", treeSha: f.repo.git("write-tree"),
        coordinatorSessionId: "2c6ca814-3304-4ec5-9198-dab59373380f",
        authorizationId: rootGrant.html_url, evidence: "verified Root comment" } :
        { status: "blocked", treeSha: f.repo.git("write-tree"), evidence: "no exclusive grant" };
    }
    if (label.startsWith("v3:live-admission:")) {
      ownAgent(label, 6);
      executorCalls++;
      if (options.mergeOnExecutor) {
        publishedHead = f.repo.git("rev-parse", "HEAD");
        merged = true;
        return { status: "merged", headSha: publishedHead,
          treeSha: f.repo.git("write-tree"), prNumber: 42,
          evidence: "exact native merge completed" };
      }
      return { status: "blocked", treeSha: f.repo.git("write-tree"), evidence: "no merge attempted" };
    }
    if (label.startsWith("v3:cleanup:")) {
      if (cleanupFailures--) return { status: "blocked", treeSha: f.repo.git("write-tree"),
        evidence: "cleanup interrupted" };
      return { status: "passed", treeSha: f.repo.git("write-tree"), evidence: "scoped cleanup handoff" };
    }
    throw new Error("Unexpected retarget agent: " + label);
  };
  return { ...f, target, gh, fetch, remoteGit, agent,
    selector: { runId: f.child.runId, parentRunId: f.source.runId, denialCommentId: 105 },
    pushes: () => pushes, comments: () => comments, remote: () => remote,
    validations: () => validations, executorCalls: () => executorCalls,
    badMergeTree: () => { wrongMergeTree = true; },
    advanceBase: () => { options.currentBase = "a".repeat(40); },
    removeExecutor: () => { ownAgents.splice(ownAgents.findIndex((agent) =>
      agent.label.startsWith("v3:live-admission:")), 1); },
    bind: (ctx, journal) => {
      ownRunId = ctx.runId;
      ownJournal = journal;
      const originalGetRun = f.session.workflow.getRun;
      const originalGetRunDetail = f.session.workflow.getRunDetail;
      f.session.workflow.getRun = async (id) => id === ownRunId ? {
        runId: id, snapshot: { journal: [...ownJournal].flatMap(([key, value], index) => {
          const result = { journalKey: (index + 301).toString(16).padStart(64, "0"),
            resultJson: JSON.stringify(value) };
          return /^(?:v3:retarget-validation:|v3:cleanup:)/.test(key) ? [result, {
            journalKey: (index + 601).toString(16).padStart(64, "0"),
            resultJson: result.resultJson }] : [result];
        }) },
      } : originalGetRun(id);
      f.session.workflow.getRunDetail = async (id) => id === ownRunId ? {
        runId: id, workflowName: "agentweaver-issue-to-merge",
        phases: [2, 6].map((ordinal) =>
          ({ ordinal, id: `p${ordinal}`, title: ordinal === 2
            ? "Validate candidate" : "Monitor CI and admit" })),
        agents: ownAgents,
      } : originalGetRunDetail(id);
    },
    authorize: () => {
      rootGrant = { id: 109,
        html_url: "https://github.com/sabbour/agentweaver/pull/42#issuecomment-109",
        user: { login: "owner" }, created_at: "2026-10-03T09:12:00Z",
        updated_at: "2026-10-03T09:12:00Z",
        body: "Coordinator admission decision: APPROVED — root-retarget-42.\n" +
          `coordinator session 2c6ca814-3304-4ec5-9198-dab59373380f ` +
          `authorizes PR #42 HEAD ${remote} tree ${f.repo.git("rev-parse", "HEAD^{tree}")} ` +
          `with current v1 base ${target}; reserved the sole admission slot; ` +
          "no other grant exists and none has auto-merge enabled.",
      };
    },
    withdraw: () => {
      withdrawal = { id: 110, user: { login: "owner" },
        created_at: "2026-10-03T09:13:00Z", body: "WITHDRAWN root-retarget-42." };
    },
  };
}

test("retarget preserves both owned patch slices and publishes once under an exact lease", async (t) => {
  const f = await retargetFixture(t, { upstreamSlices: 2 });
  const { ctx, calls, journal } = context({
    args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session,
  });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /No unique live.*grant/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
  assert.equal(f.remote(), f.repo.git("rev-parse", "HEAD"));
  assert.equal(f.repo.git("rev-parse", "HEAD~2"), f.target);
  assert.ok(journal.has(`retarget-v1:publication:${f.remote()}`));
  assert.equal(calls.filter((call) => call.label.startsWith("v3:retarget-validation:")).length, 1);
  assert.ok(calls.every((call) => !/v3:(?:review:|implementation|corrections:|publication:)/.test(call.label)));
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /No unique live.*grant/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
  f.authorize();
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /Blocked/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 1);
  assert.match(calls.at(-1).prompt, /separate native merge executor inside owning workflow/);
  assert.match(calls.at(-1).prompt, /Use direct gh CLI for fresh live GitHub checks/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
  f.withdraw();
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /nonwithdrawn Root grant/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
});

test("retarget preserves slices when upstream alters adjacent patch context", async (t) => {
  const f = await retargetFixture(t, { adjacentContext: true });
  const { ctx } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /No unique live.*grant/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
});

test("retarget replays the same rebase after blocked validation", async (t) => {
  const f = await retargetFixture(t, { failValidationOnce: true });
  const { ctx } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /Blocked/);
  const head = f.repo.git("rev-parse", "HEAD");
  assert.equal(f.pushes(), 0);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /No unique live.*grant/);
  assert.equal(f.repo.git("rev-parse", "HEAD"), head);
  assert.equal(f.pushes(), 1);
  assert.equal(f.validations(), 2);
});

test("retarget pins the native selected base before rebase and rejects later drift", async (t) => {
  const f = await retargetFixture(t, { failValidationOnce: true });
  const { ctx, journal } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /Blocked/);
  assert.equal(journal.get(`retarget-v1:target:${f.selector.runId}`).sha, f.target);
  const head = f.repo.git("rev-parse", "HEAD");
  f.advanceBase();
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
    /freshly advanced descendant/);
  assert.equal(f.repo.git("rev-parse", "HEAD"), head);
  assert.equal(f.pushes(), 0);
  assert.equal(f.comments(), 0);
  assert.equal(f.validations(), 1);
});

test("retarget reconciles a successful push after lost response without repushing", async (t) => {
  const f = await retargetFixture(t, { failAfterPushOnce: true });
  const { ctx } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
    /connection lost after successful push/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 0);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /No unique live.*grant/);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
});

async function mergedRetargetFixture(t) {
  const f = await retargetFixture(t, { mergeOnExecutor: true, failCleanupOnce: true });
  const { ctx, calls, journal } = context({
    args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session,
  });
  ctx.runId = "fresh-retarget-run";
  f.bind(ctx, journal);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
    /No unique live.*grant/);
  f.authorize();
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
    /Blocked or invalid agent output at cleanup/);
  assert.equal(f.executorCalls(), 1);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
  return { ...f, ctx, calls, journal };
}

test("merged retarget resumes only cleanup from its own native owner and executor", async (t) => {
  const f = await mergedRetargetFixture(t);
  const before = f.calls.length;
  const result = await runIssueToMerge(f.ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit);
  assert.equal(result.status, "merged");
  assert.equal(result.pr.headSha, f.repo.git("rev-parse", "HEAD"));
  assert.equal(f.executorCalls(), 1);
  assert.equal(f.pushes(), 1);
  assert.equal(f.comments(), 1);
  assert.deepEqual(f.calls.slice(before).map((call) => call.label),
    [`v3:cleanup:${result.pr.headSha}`]);
  assert.ok(f.journal.has(`retarget-v1:target:${f.selector.runId}`));
  for (let attempt = 0; attempt < 2; attempt++) {
    const afterCleanup = f.calls.length;
    const replay = await runIssueToMerge(f.ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit);
    assert.equal(replay.status, "merged");
    assert.equal(f.calls.length, afterCleanup);
    assert.equal(f.executorCalls(), 1);
    assert.equal(f.pushes(), 1);
    assert.equal(f.comments(), 1);
  }
});

test("merged retarget refuses wrong merge tree or missing same-run executor", async (t) => {
  await t.test("wrong merge tree", async (subtest) => {
    const f = await mergedRetargetFixture(subtest);
    f.badMergeTree();
    const before = f.calls.length;
    await assert.rejects(runIssueToMerge(f.ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
      /exact candidate merge tree/);
    assert.equal(f.calls.length, before);
    assert.equal(f.executorCalls(), 1);
  });
  await t.test("missing native executor", async (subtest) => {
    const f = await mergedRetargetFixture(subtest);
    f.removeExecutor();
    const before = f.calls.length;
    await assert.rejects(runIssueToMerge(f.ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
      /typed owner\/executor/);
    assert.equal(f.calls.length, before);
    assert.equal(f.executorCalls(), 1);
  });
  await t.test("grant withdrawn before merge", async (subtest) => {
    const f = await mergedRetargetFixture(subtest);
    f.withdraw();
    const before = f.calls.length;
    await assert.rejects(runIssueToMerge(f.ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit),
      /nonwithdrawn Root grant/);
    assert.equal(f.calls.length, before);
    assert.equal(f.executorCalls(), 1);
  });
});

test("retarget rejects wrong native alias and foreign committed patch before rebase", async (t) => {
  await t.test("wrong alias", async (subtest) => {
    const f = await retargetFixture(subtest, { wrongAlias: "agentweaver-issue-to-merge" });
    const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
      agent: f.agent, session: f.session });
    ctx.runId = "fresh-retarget-run";
    await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /Corrective source run/);
    assert.equal(calls.length, 0);
    assert.equal(f.pushes(), 0);
  });
  await t.test("foreign patch", async (subtest) => {
    const f = await retargetFixture(subtest);
    writeFileSync(join(f.repo.cwd, "foreign.txt"), "unowned\n");
    f.repo.git("add", "foreign.txt");
    f.repo.git("commit", "--amend", "--no-edit");
    const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
      agent: f.agent, session: f.session });
    ctx.runId = "fresh-retarget-run";
    await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /Retarget requires|Rebased feature/);
    assert.equal(calls.length, 0);
    assert.equal(f.pushes(), 0);
  });
  await t.test("foreign child agent", async (subtest) => {
    const f = await retargetFixture(subtest, { foreignAgent: true });
    const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
      agent: f.agent, session: f.session });
    ctx.runId = "fresh-retarget-run";
    await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /typed validation and publication/);
    assert.equal(calls.length, 0);
    assert.equal(f.pushes(), 0);
  });
  await t.test("different admission head", async (subtest) => {
    const f = await retargetFixture(subtest, { wrongAdmissionHead: true });
    const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
      agent: f.agent, session: f.session });
    ctx.runId = "fresh-retarget-run";
    await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /different admission head/);
    assert.equal(calls.length, 0);
    assert.equal(f.pushes(), 0);
  });
});

test("retarget conflict blocks in place without push or cleanup", async (t) => {
  const f = await retargetFixture(t, { conflict: true });
  const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /rebase|could not apply/i);
  assert.equal(f.pushes(), 0);
  assert.equal(f.comments(), 0);
  assert.equal(calls.length, 0);
});

test("retarget refuses a changed remote lease before publication", async (t) => {
  const f = await retargetFixture(t, { driftRemote: true });
  const { ctx, calls } = context({ args: { mode: "retarget", retargetSource: f.selector },
    agent: f.agent, session: f.session });
  ctx.runId = "fresh-retarget-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch, f.remoteGit), /lease rejected/);
  assert.equal(f.pushes(), 0);
  assert.equal(f.comments(), 0);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:retarget-validation:")).length, 1);
});

test("exact-old-head Git lease rejects a competing remote update", (t) => {
  const repo = fixture(t);
  const bare = mkdtempSync(join(process.cwd(), ".workflow-test-fixtures", "lease-"));
  t.after(() => rmSync(bare, { recursive: true, force: true }));
  execFileSync("git", ["init", "--bare", "--initial-branch=feature", bare], {
    cwd: repo.cwd, encoding: "utf8", windowsHide: true,
  });
  const ref = "refs/heads/feature";
  const oldHead = repo.git("rev-parse", "HEAD");
  repo.git("push", bare, `HEAD:${ref}`);
  writeFileSync(join(repo.cwd, "sample.txt"), "owned update\n");
  repo.git("add", "sample.txt");
  repo.git("commit", "-qm", "owned candidate");
  const owned = repo.git("rev-parse", "HEAD");
  repo.git("switch", "-c", "competing", oldHead);
  writeFileSync(join(repo.cwd, "other.txt"), "competing update\n");
  repo.git("add", "other.txt");
  repo.git("commit", "-qm", "competing candidate");
  const competing = repo.git("rev-parse", "HEAD");
  repo.git("push", bare, `HEAD:${ref}`);
  repo.git("switch", "feature");
  assert.throws(() => repo.git("push", `--force-with-lease=${ref}:${oldHead}`,
    bare, `HEAD:${ref}`), (error) => /stale info/.test(error.stderr ?? ""));
  assert.notEqual(owned, competing);
  assert.equal(repo.git("ls-remote", "--heads", bare, ref).split(/\s+/)[0], competing);
});

async function admissionFixture(t, options = {}) {
  const f = correctedFixture(t, { blockOwner: true, actualCommentLabels: true });
  const original = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(original.ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  f.detail.phases.push(
    { ordinal: 0, id: "p0", title: "Scope and classify" },
    { ordinal: 1, id: "p1", title: "Implement, document and record release impact" },
  );
  f.detail.agents.push({
    runId: "old-run-id", agentId: "scope", toolCallId: "scope",
    label: "v3:scope", agentType: "general-purpose", phaseId: "p0", status: "completed",
  }, {
    runId: "old-run-id", agentId: "implementation", toolCallId: "implement",
    label: "v3:implementation", agentType: "general-purpose", phaseId: "p1", status: "completed",
  });
  const parent = original.journal.get("correct-v1:receipt:old-run-id");
  const target = original.journal.get("correct-v2:target-base:old-run-id");
  const validation = original.journal.get(`v3:correct-validation:${f.firstTree()}`);
  const publication = original.journal.get(`v3:publication:${f.firstTree()}`);
  assert.ok(parent && target && validation && publication);
  const child = {
    runId: "test-run", status: "error",
    error: options.error ??
      `${options.wrappedError ? "Request workflow.execute failed with message: " : ""}` +
      `Blocked or invalid agent output at admission-owner:${f.firstHead()}: ` +
      (options.wrappedError ? JSON.stringify({ status: "blocked", treeSha: f.firstTree(),
        coordinatorSessionId: "2c6ca814-3304-4ec5-9198-dab59373380f",
        authorizationId: "", evidence: "no delivered reply" }) : "null"),
    snapshot: { journal: [parent, target, validation, validation, publication, publication]
      .map((entry, index) => ({
        journalKey: (index + 201).toString(16).padStart(64, "0"),
        resultJson: JSON.stringify(entry),
      })) },
  };
  const detail = {
    runId: child.runId, workflowName: "agentweaver-issue-to-merge-pinned-d201",
    status: "error", completedAt: Date.now(), liveAgentCount: 0,
    phases: [2, 5, 6].map((ordinal) =>
      ({ ordinal, id: `p${ordinal}`, title: [
        "Scope and classify", "Implement, document and record release impact",
        "Validate candidate", "Parallel local reviews", "Resolve findings",
        "Publish PR and review evidence", "Monitor CI and admit",
      ][ordinal] })),
    agents: [
      { runId: child.runId, agentId: "validator", toolCallId: "validate",
        label: `v3:correct-validation:${f.firstTree()}`,
        agentType: "general-purpose", phaseId: "p2", status: "completed" },
      { runId: child.runId, agentId: "publisher", toolCallId: "publish",
        label: `v3:publication:${f.firstTree()}:id`,
        agentType: "general-purpose", phaseId: "p5", status: "completed" },
      { runId: child.runId, agentId: "owner", toolCallId: "grant",
        label: `v3:admission-owner:${f.firstHead()}:id`,
        agentType: "general-purpose", phaseId: "p6", status: "completed" },
      { runId: child.runId, agentId: "owner-retry", toolCallId: "grant-retry",
        label: `v3:admission-owner:${f.firstHead()}:retry`,
        agentType: "general-purpose", phaseId: "p6", status: "completed" },
    ],
  };
  const selector = { runId: child.runId, parentRunId: f.source.runId, grantCommentId: 107 };
  const grantUrl = "https://github.com/sabbour/agentweaver/pull/42#issuecomment-107";
  const coordinatorId = "2c6ca814-3304-4ec5-9198-dab59373380f";
  const grantBody = `Coordinator admission decision: APPROVED — root-42-admit.\n` +
    `Exclusive authorization from coordinator session ${coordinatorId} applies ONLY to PR #42 ` +
    `targeting v1 at HEAD ${f.firstHead()} and tree ${f.firstTree()}, with current v1 base ${f.baseTip()}. ` +
    "The coordinator reserved the sole admission slot: no other grant exists and none has auto-merge enabled.";
  const gh = (...parts) => {
    if (parts[0] === "pr") {
      const pr = JSON.parse(f.gh(...parts));
      pr.state = options.merged ? "MERGED" : "OPEN";
      pr.mergedAt = options.merged ? "2026-10-03T09:16:00Z" : null;
      pr.mergeCommit = options.merged ? { oid: f.firstHead() } : null;
      return JSON.stringify(pr);
    }
    if (parts[0] === "api" && parts[1].includes("/branches/") && options.currentBase)
      return options.currentBase;
    if (parts[0] === "api" && parts[1].includes("/git/commits/") &&
        options.merged && options.failMergeProofOnce) {
      options.failMergeProofOnce = false;
      return "a".repeat(40);
    }
    if (parts[0] === "api" && parts[1] === "repos/sabbour/agentweaver/issues/42/comments")
      return JSON.stringify([options.laterComments ?? []]);
    if (parts[0] === "api" && parts[1] === "repos/sabbour/agentweaver/issues/comments/107")
      return JSON.stringify({ html_url: options.grantPr ?? grantUrl,
        user: { login: options.grantAuthor ?? "owner" },
        created_at: "2026-10-03T09:12:00Z", updated_at: options.editedGrant
          ? "2026-10-03T09:13:00Z" : "2026-10-03T09:12:00Z",
        body: options.grantBody ?? grantBody });
    return f.gh(...parts);
  };
  const session = { workflow: {
    getRun: async (id) => id === child.runId ? child : id === f.source.runId ? f.prior :
      Promise.reject(new Error("session not found")),
    getRunDetail: async (id) => id === child.runId ? detail : id === f.source.runId ? f.detail :
      id === options.newRunId ? {
        runId: id, phases: [{ ordinal: 6, id: "p6", title: "Monitor CI and admit" }],
        agents: options.nativeAgents ?? [],
      } : Promise.reject(new Error("session not found")),
  } };
  const agent = (prompt, opts) => {
    if (opts.label.startsWith("v3:admission-owner:")) {
      assert.match(prompt, /real coordinator grant/);
      assert.ok(prompt.includes(grantUrl));
      if (options.newRunId) options.nativeAgents = [{
        runId: options.newRunId, agentId: "new-owner", toolCallId: "new-owner-call",
        agentType: "general-purpose", label: opts.label, phaseId: "p6", status: "completed",
      }];
      return { status: "approved", treeSha: f.firstTree(),
        coordinatorSessionId: coordinatorId, authorizationId: grantUrl,
        evidence: "live exact-head exclusive root grant" };
    }
    if (opts.label.startsWith("v3:live-admission:")) {
      assert.match(prompt, /You ARE ALREADY the separate native merge executor inside owning workflow run/);
      assert.match(prompt, /handler verified the originating session's native source, validation, publication and live coordinator grant/);
      assert.match(prompt, /SessionNotFound in this child is not missing owner proof/);
      assert.match(prompt, /Do NOT start, resume or reinvoke any workflow with runFromTool/);
      assert.match(prompt, /Use direct gh CLI for fresh live GitHub checks and the exact-head merge below/);
      assert.match(prompt, /Admission continuation cannot edit, revalidate, republish or rerun CI/);
      if (options.merged) assert.match(prompt, /NEVER call gh pr merge again/);
      if (options.newRunId) {
        options.executorAttempts = (options.executorAttempts ?? 0) + 1;
        options.nativeAgents.push({
          runId: options.newRunId, agentId: `new-executor-${options.executorAttempts}`,
          toolCallId: `new-executor-call-${options.executorAttempts}`,
          agentType: "general-purpose", label: opts.label, phaseId: "p6", status: "completed",
        });
      }
      options.merged = true;
      if (options.newRunId) options.currentBase = "c".repeat(40);
    }
    if (opts.label.startsWith("v3:cleanup:") && options.failCleanupOnce) {
      options.failCleanupOnce = false;
      return { status: "blocked", treeSha: f.firstTree() };
    }
    return f.agent(prompt, opts);
  };
  return { ...f, child, detail, selector, session, gh, agent, grantBody, grantUrl, grantOptions: options };
}

test("published pinned admission continues native owner and executor without another writer", async (t) => {
  const f = await admissionFixture(t);
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent: f.agent });
  ctx.runId = "new-admission-run";
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
  assert.equal(result.status, "merged");
  assert.equal(result.pr.headSha, f.firstHead());
  assert.deepEqual(calls.map((call) => call.label.split(":").slice(0, 2).join(":")),
    ["v3:admission-owner", "v3:live-admission", "v3:cleanup"]);
});

test("pinned admission accepts the exact SDK workflow.execute owner-error envelope", async (t) => {
  const f = await admissionFixture(t, { wrappedError: true });
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent: f.agent });
  ctx.runId = "new-admission-run";
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
  assert.equal(result.status, "merged");
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 1);
});

test("pinned admission rejects unrelated, nested and malformed SDK error envelopes", async (t) => {
  const owner = "Blocked or invalid agent output at admission-owner:old-head: null";
  for (const error of [
    `Request workflow.execute failed with message: Request workflow.execute failed with message: ${owner}`,
    `Request workflow.execute failed with message:${owner}`,
    `Request workflow.execute failed with message: Blocked or invalid agent output at publication:old-head: null`,
    `Request workflow.execute failed for session: ${owner}`,
    `Unrelated failure followed by ${owner}`,
  ]) {
    await t.test(error.slice(0, 72), async (subtest) => {
      const f = await admissionFixture(subtest, { error });
      const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
        session: f.session, agent: f.agent });
      ctx.runId = "new-admission-run";
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch),
        /Pinned native admission source is not a stopped post-publication owner attempt/);
      assert.equal(calls.length, 0);
    });
  }
});

test("pinned admission rejects false native provenance and stale grant before any agent", async (t) => {
  for (const [name, tamper] of [
    ["unknown alias", (f) => { f.detail.workflowName = "agentweaver-issue-to-merge"; }],
    ["active child", (f) => { f.detail.liveAgentCount = 1; }],
    ["prepublication child", (f) => { f.child.snapshot.journal.splice(-2); }],
    ["wrong parent", (f) => { f.selector.parentRunId = "other-parent"; }],
    ["parent review changed", (f) => { f.prior.snapshot.journal[1].resultJson =
      JSON.stringify({ status: "findings", treeSha: f.source.treeSha,
        findings: ["unverified new finding"] }); }],
    ["wrong validation role", (f) => { f.detail.agents[0].agentType = "task"; }],
    ["different staged scope tree", (f) => { f.child.snapshot.journal[0].resultJson =
      JSON.stringify({ ...JSON.parse(f.child.snapshot.journal[0].resultJson),
        stagedScopeTree: "b".repeat(40) }); }],
    ["duplicate publication", (f) => { f.child.snapshot.journal[5].resultJson =
      JSON.stringify({ ...JSON.parse(f.child.snapshot.journal[5].resultJson), treeSha: "b".repeat(40) }); }],
    ["admission executor already ran", (f) => { f.detail.agents.push({
      runId: f.child.runId, agentId: "executor", toolCallId: "executor",
      label: "v3:live-admission:old", agentType: "general-purpose",
      phaseId: "p6", status: "completed" }); }],
    ["stale PR head", (f) => { f.options.wrongLiveHead = true; }],
    ["stale target base", (f) => { f.child.snapshot.journal[1].resultJson =
      JSON.stringify({ sha: "a".repeat(40) }); }],
    ["failed exact-head CI", (f) => { f.options.badCi = true; }],
    ["wrong grant author", (f) => { f.gh = (...parts) =>
      parts[1] === "repos/sabbour/agentweaver/issues/comments/107" ?
        JSON.stringify({ html_url: f.grantUrl, user: { login: "other" },
          created_at: "2026-10-03T09:12:00Z", updated_at: "2026-10-03T09:12:00Z", body: f.grantBody }) :
        f.originalGh(...parts); }],
    ["quoted approval", (f) => { f.grantOptions.grantBody =
      "> Coordinator admission decision: APPROVED — root-42-admit."; }],
    ["wrong coordinator", (f) => { f.grantOptions.grantBody =
      f.grantBody.replace("2c6ca814-3304-4ec5-9198-dab59373380f", "other-session"); }],
    ["wrong grant PR", (f) => { f.grantOptions.grantPr =
      "https://github.com/sabbour/agentweaver/pull/99#issuecomment-107"; }],
    ["wrong grant tree", (f) => { f.grantOptions.grantBody =
      f.grantBody.replace(f.firstTree(), "a".repeat(40)); }],
    ["edited grant", (f) => { f.grantOptions.editedGrant = true; }],
    ["later withdrawal", (f) => { f.grantOptions.laterComments = [{
      user: { login: "owner" }, created_at: "2026-10-03T09:14:00Z",
      body: "WITHDRAWN root-42-admit; no merge authorized.",
    }]; }],
  ]) {
    await t.test(name, async (subtest) => {
      const f = await admissionFixture(subtest);
      f.originalGh = f.gh;
      tamper(f);
      const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
        session: f.session, agent: f.agent });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
      assert.equal(calls.length, 0);
    });
  }
});

test("admission replay rechecks a withdrawn grant and never starts another executor", async (t) => {
  const f = await admissionFixture(t);
  let blocked = true;
  const agent = (prompt, options) => options.label.startsWith("v3:admission-owner:") && blocked ?
    { status: "blocked", treeSha: f.firstTree() } : f.agent(prompt, options);
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent });
  ctx.runId = "new-admission-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  blocked = false;
  f.grantOptions.laterComments = [{
    user: { login: "owner" }, created_at: "2026-10-03T09:15:00Z",
    body: "Coordinator admission decision: APPROVED — another-slot.",
  }];
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /withdrawn or superseded/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 0);
});

test("post-merge interruption resumes only exact native reconciliation and cleanup", async (t) => {
  const f = await admissionFixture(t, {
    newRunId: "new-admission-run", failMergeProofOnce: true, failCleanupOnce: true,
  });
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent: f.agent });
  ctx.runId = "new-admission-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch),
    /Live GitHub merge does not match/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 1);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 2);
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
  assert.equal(result.status, "merged");
  assert.equal(result.pr.headSha, f.firstHead());
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 3);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:cleanup:")).length, 2);
});

test("already-merged PR without this run's owner and executor cannot start admission", async (t) => {
  const f = await admissionFixture(t, { merged: true, currentBase: "c".repeat(40) });
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent: f.agent });
  ctx.runId = "unknown-attempt";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
  assert.equal(calls.length, 0);
});

test("merged replay rejects a foreign-head executor even with matching native actors", async (t) => {
  const f = await admissionFixture(t, { merged: true, newRunId: "new-admission-run",
    currentBase: "c".repeat(40) });
  const actor = (label, id) => ({
    runId: "new-admission-run", agentId: id, toolCallId: `${id}-call`,
    label, agentType: "general-purpose", phaseId: "p6", status: "completed",
  });
  f.grantOptions.nativeAgents = [
    actor(`v3:admission-owner:${f.firstHead()}:one`, "owner"),
    actor(`v3:live-admission:${f.firstHead()}:one`, "executor"),
    actor(`v3:live-admission:${"a".repeat(40)}:foreign`, "foreign"),
  ];
  const { ctx, calls } = context({ args: { mode: "admit", admissionSource: f.selector },
    session: f.session, agent: f.agent });
  ctx.runId = "new-admission-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch),
    /Merged admission replay lacks/);
  assert.equal(calls.length, 0);
});

test("source chain derives exact native parent, correction and scope before a fresh same-PR publication", async (t) => {
  const f = await sourceChainFixture(t);
  const { ctx, calls } = context({ args: { mode: "correct", sourceChain: f.sourceChain },
    agent: f.agent, session: f.session });
  ctx.runId = "revision-run";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 2);
  assert.equal(f.repo.git("rev-parse", "HEAD^"), f.firstHead());
  assert.equal(f.repo.git("rev-parse", "HEAD^{tree}"), f.revisionTree);
  assert.equal(calls.filter((call) => call.label.includes(":review:")).length, 0);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((call) => call.label.startsWith("v3:live-admission:")).length, 0);
});

test("source chain rejects mismatched native roles, receipts, scope and live PR before a writer starts", async (t) => {
  for (const [name, tamper] of [
    ["missing native parent", (f) => { f.sourceChain.parentRunId = "missing-run"; }],
    ["non-normal parent", (f) => { f.detail.agents.find((agent) =>
      agent.label === "v3:implementation").agentType = "other"; }],
    ["parent findings changed", (f) => { f.prior.snapshot.journal[1].resultJson =
      JSON.stringify({ status: "approved", treeSha: f.source.reviewedTreeSha, findings: [] }); }],
    ["child active", (f) => { f.childDetail.liveAgentCount = 1; }],
    ["validator wrong type", (f) => { f.childDetail.agents[0].agentType = "task"; }],
    ["publisher wrong phase", (f) => { f.childDetail.agents[1].phaseId = "p2"; }],
    ["duplicate child journal key", (f) => { f.child.snapshot.journal[2].journalKey =
      f.child.snapshot.journal[1].journalKey; }],
    ["child publication receipt differs", (f) => { f.child.snapshot.journal[4].resultJson =
      JSON.stringify({ ...JSON.parse(f.child.snapshot.journal[4].resultJson),
        treeSha: "b".repeat(40) }); }],
    ["parent receipt differs", (f) => { f.child.snapshot.journal[0].resultJson =
      JSON.stringify({ ...JSON.parse(f.child.snapshot.journal[0].resultJson),
        sourceHead: "b".repeat(40) }); }],
    ["admission already attempted", (f) => { f.childDetail.agents.push({
      runId: "test-run", agentId: "old-admission", toolCallId: "admit",
      label: "v3:admission-owner:old", agentType: "general-purpose",
      phaseId: "p6", status: "completed",
    }); }],
    ["unowned staged path", (f) => {
      writeFileSync(join(f.repo.cwd, "outside.txt"), "unapproved\n");
      f.repo.git("add", "outside.txt");
    }],
    ["wrong repository", (f) => { f.repo.git("remote", "set-url", "origin", "https://github.com/else/other.git"); }],
    ["wrong issue", (f) => { f.options.wrongIssue = true; }],
    ["stale PR head", (f) => { f.options.wrongLiveHead = true; }],
    ["wrong PR base", (f) => { f.options.wrongBase = true; }],
    ["absent exact denial", (f) => { f.options.newDenialBody = "Old approval grants admission"; }],
    ["approval quoting denial", (f) => { f.options.newDenialBody =
      `APPROVED\nADMISSION DENIED for head ${f.firstHead()} / tree ${f.firstTree()}. ` +
      "Coordinator decision ID: root-old-denial."; }],
    ["stale new denial", (f) => { f.options.staleNewDenial = true; }],
  ]) {
    await t.test(name, async (subtest) => {
      const f = await sourceChainFixture(subtest);
      tamper(f);
      const { ctx, calls } = context({ args: { mode: "correct", sourceChain: f.sourceChain },
        agent: f.agent, session: f.session });
      ctx.runId = "revision-run";
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
      assert.equal(calls.length, 0);
      assert.equal(f.publications(), 1);
    });
  }
});

test("corrected route adopts staged scope, retains old-tree review and refreshes admission on replay", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.includes("review:")).length, 0);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
  assert.equal(result.status, "merged");
  assert.equal(result.reviewEvidence.reviewedTree, f.source.treeSha);
  assert.equal(result.reviewEvidence.finalTree, f.candidateTree);
  assert.equal(f.publications(), 1);
  assert.equal(f.admissions(), 2);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 1);
});

test("source review two may explicitly approve the same exact tree as review one", async (t) => {
  const f = correctedFixture(t, { reviewSameTree: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), true);
});

test("failed corrected validation can adopt a newly staged scoped tree before publication", async (t) => {
  const f = correctedFixture(t, { failValidationOnce: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  writeFileSync(join(f.repo.cwd, "sample.txt"), "corrected again\n");
  f.repo.git("add", "sample.txt");
  const nextTree = f.repo.git("write-tree");
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.notEqual(nextTree, f.candidateTree);
  assert.equal(f.repo.git("rev-parse", "HEAD^{tree}"), nextTree);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 2);
});

test("validated corrected tree cannot change before publication", async (t) => {
  const f = correctedFixture(t, { blockPublicationOnce: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  writeFileSync(join(f.repo.cwd, "sample.txt"), "later\n");
  f.repo.git("add", "sample.txt");
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Already validated corrected candidate changed/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("replay refuses a PR body relinked to another issue after publication", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  f.options.wrongIssue = true;
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Published correction replay is not bound/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("replay rejects a changed coordinator denial instead of resurrecting old approval", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  f.options.spoofDenial = "APPROVED — the old denial was edited.";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Source receipt or coordinator denial changed/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("fresh corrected comment must belong to the source PR and its trusted author", async (t) => {
  for (const flag of ["wrongCommentPr", "wrongCommentAuthor"]) {
    await t.test(flag, async (subtest) => {
      const f = correctedFixture(subtest, { [flag]: true });
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Fresh corrected publication comment/);
      assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
    });

  }
});

test("fresh corrected comment accepts the native publisher's final pushed HEAD and committed tree labels", async (t) => {
  const f = correctedFixture(t, { actualCommentLabels: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:admission-owner:")).length, 1);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("base advance after ownership blocks admission and stale replay", async (t) => {
  const f = correctedFixture(t, { advanceBaseAfterOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Target tip changed after coordinator grant/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
  assert.equal(f.admissions(), 1);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Current target advanced/);
  assert.equal(f.admissions(), 1);
});

test("corrected route rejects spoofed receipts, scope and failed validation before publication", async (t) => {
  for (const [name, options, mutate] of [
    ["source head", { source: { headSha: "a".repeat(40) } }],
    ["source tree", { source: { treeSha: "b".repeat(40) } }],
    ["wrong PR", { source: { prNumber: 43 } }],
    ["source run", { source: { runId: "wrong-run" }, spoofReceipt: "Workflow run old-run-id\nNo matching reviews" }],
    ["publication receipt", { spoofReceipt: "unrelated comment" }],
    ["denial", { spoofDenial: "approved" }],
    ["stale denial", { staleDenial: true }],
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
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
      assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
    });

  }
});

test("corrected route requires green current-head CI before coordinator admission", async (t) => {
  const f = correctedFixture(t, { badCi: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /CI is not green/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
});

test("native proof rejects absent, mismatched and duplicated prior receipts before validation", async (t) => {
  for (const [name, tamper] of [
    ["missing session", (f) => { f.session.workflow = undefined; }],
    ["wrong native run", (f) => { f.prior.runId = "unrelated-run"; }],
    ["normal completed run", (f) => { f.prior.status = "completed"; }],
    ["failed before ownership", (f) => { f.prior.error = "Blocked at validation"; }],
    ["wrong workflow", (f) => { f.detail.workflowName = "other"; }],
    ["one review role missing", (f) => { f.detail.agents.splice(1, 1); }],
    ["wrong review role type", (f) => { f.detail.agents[1].agentType = "general-purpose"; }],
    ["same agent in both roles", (f) => { f.detail.agents[1].agentId = f.detail.agents[0].agentId; }],
    ["same call in both roles", (f) => { f.detail.agents[1].toolCallId = f.detail.agents[0].toolCallId; }],
    ["duplicated journal key", (f) => { f.prior.snapshot.journal[2].journalKey = f.prior.snapshot.journal[1].journalKey; }],
    ["one missing review result", (f) => { f.prior.snapshot.journal.splice(2, 1); }],
    ["one non-approved review", (f) => {
      f.prior.snapshot.journal[2].resultJson = JSON.stringify({
        status: "findings", treeSha: f.source.treeSha, findings: ["missed requirement"],
      });
    }],
    ["wrong source issue receipt", (f) => { f.prior.snapshot.journal[0].resultJson =
      JSON.stringify({ status: "passed", treeSha: f.baseTree, evidence: "Issue #9999 in sabbour/agentweaver" }); }],
    ["wrong native publication head", (f) => { f.prior.snapshot.journal[5].resultJson =
      JSON.stringify({ status: "published", treeSha: f.source.treeSha, headSha: "f".repeat(40),
        prNumber: 42, prUrl: "https://github.com/sabbour/agentweaver/pull/42",
        commentUrl: "https://github.com/sabbour/agentweaver/pull/42#issuecomment-101" }); }],
  ]) {
    await t.test(name, async (subtest) => {
      const f = correctedFixture(subtest);
      tamper(f);
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
      assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), false);
    });
  }
});

test("native findings require a linked correction receipt before using a later published tree", async (t) => {
  const f = correctedFixture(t, { source: { reviewedTreeSha: "a".repeat(40) }, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.filter((c) => c.label.includes("review:")).length, 0);
  assert.equal(f.detail.agents.some((agent) => agent.label.startsWith("v3:corrections:")), true);
});

test("new mandatory corrective paths require exact coordinator-confirmed staged scope", async (t) => {
  const extraPath = "packages/Agentweaver.Abstractions/SecretContracts.cs";
  const f = correctedFixture(t, { extraPath, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(f.source.paths.includes(extraPath), true);
  f.options.spoofScope = "Old grant APPROVED; add any path";
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Source receipt or coordinator denial changed/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:live-admission:")).length, 0);
});

test("extra staged paths or altered corrective scope cannot inherit old-tree authorization", async (t) => {
  for (const [name, options] of [
    ["no coordinator scope", { extraPath: "tests/Agentweaver.Abstractions.Tests/SecretContractsTests.cs",
      source: { scopeCommentId: undefined } }],
    ["scope comment missing paths", { extraPath: "packages/Agentweaver.Abstractions/SecretContracts.cs",
      spoofScope: "Workflow run old-run-id PR #42 issue #1742" }],
    ["scope comment from wrong tree", { extraPath: "packages/Agentweaver.Abstractions/SecretContracts.cs",
      scopeWrongTree: true }],
  ]) {
    await t.test(name, async (subtest) => {
      const f = correctedFixture(subtest, options);
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
        agent: f.agent, session: f.session });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
      assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), false);
    });
  }
});

test("scope-confirmed candidate cannot change after failed validation without fresh scope", async (t) => {
  const f = correctedFixture(t, { extraPath: "packages/Agentweaver.Abstractions/SecretContracts.cs",
    failValidationOnce: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  writeFileSync(join(f.repo.cwd, "sample.txt"), "new correction\n");
  f.repo.git("add", "sample.txt");
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Coordinator-confirmed correction tree changed/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
});

test("withdrawn old-head grant is a denial, never a new-head approval", async (t) => {
  const f = correctedFixture(t, { blockOwner: true });
  f.options.spoofDenial = `WITHDRAWN — root-1775-withdraw-${f.source.headSha.slice(0, 8)}.\n` +
    `No merge is authorized for PR 42 at HEAD ${f.source.headSha} and tree ${f.source.treeSha}.\n` +
    "Historical admission decision: APPROVED — revoked, not an active grant.";
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  assert.equal(calls.some((c) => c.label.startsWith("v3:live-admission:")), false);
});

test("owned base move retains the feature patch and validates the combined current-v1 tree", async (t) => {
  for (const upstreamSlices of [1, 2]) {
    await t.test(`${upstreamSlices} upstream slices`, async (subtest) => {
      const f = correctedFixture(subtest, { advanceBeforeRun: true, upstreamSlices, blockOwner: true });
      const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
      await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
      assert.equal(f.repo.git("merge-base", f.baseTip(), f.head()), f.baseTip());
      assert.equal(f.repo.git("show", `${f.head()}^:sample.txt`), "published");
      assert.equal(f.repo.git("show", `${f.head()}:sample.txt`), "corrected");
      assert.equal(f.repo.git("show", `${f.head()}:upstream-${upstreamSlices - 1}.txt`),
        `new v1 slice ${upstreamSlices - 1}`);
      assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 1);
      const merged = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
      assert.equal(merged.status, "merged");
      assert.equal(f.publications(), 1);
    });
  }
});

test("adjacent upstream context preserves the feature and correction deltas", async (t) => {
  const f = correctedFixture(t, { advanceBeforeRun: true, adjacentContext: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.match(f.repo.git("show", `${f.head()}^:sample.txt`),
    /^line-0-upstream\nline-1\nline-2\nline-3-published\n/);
  assert.match(f.repo.git("show", `${f.head()}:sample.txt`),
    /^line-0-upstream\nline-1\nline-2\nline-3-corrected\n/);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 1);
});

test("scope-confirmed owned rebase resumes failed validation and postpublication without losing scope", async (t) => {
  const f = correctedFixture(t, { advanceBeforeRun: true, extraPath: "tests/correction.txt",
    failValidationOnce: true, blockOwner: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 0);
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Blocked/);
  assert.equal(f.publications(), 1);
  const head = f.head();
  const result = await runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch);
  assert.equal(result.status, "merged");
  assert.equal(f.repo.git("rev-parse", "HEAD"), head);
  assert.equal(calls.filter((c) => c.label.startsWith("v3:correct-validation:")).length, 2);
});

test("active prior run cannot become a correction source", async (t) => {
  const f = correctedFixture(t, { activeOldAgent: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source },
    agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /no active agents/);
  assert.equal(calls.length, 0);
  assert.equal(f.repo.git("rev-parse", "HEAD"), f.source.headSha);
});

test("conflicting owned rebase fails before validation or publication without discarding work", async (t) => {
  const f = correctedFixture(t, { advanceBeforeRun: true, upstreamConflict: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch));
  assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), false);
  assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
  assert.match(f.repo.git("status", "--porcelain"), /sample.txt/);
});

test("owned rebase rejects a changed original feature patch even when correction paths are scoped", async (t) => {
  const f = correctedFixture(t, { advanceBeforeRun: true, upstreamSameFeature: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Owned rebase changed/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:correct-validation:")), false);
  assert.equal(calls.some((c) => c.label.startsWith("v3:publication:")), false);
});

test("corrected publication cannot redirect to another PR", async (t) => {
  const f = correctedFixture(t, { wrongPublishedPr: true });
  const { ctx, calls } = context({ args: { mode: "correct", source: f.source }, agent: f.agent, session: f.session });
  await assert.rejects(runIssueToMerge(ctx, f.repo.cwd, f.gh, f.fetch), /Publication is not bound/);
  assert.equal(calls.some((c) => c.label.startsWith("v3:admission-owner:")), false);
});

test("source receipt cannot be supplied to rehearsal or ordinary delivery", async () => {
  for (const mode of ["rehearse", "deliver"]) {
    const { ctx, calls } = context({ args: { mode, source: {} } });
    await assert.rejects(runIssueToMerge(ctx, "unused"), /Source receipt requires/);
    assert.equal(calls.length, 0);
  }
});
