import { execFileSync } from "node:child_process";

const phases = [
  "Scope and classify",
  "Implement, document and record release impact",
  "Validate candidate",
  "Parallel local reviews",
  "Resolve findings",
  "Publish PR and review evidence",
  "Monitor CI and admit",
  "Cleanup handoff",
];

const stageSchema = {
  type: "object", required: ["status", "treeSha", "evidence"],
  properties: {
    status: { type: "string", enum: ["passed", "blocked"] },
    treeSha: { type: "string" },
    evidence: { type: "string" },
  },
};
const reviewSchema = {
  type: "object", required: ["status", "treeSha", "findings"],
  properties: {
    status: { type: "string", enum: ["approved", "findings", "blocked"] },
    treeSha: { type: "string" },
    findings: { type: "array", items: { type: "string" } },
  },
};
const deliverySchema = {
  type: "object", required: ["status", "headSha", "treeSha", "prNumber", "prUrl", "commentUrl", "evidence"],
  properties: {
    status: { type: "string", enum: ["published", "merged", "blocked"] },
    headSha: { type: "string" },
    treeSha: { type: "string" },
    prNumber: { type: "integer" },
    prUrl: { type: "string" },
    commentUrl: { type: "string" },
    evidence: { type: "string" },
  },
};
const ownershipSchema = {
  type: "object", required: ["status", "treeSha", "coordinatorSessionId", "authorizationId", "evidence"],
  properties: {
    status: { type: "string", enum: ["approved", "blocked"] },
    treeSha: { type: "string" },
    coordinatorSessionId: { type: "string" },
    authorizationId: { type: "string" },
    evidence: { type: "string" },
  },
};
const sha = (value) => typeof value === "string" && /^[a-f0-9]{40}$/.test(value);
const exactPath = (path) => typeof path === "string" && path.length > 0 &&
  !path.startsWith("-") && !path.startsWith("/") && !path.includes("\\") &&
  path.split("/").every((part) => part && part !== "." && part !== "..");

export async function runIssueToMerge(ctx, cwd, gh = (...args) =>
  execFileSync("gh", args, { cwd, encoding: "utf8", windowsHide: true }).trim(),
  fetchBase = (branch) => execFileSync("git", ["fetch", "--no-tags", "origin", branch], {
    cwd, encoding: "utf8", windowsHide: true,
  }),
  remoteGit = (...args) => execFileSync("git", args, {
    cwd, encoding: "utf8", windowsHide: true,
  }).trim()) {
  const a = ctx.args;
  if (!a || typeof a.task !== "string" || !a.task.trim() ||
      !Number.isSafeInteger(a.issueNumber) || a.issueNumber < 1 ||
      typeof a.milestone !== "string" || !a.milestone.trim() ||
      !Array.isArray(a.labels) || !a.labels.length ||
      a.labels.some((x) => typeof x !== "string" || !x.trim()) ||
      !a.labels.some((x) => /^type:.+/.test(x)) ||
      !a.labels.some((x) => /^area:.+/.test(x))) {
    throw new Error("Require a concrete task, positive issueNumber, milestone, existing type: and area: labels.");
  }
  const repository = a.repository ?? "sabbour/agentweaver";
  const base = a.baseBranch ?? "v1";
  const mode = a.mode ?? "rehearse";
  const issueLinked = (body) => new RegExp(
    `\\b(?:Closes|Fixes|Resolves)\\s+(?:${repository.replace("/", "\\/")}#|#)${a.issueNumber}\\b`, "i"
  ).test(body ?? "");
  if (typeof repository !== "string" ||
      !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) ||
      typeof base !== "string" || !base.trim() || base.startsWith("-") ||
      !["rehearse", "deliver", "correct", "admit", "retarget"].includes(mode)) {
    throw new Error("Invalid repository, target branch or mode.");
  }
  if (mode !== "retarget" && a.retargetSource !== undefined) {
    throw new Error("Retarget source requires retarget mode.");
  }
  if (mode === "retarget") {
    const s = a.retargetSource;
    if (!s || typeof s !== "object" || Array.isArray(s) ||
        typeof s.runId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.runId) ||
        typeof s.parentRunId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.parentRunId) ||
        s.runId === s.parentRunId ||
        !Number.isSafeInteger(s.denialCommentId) || s.denialCommentId < 1 ||
        Object.keys(s).some((key) => !["runId", "parentRunId", "denialCommentId"].includes(key))) {
      throw new Error("Retarget requires two distinct native runs and one exact denial comment.");
    }
  }
  if (mode !== "admit" && a.admissionSource !== undefined) {
    throw new Error("Admission source requires admission-continuation mode.");
  }
  if (mode === "admit") {
    const s = a.admissionSource;
    if (!s || typeof s !== "object" || Array.isArray(s) ||
        typeof s.runId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.runId) ||
        typeof s.parentRunId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.parentRunId) ||
        s.runId === s.parentRunId ||
        !Number.isSafeInteger(s.grantCommentId) || s.grantCommentId < 1 ||
        Object.keys(s).some((key) => !["runId", "parentRunId", "grantCommentId"].includes(key))) {
      throw new Error("Admission continuation requires two distinct native runs and one grant comment ID.");
    }
  }
  if (mode !== "correct" && (a.source !== undefined || a.sourceChain !== undefined)) {
    throw new Error("Source receipt requires corrected-candidate mode.");
  }
  if (mode === "correct") {
    const s = a.source;
    const chain = a.sourceChain;
    if ((s === undefined) === (chain === undefined)) {
      throw new Error("Corrected candidate requires exactly one source or sourceChain.");
    }
    if (chain !== undefined && (!chain || typeof chain !== "object" || Array.isArray(chain) ||
        typeof chain.runId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(chain.runId) ||
        typeof chain.parentRunId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(chain.parentRunId) ||
        chain.runId === chain.parentRunId ||
        !Number.isSafeInteger(chain.denialCommentId) || chain.denialCommentId < 1 ||
        (chain.scopeCommentId !== undefined &&
          (!Number.isSafeInteger(chain.scopeCommentId) || chain.scopeCommentId < 1 ||
            chain.scopeCommentId === chain.denialCommentId)) ||
        Object.keys(chain).some((key) =>
          !["runId", "parentRunId", "denialCommentId", "scopeCommentId"].includes(key)))) {
      throw new Error("Source chain requires two distinct native run IDs and an exact coordinator denial.");
    }
    if (s !== undefined && (!s || typeof s !== "object" || Array.isArray(s) ||
        typeof s.runId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.runId) ||
        !sha(s.headSha) || !sha(s.treeSha) ||
        (s.reviewedTreeSha !== undefined && !sha(s.reviewedTreeSha)) ||
        typeof s.branch !== "string" ||
        !s.branch.trim() || s.branch.startsWith("-") ||
        ![s.prNumber, s.publicationCommentId, s.denialCommentId].every((n) => Number.isSafeInteger(n) && n > 0) ||
        (s.scopeCommentId !== undefined && (!Number.isSafeInteger(s.scopeCommentId) || s.scopeCommentId < 1 ||
          [s.publicationCommentId, s.denialCommentId].includes(s.scopeCommentId))) ||
        s.publicationCommentId === s.denialCommentId ||
        !Array.isArray(s.paths) || !s.paths.length ||
        s.paths.some((path) => !exactPath(path)) || new Set(s.paths).size !== s.paths.length)) {
      throw new Error("Corrected candidate requires exact source run, PR, receipt, denial and owned paths.");
    }
  }
  if (mode === "rehearse") {
    const traversal = [];
    for (const phase of phases) {
      ctx.phase(phase);
      await ctx.step("rehearsal-v3:" + phase, async () => ({ phase }));
      traversal.push(phase);
      ctx.log("No-mutation rehearsal: " + phase);
    }
    return {
      status: "rehearsed", workflow: "agentweaver-issue-to-merge",
      issueNumber: a.issueNumber, repository, baseBranch: base, milestone: a.milestone,
      labels: a.labels, phases: traversal, agentCalls: 0, mutations: false,
      release: false, deployment: false,
      limitations: [
        "Input, phase and journal traversal only; no live delivery/tool-permission/CI/merge proof.",
        "Project-discovered workflow; run journals and resume are scoped to the initiating session.",
      ],
    };
  }

  const git = (...args) => execFileSync("git", args, { cwd, encoding: "utf8", windowsHide: true }).trim();
  const patchId = (from, to) => execFileSync("git", ["patch-id", "--verbatim"], {
    cwd, encoding: "utf8", windowsHide: true,
    input: git("diff", "--binary", "--unified=0", from, to),
  }).trim().split(/\s+/)[0];
  const live = () => ({
    branch: git("branch", "--show-current"),
    headSha: git("rev-parse", "HEAD"),
    treeSha: git("write-tree"),
    status: git("status", "--porcelain"),
    unstaged: git("diff", "--name-only"),
  });
  const initial = await ctx.step(mode === "correct" ? "initial-correct-v1" :
    mode === "retarget" ? "initial-retarget-v1" :
    mode === "admit" ? "initial-admit-v1" : "initial-v3", async () => {
    const s = live();
    if (!s.branch || s.branch === base || (mode !== "correct" && s.status) ||
        git("rev-parse", "--show-toplevel").replaceAll("\\", "/").toLowerCase() !==
          cwd.replaceAll("\\", "/").replace(/\/$/, "").toLowerCase()) {
      throw new Error("Start in a clean isolated feature worktree, not the target branch.");
    }
    return { ...s, cwd };
  });
  if (cwd !== initial.cwd || live().branch !== initial.branch) throw new Error("Caller worktree/branch changed.");
  const policy = `Current checkout only: ${cwd}; feature branch ${initial.branch}; repository ${repository}; target ${base}; issue ${a.issueNumber}. Task: ${a.task}. Existing issue type/topic labels ${JSON.stringify(a.labels)}, milestone ${JSON.stringify(a.milestone)}. Read repository instructions and canonical commands. Surgical complete changes, no speculative framework/configurability, branch protections, secrets, release, deployment, or another checkout. Use apply_patch, preserve others' changes, stage owned files only. No nested subagents or review loops. Reconcile already-completed mutations before retry: no duplicate commit/PR/comment. Return honest structured evidence; blocked work is never success.`;
  async function call(key, task, schema = stageSchema, agent, fresh = false) {
    const value = await ctx.agent(task + "\n\n" + policy, {
      label: "v3:" + key + (fresh ? ":" + crypto.randomUUID() : ""),
      schema,
      ...(agent ? { agent } : {
        agent: "general-purpose",
        model: key === "implementation" || key.startsWith("corrections:") ?
          "gpt-6.1-sol" : "gpt-6-sol",
        ...(key === "implementation" || key.startsWith("corrections:") ?
          { reasoningEffort: "medium" } : {}),
      }),
    });
    if (!value || value.status === "blocked") throw new Error("Blocked or invalid agent output at " + key + ": " + JSON.stringify(value));
    return value;
  }
  function assertStage(value) {
    const s = live();
    if (value.status !== "passed" || !sha(value.treeSha) ||
        typeof value.evidence !== "string" || !value.evidence.trim() ||
        value.treeSha !== s.treeSha || s.unstaged) {
      throw new Error("Stage is not evidenced against current fully staged tree.");
    }
  }
  async function stage(key, task) {
    return ctx.step("v3:" + key, async () => {
      const value = await call(key, task);
      assertStage(value);
      return value;
    });
  }
  async function nativePair(source, reviewedTree, requireNormalParent = false, expectFindings = true) {
    const detail = await ctx.session.workflow.getRunDetail(source.runId);
    if (detail.status !== "error" || detail.liveAgentCount !== 0 ||
        (requireNormalParent && !detail.completedAt)) {
      throw new Error("Prior native run must be terminal with no active agents.");
    }
    const prior = await ctx.session.workflow.getRun(source.runId);
    const phase = (ordinal) => detail.phases?.find((p) => p.ordinal === ordinal && p.title === phases[ordinal]);
    const role = (label, kind, ordinal) => detail.agents?.filter((agent) =>
      agent.runId === source.runId && agent.label === label &&
      agent.agentType === kind && agent.phaseId === phase(ordinal)?.id &&
      agent.status === "completed" && agent.agentId && agent.toolCallId);
    if (prior.runId !== source.runId || prior.status !== "error" ||
        !/admission-owner:/.test(prior.error ?? "") ||
        detail.runId !== source.runId || detail.workflowName !== "agentweaver-issue-to-merge" ||
        ![3, 4, 5, 6].every((ordinal) => phase(ordinal)) ||
        !Array.isArray(prior.snapshot?.journal) ||
        !["rubber-duck", "code-review"].every((kind) =>
          role(`v3:review:${reviewedTree}:${kind}`, kind, 3)?.length === 1)) {
      throw new Error("Prior native run and distinct completed review roles are not verified.");
    }
    const reviewerIds = ["rubber-duck", "code-review"].map((kind) =>
      role(`v3:review:${reviewedTree}:${kind}`, kind, 3)[0]);
    if (new Set(reviewerIds.map((agent) => agent.agentId)).size !== 2 ||
        new Set(reviewerIds.map((agent) => agent.toolCallId)).size !== 2) {
      throw new Error("One native agent cannot stand in for both review roles.");
    }
    const journal = prior.snapshot.journal;
    if (new Set(journal.map((entry) => entry.journalKey)).size !== journal.length ||
        journal.some((entry) => !/^[a-f0-9]{64}$/.test(entry.journalKey) ||
          typeof entry.resultJson !== "string")) {
      throw new Error("Prior native journal is incomplete or malformed.");
    }
    const entries = journal.map((entry) => JSON.parse(entry.resultJson));
    if (!entries.some((entry) => entry?.status === "passed" &&
        typeof entry.evidence === "string" &&
        entry.evidence.includes(`#${a.issueNumber}`) &&
        entry.evidence.includes(repository))) {
      throw new Error("Prior native scope receipt does not bind this issue and repository.");
    }
    const reviews = entries.filter((entry) => entry &&
      ["approved", "findings", "blocked"].includes(entry.status) &&
      Array.isArray(entry.findings));
    if (reviews.length !== 4 || reviews.some((entry) =>
      entry.treeSha !== reviewedTree ||
      !["approved", "findings"].includes(entry.status) ||
      (entry.status === "approved" && entry.findings.length !== 0) ||
      (entry.status === "findings" && !entry.findings.length) ||
      entry.findings.some((finding) => typeof finding !== "string" || !finding.trim()))) {
      throw new Error("The actual old-tree review pair is missing or inconsistent.");
    }
    if ([...new Set(reviews.map((entry) => JSON.stringify(entry)))].some((result) =>
      reviews.filter((entry) => JSON.stringify(entry) === result).length % 2 !== 0)) {
      throw new Error("Native agent and step review receipts disagree.");
    }
    const findings = reviews.some((entry) => entry.status === "findings");
    if (findings !== (reviewedTree !== source.treeSha) ||
        (findings && (
          role(`v3:corrections:${reviewedTree}`, "general-purpose", 4)?.length !== 1 ||
          entries.filter((entry) => entry?.status === "passed" &&
            entry.treeSha === source.treeSha && typeof entry.evidence === "string").length < 2
        ))) {
      throw new Error("The prior review findings and corrected publication tree are not linked.");
    }
    const published = entries.filter((entry) =>
      entry?.status === "published" && Number.isSafeInteger(entry.prNumber));
    if (published.length !== 2 || published.some((entry) =>
      entry.headSha !== source.headSha || entry.treeSha !== source.treeSha ||
      entry.prNumber !== source.prNumber ||
      entry.prUrl !== `https://github.com/${repository}/pull/${source.prNumber}` ||
      entry.commentUrl !== `${entry.prUrl}#issuecomment-${source.publicationCommentId}`) ||
      JSON.stringify(published[0]) !== JSON.stringify(published[1]) ||
      !detail.agents?.some((agent) =>
        agent.runId === source.runId && agent.phaseId === phase(5)?.id &&
        agent.agentType === "general-purpose" && agent.status === "completed" &&
        agent.label.startsWith(`v3:publication:${source.treeSha}:`))) {
      throw new Error("The exact native publication receipt is absent from the prior run.");
    }
    if (requireNormalParent && (!phase(0) || !phase(1) ||
        findings !== expectFindings ||
        reviews.some((review) => review.status !== (expectFindings ? "findings" : "approved")) ||
        role("v3:scope", "general-purpose", 0)?.length !== 1 ||
        role("v3:implementation", "general-purpose", 1)?.length !== 1)) {
      throw new Error("Source chain parent must be one normal delivery with actual review findings.");
    }
    return { detail, prior, entries };
  }
  async function verifySourceChain(chain, retarget = false) {
    if (chain.runId === ctx.runId ||
        !ctx.session?.workflow?.getRun || !ctx.session.workflow.getRunDetail) {
      throw new Error("Source chain requires both stopped runs in the originating session.");
    }
    const detail = await ctx.session.workflow.getRunDetail(chain.runId);
    if (detail.runId !== chain.runId || detail.workflowName !==
          (retarget ? "agentweaver-issue-to-merge-pinned-5b8" : "agentweaver-issue-to-merge") ||
        detail.status !== "error" || !detail.completedAt || detail.liveAgentCount !== 0) {
      throw new Error("Corrective source run is not terminal, stopped, and native.");
    }
    const prior = await ctx.session.workflow.getRun(chain.runId);
    if (prior.runId !== chain.runId || prior.status !== "error" ||
        !(retarget
          ? /^(?:Request workflow\.execute failed with message: )?Blocked or invalid agent output at admission-owner:/.test(prior.error ?? "")
          : /Fresh corrected publication comment does not bind source and new candidate/.test(prior.error ?? "")) ||
        !Array.isArray(prior.snapshot?.journal) ||
        new Set(prior.snapshot.journal.map((entry) => entry.journalKey)).size !== prior.snapshot.journal.length ||
        prior.snapshot.journal.some((entry) => !/^[a-f0-9]{64}$/.test(entry.journalKey) ||
          typeof entry.resultJson !== "string")) {
      throw new Error("Corrective source run lacks the exact post-publication failure and native journal.");
    }
    const entries = prior.snapshot.journal.map((entry) => JSON.parse(entry.resultJson));
    const publications = entries.filter((entry) =>
      entry?.status === "published" && Number.isSafeInteger(entry.prNumber));
    if (publications.length !== 2 || JSON.stringify(publications[0]) !== JSON.stringify(publications[1])) {
      throw new Error("Corrective source has no unique paired native publication.");
    }
    const published = publications[0];
    const commentId = Number(published.commentUrl?.match(/#issuecomment-(\d+)$/)?.[1]);
    if (!sha(published.headSha) || !sha(published.treeSha) ||
        !Number.isSafeInteger(published.prNumber) || published.prNumber < 1 ||
        published.prUrl !== `https://github.com/${repository}/pull/${published.prNumber}` ||
        !Number.isSafeInteger(commentId) || commentId < 1 ||
        published.commentUrl !== `${published.prUrl}#issuecomment-${commentId}` ||
        chain.denialCommentId === commentId) {
      throw new Error("Corrective source publication is not bound to this repository and PR.");
    }
    if (retarget && !(prior.error ?? "").includes(`admission-owner:${published.headSha}:`)) {
      throw new Error("Pinned correction stopped at a different admission head.");
    }
    const phasesByOrdinal = (ordinal) => detail.phases?.find((p) =>
      p.ordinal === ordinal && p.title === phases[ordinal]);
    const agents = detail.agents?.filter((agent) => agent.runId === chain.runId) ?? [];
    const role = (label, ordinal, prefix = false) => agents.filter((agent) =>
      (prefix ? agent.label.startsWith(label) : agent.label === label) &&
      agent.agentType === "general-purpose" && agent.phaseId === phasesByOrdinal(ordinal)?.id &&
      agent.status === "completed" && agent.agentId && agent.toolCallId);
    const validator = role(`v3:correct-validation:${published.treeSha}`, 2);
    const publisher = role(`v3:publication:${published.treeSha}:`, 5, true);
    const owners = retarget ? role(`v3:admission-owner:${published.headSha}:`, 6, true) : [];
    const validations = entries.filter((entry) =>
      entry?.status === "passed" && entry.treeSha === published.treeSha &&
      typeof entry.evidence === "string" && entry.evidence.trim());
    if (!phasesByOrdinal(2) || !phasesByOrdinal(5) ||
        (retarget && (!phasesByOrdinal(6) || !owners.length || owners.length > 2 ||
          agents.some((agent) => agent.label.startsWith("v3:admission-owner:") &&
            !owners.some((owner) => owner.agentId === agent.agentId)))) ||
        validator.length !== 1 || publisher.length !== 1 ||
        new Set([validator[0].agentId, publisher[0].agentId,
          ...owners.map((owner) => owner.agentId)]).size !== owners.length + 2 ||
        new Set([validator[0].toolCallId, publisher[0].toolCallId,
          ...owners.map((owner) => owner.toolCallId)]).size !== owners.length + 2 ||
        (retarget && agents.some((agent) =>
          ![validator[0], publisher[0], ...owners].some((owned) =>
            owned.agentId === agent.agentId && owned.toolCallId === agent.toolCallId))) ||
        agents.some((agent) => (retarget
          ? /^v3:(?:review:|live-admission:|cleanup:|scope|implementation|corrections:)/.test(agent.label)
          : /^v3:(?:review:|admission-owner:|live-admission:|cleanup:)/.test(agent.label)) ||
          agent.label.startsWith("v3:publication:") && agent.agentId !== publisher[0].agentId) ||
        entries.some((entry) => entry?.status === "merged") ||
        validations.length !== 2 || JSON.stringify(validations[0]) !== JSON.stringify(validations[1])) {
      throw new Error("Corrective source lacks its typed validation and publication, or reached admission.");
    }
    const receipts = entries.filter((entry) =>
      sha(entry?.sourceHead) && sha(entry?.sourceTree) && sha(entry?.reviewedTree) &&
      typeof entry.publicationUrl === "string" && typeof entry.denialUrl === "string" &&
      Array.isArray(entry.changed) && typeof entry.publicationBody === "string" &&
      typeof entry.denialBody === "string");
    if (receipts.length !== 1) throw new Error("Corrective source has no unique originating source receipt.");
    const parent = receipts[0];
    const parentPr = `https://github.com/${repository}/pull/${published.prNumber}`;
    const parentPublicationId = Number(parent.publicationUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    const parentDenialId = Number(parent.denialUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    if (parent.prUrl !== parentPr || !Number.isSafeInteger(parentPublicationId) ||
        !Number.isSafeInteger(parentDenialId) || parentPublicationId < 1 || parentDenialId < 1 ||
        parentPublicationId === parentDenialId ||
        parent.publicationUrl !== `${parentPr}#issuecomment-${parentPublicationId}` ||
        parent.denialUrl !== `${parentPr}#issuecomment-${parentDenialId}` ||
        !parent.changed.length || new Set(parent.changed).size !== parent.changed.length ||
        parent.changed.some((path) => !exactPath(path)) ||
        !sha(parent.stagedScopeTree) || !retarget && parent.reviewedTree === parent.sourceTree) {
      throw new Error("Corrective source receipt does not bind a distinct reviewed and published parent.");
    }
    const parentSource = {
      runId: chain.parentRunId, headSha: parent.sourceHead, treeSha: parent.sourceTree,
      reviewedTreeSha: parent.reviewedTree, prNumber: published.prNumber,
      publicationCommentId: parentPublicationId, denialCommentId: parentDenialId,
    };
    const parentRun = await nativePair(parentSource, parent.reviewedTree, true, !retarget);
    const s = live();
    const changed = retarget ? parent.changed :
      git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean);
    const committed = git("diff", "--name-only", parent.sourceHead, published.headSha)
      .split(/\r?\n/).filter(Boolean);
    const parentCount = git("rev-list", "--parents", "-n", "1", published.headSha).split(/\s+/).length;
    const origin = git("remote", "get-url", "origin")
      .replace(/^(?:https:\/\/github\.com\/|git@github\.com:|ssh:\/\/git@github\.com\/)/i, "")
      .replace(/\.git$/i, "");
    if ((!retarget && s.headSha !== published.headSha) ||
        (retarget && s.headSha !== published.headSha &&
          git("merge-base", published.headSha, s.headSha) === published.headSha) ||
        (!retarget && git("rev-parse", "HEAD^{tree}") !== published.treeSha) ||
        parentCount !== 2 || git("rev-parse", `${published.headSha}^`) !== parent.sourceHead ||
        git("rev-parse", `${parent.sourceHead}^{tree}`) !== parent.sourceTree ||
        s.unstaged || (retarget ? Boolean(s.status) : /(^|\n)\?\? /.test(s.status)) ||
        origin.toLowerCase() !== repository.toLowerCase() ||
        !changed.length || changed.length !== new Set(changed).size ||
        committed.length !== parent.changed.length ||
        committed.some((path) => !parent.changed.includes(path))) {
      throw new Error("Corrective source checkout or committed delta differs from native proof.");
    }
    const pr = JSON.parse(gh("pr", "view", String(published.prNumber), "--repo", repository,
      "--json", "number,state,headRefOid,headRefName,baseRefName,body,url,author,files" +
        (retarget ? ",mergeCommit,mergedAt" : "")));
    const mergedReplay = retarget && pr.state === "MERGED";
    if (!["OPEN", ...(retarget ? ["MERGED"] : [])].includes(pr.state) ||
        pr.number !== published.prNumber ||
        (retarget ? ![published.headSha, s.headSha].includes(pr.headRefOid) :
          pr.headRefOid !== published.headSha) ||
        (mergedReplay ? ![s.branch, null].includes(pr.headRefName) :
          pr.headRefName !== s.branch) ||
        pr.baseRefName !== base || pr.url !== published.prUrl || !issueLinked(pr.body) ||
        !Array.isArray(pr.files) || !pr.author?.login) {
      throw new Error("Corrective source PR is not open at the exact owned head and issue.");
    }
    if (mergedReplay && (pr.headRefOid !== s.headSha ||
        !sha(pr.mergeCommit?.oid) || !Number.isFinite(Date.parse(pr.mergedAt)) ||
        gh("api", `repos/${repository}/git/commits/${pr.mergeCommit.oid}`,
          "--jq", ".tree.sha") !== s.treeSha)) {
      throw new Error("Merged retarget source lacks the exact candidate merge tree.");
    }
    const comment = (id) => JSON.parse(gh("api", `repos/${repository}/issues/comments/${id}`));
    const oldPublication = comment(parentPublicationId);
    const oldDenial = comment(parentDenialId);
    const freshPublication = comment(commentId);
    const freshDenial = comment(chain.denialCommentId);
    const scope = chain.scopeCommentId === undefined ? null : comment(chain.scopeCommentId);
    const sameAuthor = [oldPublication, oldDenial, freshPublication, freshDenial].every((item) =>
      item.user?.login === pr.author.login);
    const oldPublicationAt = Date.parse(oldPublication.created_at);
    const oldDenialAt = Date.parse(oldDenial.updated_at ?? oldDenial.created_at);
    const denialAt = Date.parse(freshDenial.created_at);
    const publicationAt = Date.parse(freshPublication.created_at);
    if (!sameAuthor || oldPublication.html_url !== parent.publicationUrl ||
        oldDenial.html_url !== parent.denialUrl ||
        oldPublication.body !== parent.publicationBody || oldDenial.body !== parent.denialBody ||
        !oldPublication.body.includes(`Workflow run ${chain.parentRunId}`) ||
        !oldPublication.body.includes(parent.sourceHead) ||
        !oldPublication.body.includes(parent.sourceTree) ||
        !oldPublication.body.includes(parent.reviewedTree) ||
        !oldDenial.body.startsWith(`ADMISSION DENIED for head ${parent.sourceHead} / tree ${parent.sourceTree}`) ||
        !/Coordinator decision ID: \S+/.test(oldDenial.body) ||
        !Number.isFinite(oldPublicationAt) || !Number.isFinite(oldDenialAt) ||
        oldDenialAt <= oldPublicationAt ||
        freshPublication.html_url !== published.commentUrl ||
        !freshPublication.body?.includes(`Workflow run ${chain.runId}`) ||
        !new RegExp(`Final(?: pushed)? HEAD: \`${published.headSha}\``).test(freshPublication.body) ||
        !new RegExp(`Final(?: committed)? tree: \`${published.treeSha}\``).test(freshPublication.body) ||
        !freshPublication.body.includes(parent.sourceTree) ||
        !freshPublication.body.includes(`issuecomment-${parentDenialId}`) ||
        freshDenial.html_url !== `${pr.url}#issuecomment-${chain.denialCommentId}` ||
        !freshDenial.body?.startsWith(`ADMISSION DENIED for head ${published.headSha} / tree ${published.treeSha}`) ||
        !/Coordinator decision ID: \S+/.test(freshDenial.body) ||
        !Number.isFinite(publicationAt) || !Number.isFinite(denialAt) ||
        denialAt <= publicationAt) {
      throw new Error("Source chain comments do not bind both native publications and exact denials.");
    }
    const expanded = (retarget ? [] : changed).filter((path) =>
      !parent.changed.includes(path) || !pr.files.some((file) => file.path === path));
    const scopedPaths = scope?.body?.split(/\r?\n/).map((line) => line.match(/^- `([^`]+)`$/)?.[1])
      .filter(Boolean) ?? [];
    if (expanded.length && !scope ||
        scope && (scope.html_url !== `${pr.url}#issuecomment-${chain.scopeCommentId}` ||
          scope.user?.login !== pr.author.login ||
          !scope.body?.includes(`Workflow run ${chain.runId}`) ||
          !scope.body.includes(`PR #${published.prNumber}`) ||
          !scope.body.includes(`issue #${a.issueNumber}`) ||
          !scope.body.includes(published.headSha) || !scope.body.includes(published.treeSha) ||
          !scope.body.includes(`Staged correction tree: \`${s.treeSha}\``) ||
          !scope.body.includes(`issuecomment-${chain.denialCommentId}`) ||
          !/scope confirmation, not admission authorization/i.test(scope.body) ||
          scopedPaths.length !== changed.length || new Set(scopedPaths).size !== changed.length ||
          scopedPaths.some((path) => !changed.includes(path)) ||
          !Number.isFinite(Date.parse(scope.created_at)) ||
          Date.parse(scope.created_at) <= denialAt)) {
      throw new Error("Source chain correction scope requires exact coordinator confirmation.");
    }
    const initialReceipts = retarget ? parentRun.entries.filter((entry) =>
      entry?.branch === s.branch && sha(entry.headSha) && sha(entry.treeSha) &&
      entry.cwd === cwd && entry.status === "" && entry.unstaged === "") : [];
    const childBases = retarget ? entries.filter((entry) => sha(entry?.sha)) : [];
    if (retarget && (initialReceipts.length !== 1 || childBases.length !== 1 ||
        childBases[0].sha !== initialReceipts[0].headSha ||
        git("rev-parse", `${initialReceipts[0].headSha}^{tree}`) !== initialReceipts[0].treeSha)) {
      throw new Error("Retarget requires the clean native initial base of the normal parent.");
    }
    return {
      source: {
        runId: chain.runId, headSha: published.headSha, treeSha: published.treeSha,
        branch: s.branch, prNumber: published.prNumber, publicationCommentId: commentId,
        denialCommentId: chain.denialCommentId, paths: changed,
        ...(chain.scopeCommentId === undefined ? {} : { scopeCommentId: chain.scopeCommentId }),
      },
      original: {
        prUrl: pr.url, author: pr.author.login, publicationUrl: freshPublication.html_url,
        denialUrl: freshDenial.html_url, sourceTree: published.treeSha,
        sourceHead: published.headSha, reviewedTree: parent.reviewedTree, changed,
        stagedScopeTree: s.treeSha, publicationBody: freshPublication.body,
        denialBody: freshDenial.body, scopeBody: scope?.body ?? null,
      },
      parent: {
        runId: chain.parentRunId, reviewedTree: parent.reviewedTree,
        publishedHead: parent.sourceHead, publishedTree: parent.sourceTree,
        publicationUrl: parent.publicationUrl, denialUrl: parent.denialUrl,
      },
      ...(retarget ? { initial: initialReceipts[0], published,
        livePrHead: pr.headRefOid, mergedReplay, mergedAt: pr.mergedAt } : {}),
    };
  }
  async function verifyAdmissionSource(selector) {
    if (selector.runId === ctx.runId || selector.parentRunId === ctx.runId ||
        !ctx.session?.workflow?.getRun || !ctx.session.workflow.getRunDetail) {
      throw new Error("Admission continuation requires both native runs in the originating session.");
    }
    const detail = await ctx.session.workflow.getRunDetail(selector.runId);
    const prior = await ctx.session.workflow.getRun(selector.runId);
    if (detail.runId !== selector.runId ||
        detail.workflowName !== "agentweaver-issue-to-merge-pinned-d201" ||
        detail.status !== "error" || !detail.completedAt || detail.liveAgentCount !== 0 ||
        prior.runId !== selector.runId || prior.status !== "error" ||
        !/^(?:Request workflow\.execute failed with message: )?Blocked or invalid agent output at admission-owner:/.test(prior.error ?? "") ||
        !Array.isArray(prior.snapshot?.journal) ||
        new Set(prior.snapshot.journal.map((entry) => entry.journalKey)).size !== prior.snapshot.journal.length ||
        prior.snapshot.journal.some((entry) => !/^[a-f0-9]{64}$/.test(entry.journalKey) ||
          typeof entry.resultJson !== "string")) {
      throw new Error("Pinned native admission source is not a stopped post-publication owner attempt.");
    }
    const entries = prior.snapshot.journal.map((entry) => JSON.parse(entry.resultJson));
    const receipts = entries.filter((entry) =>
      sha(entry?.sourceHead) && sha(entry?.sourceTree) && sha(entry?.reviewedTree) &&
      typeof entry.publicationUrl === "string" && typeof entry.denialUrl === "string" &&
      Array.isArray(entry.changed) && typeof entry.publicationBody === "string" &&
      typeof entry.denialBody === "string");
    const publications = entries.filter((entry) =>
      entry?.status === "published" && Number.isSafeInteger(entry.prNumber));
    if (receipts.length !== 1 || publications.length !== 2 ||
        JSON.stringify(publications[0]) !== JSON.stringify(publications[1])) {
      throw new Error("Pinned admission source lacks unique paired native publication and parent receipt.");
    }
    const sourceReceipt = receipts[0];
    const publication = publications[0];
    const prUrl = `https://github.com/${repository}/pull/${publication.prNumber}`;
    const parentPublicationId = Number(sourceReceipt.publicationUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    const parentDenialId = Number(sourceReceipt.denialUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    const publicationId = Number(publication.commentUrl?.match(/#issuecomment-(\d+)$/)?.[1]);
    if (!sha(publication.headSha) || !sha(publication.treeSha) ||
        !Number.isSafeInteger(publication.prNumber) || publication.prNumber < 1 ||
        publication.prUrl !== prUrl ||
        ![parentPublicationId, parentDenialId, publicationId].every((id) =>
          Number.isSafeInteger(id) && id > 0) ||
        new Set([parentPublicationId, parentDenialId, publicationId, selector.grantCommentId]).size !== 4 ||
        sourceReceipt.prUrl !== prUrl ||
        sourceReceipt.publicationUrl !== `${prUrl}#issuecomment-${parentPublicationId}` ||
        sourceReceipt.denialUrl !== `${prUrl}#issuecomment-${parentDenialId}` ||
        publication.commentUrl !== `${prUrl}#issuecomment-${publicationId}` ||
        sourceReceipt.reviewedTree !== sourceReceipt.sourceTree ||
        sourceReceipt.stagedScopeTree !== publication.treeSha ||
        !sourceReceipt.changed.length || new Set(sourceReceipt.changed).size !== sourceReceipt.changed.length ||
        sourceReceipt.changed.some((path) => !exactPath(path))) {
      throw new Error("Pinned admission source is not bound to one reviewed parent and published PR.");
    }
    const parentSource = {
      runId: selector.parentRunId, headSha: sourceReceipt.sourceHead,
      treeSha: sourceReceipt.sourceTree, prNumber: publication.prNumber,
      publicationCommentId: parentPublicationId,
    };
    const parent = await nativePair(parentSource, sourceReceipt.reviewedTree);
    const parentPhase = (ordinal) => parent.detail.phases?.find((phase) =>
      phase.ordinal === ordinal && phase.title === phases[ordinal]);
    if (![0, 1].every((ordinal) => parentPhase(ordinal)) ||
        !["scope", "implementation"].every((label, ordinal) =>
          parent.detail.agents?.filter((agent) =>
            agent.runId === selector.parentRunId && agent.label === `v3:${label}` &&
            agent.phaseId === parentPhase(ordinal).id && agent.agentType === "general-purpose" &&
            agent.status === "completed" && agent.agentId && agent.toolCallId).length === 1)) {
      throw new Error("Admission source parent is not a normal native delivery.");
    }
    const phase = (ordinal) => detail.phases?.find((p) =>
      p.ordinal === ordinal && p.title === phases[ordinal]);
    const agents = detail.agents?.filter((agent) => agent.runId === selector.runId) ?? [];
    const role = (label, ordinal, prefix = false) => agents.filter((agent) =>
      (prefix ? agent.label.startsWith(label) : agent.label === label) &&
      agent.agentType === "general-purpose" && agent.phaseId === phase(ordinal)?.id &&
      agent.status === "completed" && agent.agentId && agent.toolCallId);
    const validator = role(`v3:correct-validation:${publication.treeSha}`, 2);
    const publisher = role(`v3:publication:${publication.treeSha}:`, 5, true);
    const owners = role(`v3:admission-owner:${publication.headSha}:`, 6, true);
    const validations = entries.filter((entry) =>
      entry?.status === "passed" && entry.treeSha === publication.treeSha &&
      typeof entry.evidence === "string" && entry.evidence.trim());
    const baseReceipts = entries.filter((entry) => sha(entry?.sha));
    if (![2, 5, 6].every((ordinal) => phase(ordinal)) ||
        validator.length !== 1 || publisher.length !== 1 ||
        owners.length < 1 || owners.length > 2 ||
        new Set([validator[0].agentId, publisher[0].agentId,
          ...owners.map((agent) => agent.agentId)]).size !== owners.length + 2 ||
        new Set([validator[0].toolCallId, publisher[0].toolCallId,
          ...owners.map((agent) => agent.toolCallId)]).size !== owners.length + 2 ||
        agents.some((agent) =>
          /v3:(?:review:|live-admission:|cleanup:|scope|implementation|corrections:)/.test(agent.label) ||
          agent.label.startsWith("v3:publication:") && agent.agentId !== publisher[0].agentId ||
          agent.label.startsWith("v3:admission-owner:") &&
            !owners.some((owner) => owner.agentId === agent.agentId)) ||
        entries.some((entry) => entry?.status === "merged") ||
        validations.length !== 2 || JSON.stringify(validations[0]) !== JSON.stringify(validations[1]) ||
        baseReceipts.length !== 1) {
      throw new Error("Pinned source lacks distinct native validation, publication and blocked owner roles.");
    }
    const s = live();
    const origin = git("remote", "get-url", "origin")
      .replace(/^(?:https:\/\/github\.com\/|git@github\.com:|ssh:\/\/git@github\.com\/)/i, "")
      .replace(/\.git$/i, "");
    const changed = git("diff", "--name-only", sourceReceipt.sourceHead, publication.headSha)
      .split(/\r?\n/).filter(Boolean);
    if (s.status || s.unstaged || s.headSha !== publication.headSha ||
        s.treeSha !== publication.treeSha ||
        git("rev-parse", "HEAD^{tree}") !== publication.treeSha ||
        git("rev-parse", "HEAD^") !== sourceReceipt.sourceHead ||
        git("rev-parse", `${sourceReceipt.sourceHead}^{tree}`) !== sourceReceipt.sourceTree ||
        git("rev-list", "--parents", "-n", "1", publication.headSha).split(/\s+/).length !== 2 ||
        origin.toLowerCase() !== repository.toLowerCase() ||
        changed.length !== sourceReceipt.changed.length ||
        changed.some((path) => !sourceReceipt.changed.includes(path))) {
      throw new Error("Published admission checkout differs from the owned native correction.");
    }
    const pr = JSON.parse(gh("pr", "view", String(publication.prNumber), "--repo", repository,
      "--json", "number,state,headRefOid,headRefName,baseRefName,body,url,author,statusCheckRollup,mergeCommit,mergedAt"));
    const currentBase = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
      "--jq", ".commit.sha");
    const baseTip = baseReceipts[0].sha;
    const mergedReplay = pr.state === "MERGED";
    if (pr.number !== publication.prNumber || !["OPEN", "MERGED"].includes(pr.state) ||
        pr.headRefOid !== publication.headSha || pr.headRefName !== s.branch ||
        pr.baseRefName !== base || pr.url !== prUrl || !issueLinked(pr.body) ||
        !pr.author?.login || !sha(currentBase) || (!mergedReplay && currentBase !== baseTip) ||
        git("merge-base", baseTip, publication.headSha) !== baseTip ||
        !Array.isArray(pr.statusCheckRollup) || !pr.statusCheckRollup.length ||
        pr.statusCheckRollup.some((check) =>
          !["SUCCESS", "NEUTRAL", "SKIPPED"].includes(check.conclusion ?? check.state))) {
      throw new Error("Published admission PR, issue, current base or exact-head CI is stale.");
    }
    if (mergedReplay) {
      const attempt = await ctx.session.workflow.getRunDetail(ctx.runId);
      const admissionPhase = attempt.phases?.find((p) =>
        p.ordinal === 6 && p.title === phases[6]);
      const completed = (label) => attempt.agents?.filter((agent) =>
        agent.runId === ctx.runId && agent.label.startsWith(label) &&
        agent.phaseId === admissionPhase?.id && agent.agentType === "general-purpose" &&
        agent.status === "completed" && agent.agentId && agent.toolCallId) ?? [];
      const priorOwner = completed(`v3:admission-owner:${publication.headSha}:`);
      const priorExecutor = completed(`v3:live-admission:${publication.headSha}:`);
      if (attempt.runId !== ctx.runId || !admissionPhase ||
          !priorOwner.length || !priorExecutor.length ||
          new Set([...priorOwner.map((agent) => agent.agentId),
            ...priorExecutor.map((agent) => agent.agentId)]).size !==
            priorOwner.length + priorExecutor.length ||
          new Set([...priorOwner.map((agent) => agent.toolCallId),
            ...priorExecutor.map((agent) => agent.toolCallId)]).size !==
            priorOwner.length + priorExecutor.length ||
          attempt.agents?.some((agent) => agent.runId === ctx.runId &&
            /^(?:v3:admission-owner:|v3:live-admission:)/.test(agent.label) &&
            ![...priorOwner, ...priorExecutor].some((matched) =>
              matched.agentId === agent.agentId)) ||
          !sha(pr.mergeCommit?.oid) || !Number.isFinite(Date.parse(pr.mergedAt)) ||
          gh("api", `repos/${repository}/git/commits/${pr.mergeCommit.oid}`,
            "--jq", ".tree.sha") !== publication.treeSha) {
        throw new Error("Merged admission replay lacks this run's owner/executor and exact merge tree.");
      }
    }
    const comment = (id) => JSON.parse(gh("api", `repos/${repository}/issues/comments/${id}`));
    const oldPublication = comment(parentPublicationId);
    const oldDenial = comment(parentDenialId);
    const freshPublication = comment(publicationId);
    if ([oldPublication, oldDenial, freshPublication].some((entry) =>
      entry.user?.login !== pr.author.login) ||
        oldPublication.html_url !== sourceReceipt.publicationUrl ||
        oldDenial.html_url !== sourceReceipt.denialUrl ||
        oldPublication.body !== sourceReceipt.publicationBody ||
        oldDenial.body !== sourceReceipt.denialBody ||
        !oldPublication.body.includes(`Workflow run ${selector.parentRunId}`) ||
        !oldPublication.body.includes(sourceReceipt.sourceHead) ||
        !oldPublication.body.includes(sourceReceipt.sourceTree) ||
        !oldPublication.body.includes(sourceReceipt.reviewedTree) ||
        !oldDenial.body.startsWith(`ADMISSION DENIED for head ${sourceReceipt.sourceHead} / tree ${sourceReceipt.sourceTree}`) ||
        !/Coordinator decision ID: \S+/.test(oldDenial.body) ||
        freshPublication.html_url !== publication.commentUrl ||
        !freshPublication.body?.includes(`Workflow run ${selector.runId}`) ||
        !new RegExp(`Final(?: pushed)? HEAD: \`${publication.headSha}\``).test(freshPublication.body) ||
        !new RegExp(`Final(?: committed)? tree: \`${publication.treeSha}\``).test(freshPublication.body) ||
        !freshPublication.body.includes(sourceReceipt.sourceTree) ||
        !freshPublication.body.includes(`issuecomment-${parentDenialId}`) ||
        !Number.isFinite(Date.parse(freshPublication.created_at)) ||
        Date.parse(freshPublication.created_at) <= Date.parse(oldDenial.updated_at ?? oldDenial.created_at)) {
      throw new Error("Admission source comments do not bind the native parent and correction.");
    }
    const coordinatorSessionId = "2c6ca814-3304-4ec5-9198-dab59373380f";
    const checkGrant = () => {
      const grant = comment(selector.grantCommentId);
      const first = grant.body?.split(/\r?\n/, 1)[0];
      const decision = first?.match(/^Coordinator admission decision: APPROVED [—-] ([A-Za-z0-9-]+)\.$/);
      if (grant.html_url !== `${prUrl}#issuecomment-${selector.grantCommentId}` ||
          grant.user?.login !== pr.author.login || !decision ||
          !grant.body.includes(`coordinator session ${coordinatorSessionId}`) ||
          !grant.body.includes(`PR #${publication.prNumber}`) ||
          !grant.body.includes(`HEAD ${publication.headSha}`) ||
          !grant.body.includes(`tree ${publication.treeSha}`) ||
          !grant.body.includes(`current ${base} base ${baseTip}`) ||
          !/reserved the sole admission slot/i.test(grant.body) ||
          !/no other grant exists and none has auto-merge enabled/i.test(grant.body) ||
          !Number.isFinite(Date.parse(grant.created_at)) ||
          Date.parse(grant.created_at) <= Date.parse(freshPublication.created_at) ||
          grant.updated_at !== grant.created_at ||
          (!mergedReplay && gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
            "--jq", ".commit.sha") !== baseTip) ||
          (mergedReplay && Date.parse(grant.created_at) >= Date.parse(pr.mergedAt))) {
        throw new Error("Exclusive coordinator grant does not bind the published head and current base.");
      }
      const comments = JSON.parse(gh("api", `repos/${repository}/issues/${publication.prNumber}/comments`,
        "--paginate", "--slurp")).flat();
      if (comments.some((entry) => entry.user?.login === pr.author.login &&
          Number.isFinite(Date.parse(entry.created_at)) &&
          Date.parse(entry.created_at) > Date.parse(grant.created_at) &&
          (!mergedReplay || Date.parse(entry.created_at) <= Date.parse(pr.mergedAt)) &&
          /^(?:WITHDRAWN|ADMISSION DENIED|Coordinator admission decision:)/i.test(entry.body ?? ""))) {
        throw new Error("Coordinator grant was withdrawn or superseded.");
      }
      return { coordinatorSessionId, authorizationId: grant.html_url,
        evidence: `Live coordinator decision ${decision[1]} at ${grant.html_url}; exclusive ${base} base ${baseTip}` };
    };
    return {
      publication, candidate: publication.treeSha, checkGrant, mergedReplay,
      reviewEvidence: { reviewedTree: sourceReceipt.reviewedTree,
        finalTree: publication.treeSha, historicalPublication: sourceReceipt.publicationUrl,
        coordinatorDenial: sourceReceipt.denialUrl,
        reviews: "historical old-tree reviews only; no corrected-tree approval",
        validation: validations[0].evidence, admissionBaseTip: baseTip,
        admissionSourceRunId: selector.runId },
    };
  }
  async function finishMerged(publication, reviewEvidence, ownership, admission) {
    ctx.phase(phases[7]);
    const cleanup = await stage("cleanup:" + publication.headSha,
      "Verify merged candidate and clean current worktree. Clean only this run's remote source branch if it still exists at exact published head; never another branch, force-remove worktree or recursively delete roots. This running session cannot archive itself/delete its checked-out branch. Return exact creator/app local cleanup handoff with branch/path/head, do not pretend local cleanup occurred. No extra product work. Coordinator authorization: " + JSON.stringify(ownership) + ". Merge evidence: " + admission.evidence);
    return { status: "merged", issueNumber: a.issueNumber, repository, baseBranch: base, pr: admission,
      reviewEvidence, cleanupHandoff: cleanup.evidence, release: false, deployment: false };
  }
  async function admitPublication(publication, candidate, reviewEvidence, checkGrant, mergedReplay = false) {
    if (checkGrant) checkGrant();
    ctx.phase(phases[6]);
    const ownership = mergedReplay ? {
      ...checkGrant(), treeSha: candidate,
      evidence: "Prior native owner and executor verified; " + checkGrant().evidence,
    } : await ctx.step("v3:admission-owner:" + publication.headSha, async () => {
      const grant = await call("admission-owner:" + publication.headSha,
        "Before any merge, obtain an explicit exclusive-admission decision from the parent/current coordinator for PR " +
        publication.prNumber + " at exact head " + publication.headSha + " and tree " + candidate +
        (reviewEvidence.admissionBaseTip ? " against current target tip " + reviewEvidence.admissionBaseTip : "") +
        (checkGrant ? ". The owning workflow verified the live original-session native validation/publication " +
          "and a real coordinator grant: " + JSON.stringify(checkGrant()) +
          ". Your separate child session cannot read those owner SDK runs; do not claim they are absent. " : ". ") +
        "A coordinator-authored, durable top-level comment on THIS PR is an actual response even if a cross-session reply never reaches this child session: read the comment live, and use its numeric comment ID/URL as the stable response ID. Verify its author and coordinator session ID against the actual coordinator, an explicit first-line APPROVED decision ID, exclusive sole-slot evidence, and this exact PR, head, tree and current target base; re-read for withdrawal or supersession. Do not author your own grant, accept quoted or revoked approval, infer a global lock, or waive the live merge executor's fresh head/base/CI checks. If no verifiable actual coordinator response exists, return blocked. Return approved with the coordinator session ID, comment/message ID and concrete authorization evidence; do not merge. PR: " + JSON.stringify(publication),
        ownershipSchema, undefined, true);
      if (grant.status !== "approved" || grant.treeSha !== candidate ||
          typeof grant.coordinatorSessionId !== "string" || !grant.coordinatorSessionId.trim() ||
          typeof grant.authorizationId !== "string" || !grant.authorizationId.trim() ||
          typeof grant.evidence !== "string" || !grant.evidence.trim() ||
          (checkGrant && (grant.coordinatorSessionId !== checkGrant().coordinatorSessionId ||
            grant.authorizationId !== checkGrant().authorizationId))) {
        throw new Error("Exclusive coordinator admission is not evidenced for this candidate.");
      }
      return grant;
    }, { volatile: true });
    if (checkGrant) checkGrant();
    if (!mergedReplay && reviewEvidence.admissionBaseTip &&
        gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha") !==
          reviewEvidence.admissionBaseTip) {
      throw new Error("Target tip changed after coordinator grant; request fresh admission on retry.");
    }
    const admission = await ctx.step("v3:live-admission:" + publication.headSha, async () => {
      const r = await call("live-admission:" + publication.headSha,
        (checkGrant ? "You ARE ALREADY the separate native merge executor inside owning workflow run " +
          ctx.runId + ". Its handler verified the originating session's native source, validation, publication " +
          "and live coordinator grant before spawning you. Your child session cannot read those owner SDK runs; " +
          "SessionNotFound in this child is not missing owner proof. Do NOT start, resume or reinvoke any workflow " +
          "with runFromTool, dynamic workflow tools or child-session SDK. Use direct gh CLI for fresh live GitHub " +
          "checks and the exact-head merge below, not a nested workflow. " : "") +
        (mergedReplay ? "This same-run PR is ALREADY MERGED with a verified exact merge tree and prior native owner/executor. Reconcile only: NEVER call gh pr merge again or mutate another PR. " : "") +
        "Fresh live checks: re-read PR state/base/head, target tip, actual review comment and CI to completion. Head must be " + publication.headSha + ", tree " + candidate + ", base " + base + ". Reconcile already-merged PR by exact merged-tree equivalence. Exclusive parent authorization for this head: " + JSON.stringify(ownership) + ". Block if that authorization has been withdrawn or cannot be verified; this is coordination, not a global lock. Never admit simultaneously. " +
        (mergedReplay ? "No merge action is authorized on replay. " : "Green exact candidate only: gh pr merge --rebase --match-head-commit " + publication.headSha + ". ") +
        "No squash/protection changes. " +
        (checkGrant ? "Admission continuation cannot edit, revalidate, republish or rerun CI; block on changed head/tree/base or failed required premerge CI. " :
          "Fix attributable CI failures only with affected validation; any changed head/tree/rebase must BLOCK for new candidate rather than merging stale evidence. Justified unrelated flake reruns allowed. ") +
        "Confirm actual MERGED state, merge SHA and merged tree, then manually close non-default-target issue if still open and remove status:in-progress. Return merged with original publication head/tree/number/comment and concrete fresh CI/merge evidence. PR: " + JSON.stringify(publication),
        deliverySchema, undefined, true);
      if (r.status !== "merged" || r.headSha !== publication.headSha ||
          r.treeSha !== candidate || r.prNumber !== publication.prNumber ||
          typeof r.evidence !== "string" || !r.evidence.trim()) {
        throw new Error("Actual merge not verified for exact reviewed candidate.");
      }
      return r;
    }, { volatile: true });
    const pr = JSON.parse(gh("pr", "view", String(publication.prNumber), "--repo", repository,
      "--json", "state,headRefOid,baseRefName,mergeCommit,url"));
    if (pr.state !== "MERGED" || pr.headRefOid !== publication.headSha ||
        pr.baseRefName !== base || pr.url !== publication.prUrl ||
        !sha(pr.mergeCommit?.oid) ||
        gh("api", `repos/${repository}/git/commits/${pr.mergeCommit.oid}`, "--jq", ".tree.sha") !== candidate) {
      throw new Error("Live GitHub merge does not match the reviewed head, target and final tree.");
    }
    return finishMerged(publication, reviewEvidence, ownership, admission);
  }
  if (mode === "admit") {
    ctx.phase(phases[0]);
    const receipt = await verifyAdmissionSource(a.admissionSource);
    return admitPublication(receipt.publication, receipt.candidate,
      receipt.reviewEvidence, receipt.checkGrant, receipt.mergedReplay);
  }
  if (mode === "retarget") {
    ctx.phase(phases[0]);
    const proof = await verifySourceChain(a.retargetSource, true);
    const oldHead = proof.published.headSha;
    const oldFeature = proof.parent.publishedHead;
    const initialHead = proof.initial.headSha;
    const branch = proof.source.branch;
    const prNumber = proof.source.prNumber;
    const prUrl = proof.original.prUrl;
    const ownedPaths = proof.original.changed;
    const oldPaths = git("diff", "--name-only", initialHead, oldFeature).split(/\r?\n/).filter(Boolean);
    if (git("rev-parse", `${oldFeature}^`) !== initialHead ||
        git("rev-parse", `${oldHead}^`) !== oldFeature ||
        git("rev-list", "--count", `${initialHead}..${oldHead}`) !== "2" ||
        git("rev-list", "--merges", `${initialHead}..${oldHead}`) ||
        !oldPaths.length || oldPaths.some((path) => !exactPath(path)) ||
        git("diff", "--name-only", oldFeature, oldHead).split(/\r?\n/).filter(Boolean)
          .some((path) => !ownedPaths.includes(path)) ||
        git("check-ref-format", `refs/heads/${branch}`) !== "" ||
        !patchId(initialHead, oldFeature) || !patchId(oldFeature, oldHead)) {
      throw new Error("Retarget requires exactly two scoped, linear native-owned commits.");
    }
    const replay = proof.mergedReplay ? await (async () => {
      const own = await ctx.session.workflow.getRun(ctx.runId);
      const detail = await ctx.session.workflow.getRunDetail(ctx.runId);
      const journal = own.snapshot?.journal;
      const phase = (ordinal) => detail.phases?.find((p) =>
        p.ordinal === ordinal && p.title === phases[ordinal]);
      const agents = detail.agents?.filter((agent) => agent.runId === ctx.runId) ?? [];
      const role = (prefix, ordinal) => agents.filter((agent) =>
        agent.label.startsWith(prefix) && agent.agentType === "general-purpose" &&
        agent.phaseId === phase(ordinal)?.id && agent.status === "completed" &&
        agent.agentId && agent.toolCallId);
      const validator = role(`v3:retarget-validation:${live().treeSha}`, 2);
      const owners = role(`v3:admission-owner:${live().headSha}:`, 6);
      const executors = role(`v3:live-admission:${live().headSha}:`, 6);
      if (own.runId !== ctx.runId || detail.runId !== ctx.runId ||
          !phase(2) || !phase(6) || validator.length !== 1 ||
          !owners.length || owners.length > 2 || executors.length !== 1 ||
          new Set([...validator, ...owners, ...executors].map((agent) => agent.agentId)).size !==
            owners.length + 2 ||
          new Set([...validator, ...owners, ...executors].map((agent) => agent.toolCallId)).size !==
            owners.length + 2 ||
          agents.some((agent) => /^(?:v3:admission-owner:|v3:live-admission:)/.test(agent.label) &&
            ![...owners, ...executors].some((matched) =>
              matched.agentId === agent.agentId && matched.toolCallId === agent.toolCallId)) ||
          !Array.isArray(journal) || new Set(journal.map((entry) => entry.journalKey)).size !== journal.length ||
          journal.some((entry) => !/^[a-f0-9]{64}$/.test(entry.journalKey) ||
            typeof entry.resultJson !== "string")) {
        throw new Error("Merged retarget replay lacks this run's typed owner/executor and native journal.");
      }
      const entries = journal.map((entry) => JSON.parse(entry.resultJson));
      const selected = entries.filter((entry) => sha(entry?.sha));
      const rebased = entries.filter((entry) => sha(entry?.target) && sha(entry?.headSha) &&
        entry?.branch === branch && entry.status === "" && entry.unstaged === "");
      const pushed = entries.filter((entry) =>
        entry?.oldHead === oldHead && sha(entry?.headSha) && sha(entry?.treeSha) &&
        sha(entry?.baseTip));
      const published = entries.filter((entry) =>
        entry?.status === "published" && entry?.headSha === live().headSha &&
        entry?.treeSha === live().treeSha && entry?.prNumber === prNumber);
      if ([selected, rebased, pushed, published].some((matches) => matches.length !== 1) ||
          selected[0].sha !== rebased[0].target ||
          selected[0].sha !== pushed[0].baseTip ||
          rebased[0].headSha !== pushed[0].headSha ||
          rebased[0].treeSha !== pushed[0].treeSha ||
          pushed[0].headSha !== published[0].headSha ||
          pushed[0].treeSha !== published[0].treeSha ||
          published[0].commentUrl?.startsWith(`${prUrl}#issuecomment-`) !== true) {
        throw new Error("Merged retarget replay lacks the unique selected base and published candidate.");
      }
      return { selectedBase: selected[0].sha, publication: published[0],
        entries };
    })() : null;
    const selectedBase = await ctx.step(`retarget-v1:target:${a.retargetSource.runId}`,
      async () => {
        if (proof.mergedReplay) {
          throw new Error("A merged retarget cannot select a new target base on replay.");
        }
        const tip = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
          "--jq", ".commit.sha");
        if (!sha(tip)) throw new Error("Cannot select a verified target for retarget.");
        return { sha: tip };
      });
    const target = selectedBase.sha;
    const currentBase = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
      "--jq", ".commit.sha");
    if (replay && replay.selectedBase !== target) {
      throw new Error("Merged retarget lost its native selected base.");
    }
    if (!sha(target) || target === initialHead || target === oldHead ||
        git("merge-base", initialHead, target) !== initialHead ||
        (!proof.mergedReplay && currentBase !== target)) {
      throw new Error("Retarget needs a freshly advanced descendant of the native initial base.");
    }
    if (!proof.mergedReplay) {
      fetchBase(base);
      if (git("rev-parse", "FETCH_HEAD") !== target) {
        throw new Error("Target moved during owned retarget fetch.");
      }
    }
    const featurePatch = patchId(initialHead, oldFeature);
    const correctionPatch = patchId(oldFeature, oldHead);
    const verifyMoved = () => {
      const s = live();
      if (s.branch !== branch || s.status || s.unstaged ||
          git("rev-parse", "HEAD^{tree}") !== s.treeSha ||
          git("rev-parse", `${s.headSha}~2`) !== target ||
          git("rev-list", "--count", `${target}..${s.headSha}`) !== "2" ||
          git("rev-list", "--merges", `${target}..${s.headSha}`) ||
          patchId(target, `${s.headSha}^`) !== featurePatch ||
          patchId(`${s.headSha}^`, s.headSha) !== correctionPatch ||
          JSON.stringify(git("diff", "--name-only", target, `${s.headSha}^`)
            .split(/\r?\n/).filter(Boolean)) !== JSON.stringify(oldPaths) ||
          JSON.stringify(git("diff", "--name-only", `${s.headSha}^`, s.headSha)
            .split(/\r?\n/).filter(Boolean)) !== JSON.stringify(ownedPaths)) {
        throw new Error("Rebased feature/correction slices differ from native published patches.");
      }
      return s;
    };
    const moved = await ctx.step(`retarget-v1:rebase:${a.retargetSource.runId}`, async () => {
      if (proof.mergedReplay) {
        throw new Error("A merged retarget cannot rebase on replay.");
      }
      if (live().headSha === oldHead) {
        if (gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
          "--jq", ".commit.sha") !== target || proof.livePrHead !== oldHead) {
          throw new Error("Target or same-PR old head changed before owned rebase.");
        }
        git("rebase", "--no-autostash", "--onto", target, initialHead);
      }
      return { ...verifyMoved(), target };
    });
    if (moved.target !== target || moved.headSha !== verifyMoved().headSha ||
        moved.treeSha !== live().treeSha) {
      throw new Error("Owned retarget journal differs from current two-slice commit.");
    }
    ctx.phase(phases[2]);
    const validation = await stage("retarget-validation:" + moved.treeSha,
      "Validate the rebased owned issue candidate only, tree " + moved.treeSha +
      ", base " + target + ". Run affected tests, release:validate and docs checks as applicable. " +
      "The originating owner verified the two native published commits, old reviews/denials and exact " +
      "verbatim zero-context patch IDs across the rebase; do not rerun a writer or review pair. " +
      "No commit, push, PR comment or merge. Own paths: " + JSON.stringify([...new Set([...oldPaths, ...ownedPaths])]));
    if (replay && replay.entries.filter((entry) =>
      JSON.stringify(entry) === JSON.stringify(validation)).length !== 2) {
      throw new Error("Merged retarget replay lacks the paired native validation receipt.");
    }
    if (validation.treeSha !== moved.treeSha || live().headSha !== moved.headSha ||
        (!proof.mergedReplay &&
          gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
            "--jq", ".commit.sha") !== target)) {
      throw new Error("Validated retarget changed before publication.");
    }
    const ref = `refs/heads/${branch}`;
    const remoteHead = () => {
      const remote = remoteGit("ls-remote", "--heads", "origin", ref).split(/\s+/);
      if (remote.length !== 2 || !sha(remote[0]) || remote[1] !== ref) {
        throw new Error("Owned remote ref is missing or ambiguous.");
      }
      return remote[0];
    };
    const prState = () => JSON.parse(gh("pr", "view", String(prNumber), "--repo", repository,
      "--json", "number,state,headRefOid,headRefName,baseRefName,body,url,author,statusCheckRollup,mergeCommit,mergedAt"));
    const checkPr = (allowedHeads) => {
      const pr = prState();
      if (pr.number !== prNumber ||
          (proof.mergedReplay ? pr.state !== "MERGED" : pr.state !== "OPEN") ||
          !allowedHeads.includes(pr.headRefOid) ||
          (proof.mergedReplay ? ![branch, null].includes(pr.headRefName) :
            pr.headRefName !== branch) ||
          pr.baseRefName !== base || pr.url !== prUrl || !issueLinked(pr.body) ||
          pr.author?.login !== proof.original.author ||
          (proof.mergedReplay
            ? !sha(pr.mergeCommit?.oid) ||
              !Number.isFinite(Date.parse(pr.mergedAt)) ||
              gh("api", `repos/${repository}/git/commits/${pr.mergeCommit.oid}`,
                "--jq", ".tree.sha") !== moved.treeSha
            : gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`,
                "--jq", ".commit.sha") !== target)) {
        throw new Error("Owned PR, target, issue or published head changed during retarget.");
      }
      return pr;
    };
    ctx.phase(phases[5]);
    await ctx.step(`retarget-v1:push:${a.retargetSource.runId}`, async () => {
      if (proof.mergedReplay) {
        throw new Error("A merged retarget cannot push on replay.");
      }
      verifyMoved();
      const remote = remoteHead();
      checkPr([remote]);
      if (remote === oldHead) {
        remoteGit("push", `--force-with-lease=${ref}:${oldHead}`, "origin", `HEAD:${ref}`);
      } else if (remote !== moved.headSha) {
        throw new Error("Remote head moved outside the owned old/new retarget transition.");
      }
      if (remoteHead() !== moved.headSha) {
        throw new Error("Lease-controlled publication did not reach the owned remote head.");
      }
      checkPr([moved.headSha]);
      return { headSha: moved.headSha, treeSha: moved.treeSha, oldHead, baseTip: target };
    });
    if (!proof.mergedReplay && remoteHead() !== moved.headSha) {
      throw new Error("Remote head changed after lease-controlled publication.");
    }
    checkPr([moved.headSha]);
    const publication = await ctx.step(`retarget-v1:publication:${moved.headSha}`, async () => {
      if (proof.mergedReplay) {
        throw new Error("A merged retarget cannot publish a new comment on replay.");
      }
      const comments = JSON.parse(gh("api", `repos/${repository}/issues/${prNumber}/comments`,
        "--paginate", "--slurp")).flat();
      const marker = `Workflow run ${ctx.runId} retargets native run ${a.retargetSource.runId}`;
      const body = `${marker}\nIssue #${a.issueNumber}; PR #${prNumber}; target ${base} at ${target}.\n` +
        `Original native base: \`${initialHead}\`; published correction HEAD: \`${oldHead}\` / tree \`${proof.published.treeSha}\`.\n` +
        `Coordinator denial: ${proof.original.denialUrl}\n` +
        `Two owned zero-context verbatim patches preserved: \`${featurePatch}\`, \`${correctionPatch}\`.\n` +
        `Final pushed HEAD: \`${moved.headSha}\`\nFinal committed tree: \`${moved.treeSha}\``;
      const matches = comments.filter((comment) => comment.body?.startsWith(marker));
      if (matches.length > 1 || matches.length === 1 && matches[0].body !== body) {
        throw new Error("Retarget publication marker is duplicated or edited.");
      }
      const comment = matches[0] ?? JSON.parse(gh("api", `repos/${repository}/issues/${prNumber}/comments`,
        "--method", "POST", "-f", `body=${body}`));
      if (comment.body !== body || comment.user?.login !== proof.original.author ||
          !Number.isSafeInteger(comment.id) || comment.id < 1 ||
          comment.html_url !== `${prUrl}#issuecomment-${comment.id}`) {
        throw new Error("Retarget publication comment is not bound to the owned PR and author.");
      }
      return { status: "published", headSha: moved.headSha, treeSha: moved.treeSha,
        prNumber, prUrl, commentUrl: comment.html_url, evidence: "Two exact owned patches retargeted onto " + target };
    });
    const commentId = Number(publication.commentUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    const comment = JSON.parse(gh("api", `repos/${repository}/issues/comments/${commentId}`));
    const pr = checkPr([moved.headSha]);
    if (publication.headSha !== moved.headSha || publication.treeSha !== moved.treeSha ||
        publication.prNumber !== prNumber || publication.prUrl !== prUrl ||
        comment.html_url !== publication.commentUrl ||
        comment.user?.login !== proof.original.author ||
        !comment.body?.includes(`Workflow run ${ctx.runId} retargets native run ${a.retargetSource.runId}`) ||
        !comment.body.includes(`Final pushed HEAD: \`${moved.headSha}\``) ||
        !comment.body.includes(`Final committed tree: \`${moved.treeSha}\``) ||
        !comment.body.includes(proof.original.denialUrl) ||
        !Number.isFinite(Date.parse(comment.created_at)) ||
        Date.parse(comment.created_at) <= Date.parse(
          JSON.parse(gh("api", `repos/${repository}/issues/comments/${a.retargetSource.denialCommentId}`)).created_at) ||
        !Array.isArray(pr.statusCheckRollup) || !pr.statusCheckRollup.length ||
        pr.statusCheckRollup.some((check) =>
          !["SUCCESS", "NEUTRAL", "SKIPPED"].includes(check.conclusion ?? check.state)) ||
        (!proof.mergedReplay && remoteHead() !== moved.headSha) ||
        verifyMoved().headSha !== moved.headSha) {
      throw new Error("Fresh retarget publication, exact-head CI or owned patch proof is missing.");
    }
    const coordinatorSessionId = "2c6ca814-3304-4ec5-9198-dab59373380f";
    const checkGrant = () => {
      const current = checkPr([moved.headSha]);
      if (!Array.isArray(current.statusCheckRollup) || !current.statusCheckRollup.length ||
          current.statusCheckRollup.some((check) =>
            !["SUCCESS", "NEUTRAL", "SKIPPED"].includes(check.conclusion ?? check.state)) ||
          (!proof.mergedReplay && remoteHead() !== moved.headSha)) {
        throw new Error("Retarget grant requires live green exact-head CI and unchanged remote.");
      }
      const all = JSON.parse(gh("api", `repos/${repository}/issues/${prNumber}/comments`,
        "--paginate", "--slurp")).flat();
      const grants = all.filter((entry) =>
        /^Coordinator admission decision: APPROVED [—-] [A-Za-z0-9-]+\.$/
          .test(entry.body?.split(/\r?\n/, 1)[0] ?? "") &&
        entry.body.includes(`coordinator session ${coordinatorSessionId}`) &&
        entry.body.includes(`PR #${prNumber}`) &&
        entry.body.includes(`HEAD ${moved.headSha}`) &&
        entry.body.includes(`tree ${moved.treeSha}`) &&
        entry.body.includes(`current ${base} base ${target}`) &&
        /reserved the sole admission slot/i.test(entry.body) &&
        /no other grant exists and none has auto-merge enabled/i.test(entry.body) &&
        entry.user?.login === proof.original.author &&
        Number.isSafeInteger(entry.id) && entry.id > 0 &&
        entry.html_url === `${prUrl}#issuecomment-${entry.id}` &&
        entry.updated_at === entry.created_at &&
        Number.isFinite(Date.parse(entry.created_at)) &&
        Date.parse(entry.created_at) > Date.parse(comment.created_at) &&
        (!proof.mergedReplay || Date.parse(entry.created_at) < Date.parse(current.mergedAt)));
      if (grants.length !== 1 ||
          all.some((entry) => entry.user?.login === proof.original.author &&
            Number.isFinite(Date.parse(entry.created_at)) &&
            Date.parse(entry.created_at) > Date.parse(grants[0]?.created_at) &&
            (!proof.mergedReplay || Date.parse(entry.created_at) <= Date.parse(current.mergedAt)) &&
            /^(?:WITHDRAWN|ADMISSION DENIED|Coordinator admission decision:)/i
              .test(entry.body ?? ""))) {
        throw new Error("No unique live, nonwithdrawn Root grant binds the new retarget head and base.");
      }
      return {
        coordinatorSessionId, authorizationId: grants[0].html_url,
        evidence: `Exclusive Root decision ${grants[0].body.split(/\r?\n/, 1)[0]} ` +
          `for ${moved.headSha} on current ${base} ${target}`,
      };
    };
    const ownership = checkGrant();
    const reviewEvidence = {
      reviewedTree: proof.original.reviewedTree, finalTree: moved.treeSha,
      reviews: "historical old-tree reviews only; no rebased-tree approval",
      validation: validation.evidence, admissionBaseTip: target,
      historicalPublication: proof.original.publicationUrl,
      coordinatorDenial: proof.original.denialUrl,
    };
    if (proof.mergedReplay) {
      if (JSON.stringify(replay.publication) !== JSON.stringify(publication)) {
        throw new Error("Merged retarget differs from this run's native publication and validation.");
      }
      return finishMerged(publication, reviewEvidence,
        { ...ownership, treeSha: moved.treeSha,
          evidence: "Prior same-run native owner and executor verified; " + ownership.evidence },
        { status: "merged", headSha: moved.headSha, treeSha: moved.treeSha,
          prNumber, prUrl, commentUrl: publication.commentUrl,
          evidence: "Same-run owner/executor and exact committed merge tree verified without another merge" });
    }
    return admitPublication(publication, moved.treeSha, reviewEvidence, checkGrant);
  }
  let candidate, validation, reviewEvidence, original, source;
  if (mode === "correct") {
    ctx.phase(phases[0]);
    const chainReceipt = a.sourceChain
      ? await ctx.step(`correct-v3:source-chain:${a.sourceChain.runId}`, async () =>
        verifySourceChain(a.sourceChain))
      : null;
    source = chainReceipt?.source ?? a.source;
    original = chainReceipt?.original ?? await ctx.step("correct-v1:receipt:" + source.runId, async () => {
      const s = live();
      const reviewedTree = source.reviewedTreeSha ?? source.treeSha;
      if (source.runId === ctx.runId ||
          !ctx.session?.workflow?.getRun || !ctx.session.workflow.getRunDetail) {
        throw new Error("Corrected candidate requires the originating session's native workflow receipts.");
      }
      await nativePair(source, reviewedTree);
      const origin = git("remote", "get-url", "origin")
        .replace(/^(?:https:\/\/github\.com\/|git@github\.com:|ssh:\/\/git@github\.com\/)/i, "")
        .replace(/\.git$/i, "");
      if (s.branch !== source.branch || s.headSha !== source.headSha ||
          git("rev-parse", "HEAD^{tree}") !== source.treeSha ||
          s.unstaged || /(^|\n)\?\? /.test(s.status) ||
          origin.toLowerCase() !== repository.toLowerCase()) {
        throw new Error("Corrected candidate source checkout, origin, or staged-only state does not match receipt.");
      }
      const changed = git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean);
      if (!changed.length || changed.length !== source.paths.length ||
          changed.some((path) => !source.paths.includes(path))) {
        throw new Error("Corrections must be fully staged and limited to selected owned paths.");
      }
      const pr = JSON.parse(gh("pr", "view", String(source.prNumber), "--repo", repository,
        "--json", "number,state,headRefOid,headRefName,baseRefName,body,url,author,files"));
      if (pr.state !== "OPEN" || pr.number !== source.prNumber ||
          pr.url !== `https://github.com/${repository}/pull/${source.prNumber}` ||
          pr.headRefOid !== source.headSha ||
          pr.headRefName !== source.branch || pr.baseRefName !== base ||
          !issueLinked(pr.body)) {
        throw new Error("Source PR issue, branch, base or exact head changed.");
      }
      const comment = (id) => JSON.parse(gh("api", `repos/${repository}/issues/comments/${id}`));
      const publication = comment(source.publicationCommentId);
      const denial = comment(source.denialCommentId);
      const scope = source.scopeCommentId === undefined ? null : comment(source.scopeCommentId);
      const publicationAt = Date.parse(publication.created_at);
      const denialAt = Date.parse(denial.updated_at ?? denial.created_at);
      const extraPaths = changed.filter((path) => !pr.files.some((file) => file.path === path));
      const scopePaths = scope?.body?.split(/\r?\n/).map((line) => line.match(/^- `([^`]+)`$/)?.[1])
        .filter(Boolean) ?? [];
      if (extraPaths.length && !scope) {
        throw new Error("New correction paths require a coordinator-confirmed exact staged scope.");
      }
      if (scope && (scope.html_url !== `${pr.url}#issuecomment-${source.scopeCommentId}` ||
          scope.user?.login !== pr.author?.login ||
          !scope.body?.includes(`Workflow run ${source.runId}`) ||
          !scope.body.includes(`PR #${source.prNumber}`) ||
          !scope.body.includes(`issue #${a.issueNumber}`) ||
          !scope.body.includes(source.headSha) || !scope.body.includes(source.treeSha) ||
          !scope.body.includes(`Staged correction tree: \`${s.treeSha}\``) ||
          !scope.body.includes(`issuecomment-${source.denialCommentId}`) ||
          !/scope confirmation, not admission authorization/i.test(scope.body) ||
          scopePaths.length !== changed.length || new Set(scopePaths).size !== changed.length ||
          scopePaths.some((path) => !changed.includes(path)) ||
          !Number.isFinite(Date.parse(scope.updated_at ?? scope.created_at)) ||
          Date.parse(scope.updated_at ?? scope.created_at) <= denialAt)) {
        throw new Error("Coordinator corrective scope is not bound to the exact staged paths and tree.");
      }
      if (publication.html_url !== `${pr.url}#issuecomment-${source.publicationCommentId}` ||
          denial.html_url !== `${pr.url}#issuecomment-${source.denialCommentId}` ||
          publication.user?.login !== denial.user?.login ||
          publication.user?.login !== pr.author?.login ||
          !publication.body?.includes(`Workflow run ${source.runId}`) ||
          !publication.body.includes(source.headSha) ||
          !publication.body.includes(source.treeSha) ||
          !publication.body.includes(reviewedTree) ||
          !denial.body?.includes(source.headSha) || !denial.body.includes(source.treeSha) ||
          !(denial.body.includes(`ADMISSION DENIED for head ${source.headSha} / tree ${source.treeSha}`) &&
              /Coordinator decision ID: \S+/.test(denial.body) ||
            /^WITHDRAWN[^\n]*root-\S+/i.test(denial.body) &&
              /No merge is authorized|No merge slot/i.test(denial.body)) ||
          !Number.isFinite(publicationAt) || !Number.isFinite(denialAt) ||
          denialAt <= publicationAt) {
        throw new Error("Missing or mismatched durable source reviews, publication or exact coordinator denial.");
      }
      return { prUrl: pr.url, author: pr.author.login, publicationUrl: publication.html_url, denialUrl: denial.html_url,
        sourceTree: source.treeSha, sourceHead: source.headSha, reviewedTree, changed,
        stagedScopeTree: s.treeSha, publicationBody: publication.body, denialBody: denial.body,
        scopeBody: scope?.body ?? null };
    });
    // The receipt is journaled, but the live source and its staged scope are checked on every replay.
    const baseTip = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha");
    if (!sha(baseTip)) throw new Error("Cannot verify the target tip before corrective validation.");
    const selectedBase = await ctx.step(`correct-v2:target-base:${source.runId}`, async () => ({ sha: baseTip }));
    if (selectedBase.sha !== baseTip) {
      throw new Error("Current target advanced; prior corrective validation and grant are stale.");
    }
    fetchBase(base);
    if (git("rev-parse", "FETCH_HEAD") !== baseTip) {
      throw new Error("Target changed during owned fetch; retry with a fresh base.");
    }
    const oldBase = git("merge-base", source.headSha, baseTip);
    if (oldBase === source.headSha) throw new Error("Source feature is already in the target; require a new decision.");
    const baseMove = oldBase !== baseTip;
    const prepared = baseMove ? await ctx.step(`correct-v2:base-move:${source.runId}:${baseTip}`, async () => {
      const before = live();
      if (git("rev-list", "--merges", `${oldBase}..${source.headSha}`) ||
          before.unstaged || /(^|\n)\?\? /.test(before.status)) {
        throw new Error("Owned base move requires linear source history and staged-only corrections.");
      }
      if (before.headSha === source.headSha) {
        const paths = git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean);
        if (!paths.length || paths.some((path) => !source.paths.includes(path))) {
          throw new Error("Owned base move cannot commit unstaged or unrelated corrections.");
        }
        git("commit", "-m", `fix: correct #${a.issueNumber} candidate`, "-m",
          "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>");
      }
      const committed = live();
      if (committed.status || committed.unstaged) throw new Error("Owned base move requires a clean correction commit.");
      if (git("merge-base", baseTip, committed.headSha) !== baseTip) {
        if (git("rev-parse", `${committed.headSha}^`) !== source.headSha ||
            git("diff", "--name-only", source.headSha, committed.headSha).split(/\r?\n/).filter(Boolean)
              .some((path) => !source.paths.includes(path))) {
          throw new Error("Correction commit is not a scoped child of the original published head.");
        }
        git("rebase", "--no-autostash", "--onto", baseTip, oldBase);
      }
      const after = live();
      if (after.status || after.unstaged || git("merge-base", baseTip, after.headSha) !== baseTip ||
          patchId(oldBase, source.headSha) !== patchId(baseTip, `${after.headSha}^`) ||
          patchId(source.headSha, original.stagedScopeTree) !== patchId(`${after.headSha}^`, after.headSha) ||
          git("diff", "--name-only", `${after.headSha}^`, after.headSha).split(/\r?\n/).filter(Boolean)
            .some((path) => !source.paths.includes(path))) {
        throw new Error("Owned rebase changed the original feature patch or correction scope.");
      }
      return { headSha: after.headSha, treeSha: after.treeSha, baseTip };
    }) : null;
    const current = live();
    const changed = git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean);
    const publishedReplay = current.headSha !== source.headSha;
    if (original.scopeBody && !baseMove && current.headSha === source.headSha &&
        current.treeSha !== original.stagedScopeTree) {
      throw new Error("Coordinator-confirmed correction tree changed before validation.");
    }
    const candidateReceipt = await ctx.step("correct-v1:candidate:" + source.runId + ":" + current.treeSha, async () => {
      if ((publishedReplay && !baseMove) || (!baseMove && !changed.length)) {
        throw new Error("No scoped staged or owned-rebased correction candidate to adopt.");
      }
      return { treeSha: current.treeSha };
    });
    if (current.branch !== source.branch || current.unstaged ||
        /(^|\n)\?\? /.test(current.status) || !sha(candidateReceipt.treeSha) ||
        (publishedReplay
          ? current.status ||
            (baseMove ? current.headSha !== prepared?.headSha :
              git("rev-parse", "HEAD^") !== source.headSha) ||
            git("rev-parse", "HEAD^{tree}") !== candidateReceipt.treeSha ||
            git("rev-parse", `${source.headSha}^{tree}`) !== source.treeSha ||
            git("merge-base", baseTip, current.headSha) !== baseTip ||
            (baseMove && patchId(oldBase, source.headSha) !== patchId(baseTip, `${current.headSha}^`)) ||
            (baseMove && patchId(source.headSha, original.stagedScopeTree) !==
              patchId(`${current.headSha}^`, current.headSha)) ||
            git("diff", "--name-only", `${current.headSha}^`, current.headSha)
              .split(/\r?\n/).filter(Boolean).some((path) => !source.paths.includes(path))
          : current.headSha !== source.headSha || git("rev-parse", "HEAD^{tree}") !== source.treeSha ||
            !changed.length || changed.some((path) => !source.paths.includes(path)) ||
            current.treeSha !== candidateReceipt.treeSha)) {
      throw new Error("Corrected candidate replay lost the owned source or staged scope.");
    }
    if (publishedReplay) {
      const pr = JSON.parse(gh("pr", "view", String(source.prNumber), "--repo", repository,
        "--json", "state,headRefOid,headRefName,baseRefName,url,body"));
      if (pr.state !== "OPEN" ||
          ![source.headSha, current.headSha].includes(pr.headRefOid) ||
          pr.headRefName !== source.branch || pr.baseRefName !== base || pr.url !== original.prUrl ||
          !issueLinked(pr.body)) {
        throw new Error("Published correction replay is not bound to the owned PR.");
      }
    }
    const publication = JSON.parse(gh("api", `repos/${repository}/issues/comments/${source.publicationCommentId}`));
    const denial = JSON.parse(gh("api", `repos/${repository}/issues/comments/${source.denialCommentId}`));
    const scope = source.scopeCommentId === undefined ? null :
      JSON.parse(gh("api", `repos/${repository}/issues/comments/${source.scopeCommentId}`));
    if (publication.body !== original.publicationBody || denial.body !== original.denialBody ||
        (scope?.body ?? null) !== original.scopeBody ||
        (original.scopeBody && !baseMove && current.headSha === source.headSha &&
          original.stagedScopeTree !== current.treeSha)) {
      throw new Error("Source receipt or coordinator denial changed on replay.");
    }
    candidate = candidateReceipt.treeSha;
    ctx.phase(phases[2]);
    validation = await stage("correct-validation:" + candidate,
      "Validate ONLY surgical staged corrections at tree " + candidate +
      ". Confirm they address exact coordinator denial " + original.denialUrl +
      " without material scope/architecture changes; block instead of expanding scope. Run affected tests, release:validate and docs checks as applicable; no push. Original review receipts apply ONLY to reviewed tree " +
      original.reviewedTree + ". The owning workflow already verified native terminal run " +
      source.runId + ", its distinct role-bound review pair, corrective transition, exact published head " +
      source.headSha + " / tree " + source.treeSha + ", PR publication " + original.publicationUrl +
      ", and denial using this session's SDK. Subagents have separate sessions and cannot re-read that workflow run; " +
      "do not block solely because a subagent SDK lookup says session not found. Validate this candidate and report its own evidence; " +
      "never describe the historical pair as approval of this tree. Owned rebase: " +
      JSON.stringify(prepared) + ". Paths: " + JSON.stringify(source.paths));
    const validated = await ctx.step("correct-v1:validated:" + source.runId, async () => ({ treeSha: candidate }));
    if (validated.treeSha !== candidate) {
      throw new Error("Already validated corrected candidate changed before publication.");
    }
    reviewEvidence = { reviewedTree: original.reviewedTree, finalTree: candidate,
      historicalPublication: original.publicationUrl, coordinatorDenial: original.denialUrl,
      ...(chainReceipt ? { sourceChain: chainReceipt.parent } : {}),
      reviews: "historical old-tree reviews only; no corrected-tree approval", validation,
      preparedBaseTip: baseTip, ownedRebase: prepared };
  } else {
    ctx.phase(phases[0]);
    const scope = await stage("scope", "Read issue and acceptance criteria; verify origin matches repository and current isolated branch belongs to intended target. Apply existing labels/milestone to issue; product work uses product milestone, Squad governance uses Squad. No code edits yet. Return passed with current tree SHA and concrete scope evidence.");
    ctx.phase(phases[1]);
    const implementation = await stage("implementation", "Implement issue with regression tests and directly related docs/spec/config/API updates. For each changed versioned product component, add or modify a standard .changeset/*.md record with its exact release-manifest ID, semver bump, and human summary; release prose is not a substitute. For tooling-only changes, use a version-neutral changeset with empty frontmatter when release impact warrants a record; docs/tests/CI-only changes need no record. Use existing dependency-free release validation, not an npm Changesets CLI or a publishing pipeline. No manual version bumps. Run existing docs checks where applicable. Stage owned files without commit/push. Return passed with actual staged tree SHA, owned paths, docs/release decisions and evidence. Scope: " + scope.evidence);
    candidate = implementation.treeSha;
    const beforeValidation = live();
    if (beforeValidation.headSha === initial.headSha && beforeValidation.treeSha !== candidate &&
        !beforeValidation.unstaged) {
      const changedPaths = git("diff", "--name-only", candidate, beforeValidation.treeSha).split(/\r?\n/);
      if (changedPaths.every((path) => [
        ".github/extensions/agentweaver-issue-to-merge/workflow.mjs",
        ".github/extensions/agentweaver-issue-to-merge/workflow.test.mjs",
      ].includes(path))) {
        candidate = beforeValidation.treeSha;
      }
    }
    ctx.phase(phases[2]);
    validation = await stage("validation:" + candidate, "Validate staged tree " + candidate + " with smallest exact canonical build/typecheck/lint/tests. Use coverage runners in place of duplicate plain suites where available; v1 supports locked restore, Release build, .NET coverage including real Postgres tests, native Node coverage, coverage guard tests and release:validate as relevant. Docs-only: links/diff, not unrelated suites. Install only after changed manifests or real missing dependency failures. Record commands/counts/skips/coverage paths. Coverage is not E2E. For a contract/adapter-only foundation with no deployable service, report absent deployed Azure proof as a limitation, not a blocker; separately scoped deployed-impact requirements must have exact-SHA evidence or block. Do not deploy. Implementation evidence: " + implementation.evidence);
    ctx.phase(phases[3]);
    const reviews = await ctx.parallel(["rubber-duck", "code-review"].map((kind) => () =>
    ctx.step("v3:review:" + candidate + ":" + kind, async () => {
      const r = await call("review:" + candidate + ":" + kind,
        `Read-only ${kind} review of actual staged diff and issue scope at tree ${candidate}. Real bugs, missed cases, plan mismatches only. No extra abstraction/configurability/future-proofing or out-of-scope additions. Do not edit. Return approved with empty findings, findings with precise locations/surgical fixes, or blocked. Validation: ${validation.evidence}`,
        reviewSchema, kind);
      if (r.treeSha !== candidate || !Array.isArray(r.findings) ||
          r.findings.some((f) => typeof f !== "string" || !f.trim()) ||
          (r.status === "approved" && r.findings.length) ||
          (r.status === "findings" && !r.findings.length) ||
          !["approved", "findings"].includes(r.status)) {
        throw new Error("Review result does not match candidate or findings semantics.");
      }
      return r;
    })));
    if (reviews.length !== 2 || reviews.some((r) => !r)) throw new Error("Both actual local reviews are required; a parallel failure is not approval.");
    const findings = reviews.flatMap((r) => r.findings);
    ctx.phase(phases[4]);
    let corrections = null;
    if (findings.length) {
      corrections = await stage("corrections:" + candidate, "Fix only concrete in-scope findings from the one local review pair; explicitly justify rejected scope expansion. Stage owned corrections and rerun affected validation. Record every fix/rejection and commands. No full review-pair reruns for surgical fixes. Material architecture/scope changes must block for a new decision. No commit/push. Findings: " + JSON.stringify(findings));
      candidate = corrections.treeSha;
      validation = corrections;
    }
    reviewEvidence = { reviewedTree: reviews[0].treeSha, finalTree: candidate, reviews, corrections, validation };
  }
  // Replayed intermediate steps retain their historical tree; only the final candidate must match.
  const current = live();
  if (current.treeSha !== candidate || current.unstaged) throw new Error("Current tree differs from final validated candidate; refuse stale evidence.");
  if (mode === "correct" && (
    /(^|\n)\?\? /.test(current.status) ||
    (current.headSha === source.headSha
      ? git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean)
      : git("diff", "--name-only",
        reviewEvidence.ownedRebase ? `${current.headSha}^` : source.headSha, current.headSha)
        .split(/\r?\n/).filter(Boolean)
    ).some((path) => !source.paths.includes(path)))) {
    throw new Error("Corrected candidate contains unstaged, untracked or out-of-scope files.");
  }
  if (mode === "correct" &&
      gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha") !==
        reviewEvidence.preparedBaseTip) {
    throw new Error("Target advanced before corrective publication; revalidate on a new base.");
  }
  ctx.phase(phases[5]);
  const publisherSource = mode === "correct" ?
    "The owning workflow already verified native terminal source run " + source.runId +
    ", published head " + source.headSha + " / tree " + source.treeSha +
    ", publication " + original.publicationUrl + ", exact coordinator denial " + original.denialUrl +
    ", and historical review tree " + original.reviewedTree +
    (reviewEvidence.sourceChain ? ", parent native lineage " + JSON.stringify(reviewEvidence.sourceChain) : "") +
    ". This publisher subagent has a separate session and cannot read the owning workflow's getRun/getRunDetail; " +
    "do not block solely because its own SDK lookup says session not found. Recheck the live PR, source head/tree, " +
    "denial, target and candidate with Git/GitHub before mutation; do not fabricate corrected-tree review approval. " : "";
  const publication = await ctx.step("v3:publication:" + candidate, async () => {
    const p = await call("publication:" + candidate,
      publisherSource + "Verify current tree equals " + candidate + " before commit/push. Read live target; if base changes, block for revalidation. Reconcile an already-owned candidate commit/PR before mutation, never duplicate or amend another commit. If owned rebase already committed the correction, do not commit again; otherwise commit owned staged files with Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>. Push current branch with own-branch upstream. " + (mode === "correct" ? "Reuse ONLY source PR #" + source.prNumber + " at exact source head " + source.headSha + "; never create another PR. Re-read PR head/base/branch and original denial before push; block if changed. " : "Use create_pull_request TOOL in current caller workspace with explicit base (not gh pr create); reuse matching existing PR and refuse tool head/base mismatch. ") + "Body: linked issue, actual changeset/release intent or a justified docs/tests/CI-only exemption, actual validation. Apply topic/type labels and milestone; changeset:not-required only with rationale. Post/reconcile ONE top-level PR timeline comment marked Workflow run " + ctx.runId + ", containing both actual review results, fixes/rejections, commands, final HEAD SHA/tree. For corrected candidates explicitly describe original pair as old-tree evidence, cite source receipt and denial, and DO NOT imply corrected-tree approval. Never fabricate GitHub approval. Preserve newlines via body-file. Return published with actual PR/comment URLs, PR number, exact HEAD SHA/tree and evidence. Actual review evidence: " + JSON.stringify(reviewEvidence),
      deliverySchema, undefined, true);
    if (p.status !== "published" || !Number.isSafeInteger(p.prNumber) || p.prNumber < 1 ||
        (mode === "correct" && p.prNumber !== source.prNumber) ||
        !sha(p.headSha) || p.treeSha !== candidate ||
        git("rev-parse", "HEAD") !== p.headSha || git("rev-parse", "HEAD^{tree}") !== candidate ||
        typeof p.prUrl !== "string" || !p.prUrl.startsWith("https://") ||
        typeof p.commentUrl !== "string" || !p.commentUrl.startsWith("https://") ||
        typeof p.evidence !== "string" || !p.evidence.trim()) {
      throw new Error("Publication is not bound to final validated candidate.");
    }
    return p;
  });
  if (publication.treeSha !== candidate || git("rev-parse", "HEAD") !== publication.headSha ||
      live().treeSha !== candidate || live().status) {
    throw new Error("Published caller candidate changed or worktree is not clean.");
  }
  if (mode === "correct") {
    const moved = reviewEvidence.ownedRebase;
    const previous = git("rev-parse", "HEAD^");
    if (moved
      ? (publication.headSha !== moved.headSha ||
          git("merge-base", moved.baseTip, publication.headSha) !== moved.baseTip ||
          patchId(git("merge-base", source.headSha, moved.baseTip), source.headSha) !==
            patchId(moved.baseTip, previous) ||
          patchId(source.headSha, original.stagedScopeTree) !== patchId(previous, publication.headSha))
      : (git("rev-list", "--count", `${source.headSha}..HEAD`) !== "1" ||
          previous !== source.headSha)) {
      throw new Error("Corrected publication changed the previously reviewed feature patch.");
    }
    if (git("diff", "--name-only", previous, "HEAD").split(/\r?\n/).filter(Boolean)
      .some((path) => !source.paths.includes(path))) {
      throw new Error("Corrected publication includes out-of-scope changes.");
    }
    const pr = JSON.parse(gh("pr", "view", String(publication.prNumber), "--repo", repository,
      "--json", "state,headRefOid,headRefName,baseRefName,url,statusCheckRollup"));
    if (pr.state !== "OPEN" || pr.headRefOid !== publication.headSha ||
        pr.headRefName !== source.branch || pr.baseRefName !== base ||
        pr.url !== publication.prUrl || publication.headSha === source.headSha) {
      throw new Error("Corrected publication is not on the same owned PR at a fresh head.");
    }
    if (!Array.isArray(pr.statusCheckRollup) || !pr.statusCheckRollup.length ||
        pr.statusCheckRollup.some((check) => !["SUCCESS", "NEUTRAL", "SKIPPED"].includes(check.conclusion ?? check.state))) {
      throw new Error("Fresh corrected-head CI is not green.");
    }
    const id = Number(publication.commentUrl.match(/#issuecomment-(\d+)$/)?.[1]);
    if (!Number.isSafeInteger(id) || id < 1) throw new Error("Missing corrected publication comment ID.");
    const comment = JSON.parse(gh("api", `repos/${repository}/issues/comments/${id}`));
    if (comment.html_url !== `${publication.prUrl}#issuecomment-${id}` ||
        comment.user?.login !== original.author ||
        !comment.body?.includes(`Workflow run ${ctx.runId}`) ||
        !new RegExp(`Final(?: pushed)? HEAD: \`${publication.headSha}\``).test(comment.body) ||
        !new RegExp(`Final(?: committed)? tree: \`${candidate}\``).test(comment.body) ||
        !comment.body.includes(source.treeSha) ||
        !comment.body.includes(`issuecomment-${source.denialCommentId}`)) {
      throw new Error("Fresh corrected publication comment does not bind source and new candidate.");
    }
    const baseTip = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha");
    if (!sha(baseTip) || baseTip !== reviewEvidence.preparedBaseTip) {
      throw new Error("Corrected publication is stale against the current target.");
    }
    reviewEvidence.admissionBaseTip = baseTip;
  }
  return admitPublication(publication, candidate, reviewEvidence);
}
