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
  })) {
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
      !["rehearse", "deliver", "correct"].includes(mode)) {
    throw new Error("Invalid repository, target branch or mode.");
  }
  if (mode !== "correct" && a.source !== undefined) throw new Error("Source receipt requires corrected-candidate mode.");
  if (mode === "correct") {
    const s = a.source;
    if (!s || typeof s.runId !== "string" || !/^[a-zA-Z0-9-]{8,80}$/.test(s.runId) ||
        !sha(s.headSha) || !sha(s.treeSha) ||
        (s.reviewedTreeSha !== undefined && !sha(s.reviewedTreeSha)) ||
        typeof s.branch !== "string" ||
        !s.branch.trim() || s.branch.startsWith("-") ||
        ![s.prNumber, s.publicationCommentId, s.denialCommentId].every((n) => Number.isSafeInteger(n) && n > 0) ||
        (s.scopeCommentId !== undefined && (!Number.isSafeInteger(s.scopeCommentId) || s.scopeCommentId < 1 ||
          [s.publicationCommentId, s.denialCommentId].includes(s.scopeCommentId))) ||
        s.publicationCommentId === s.denialCommentId ||
        !Array.isArray(s.paths) || !s.paths.length ||
        s.paths.some((path) => !exactPath(path)) || new Set(s.paths).size !== s.paths.length) {
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
  const initial = await ctx.step(mode === "correct" ? "initial-correct-v1" : "initial-v3", async () => {
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
  let candidate, validation, reviewEvidence, original;
  if (mode === "correct") {
    ctx.phase(phases[0]);
    const source = a.source;
    original = await ctx.step("correct-v1:receipt:" + source.runId, async () => {
      const s = live();
      const reviewedTree = source.reviewedTreeSha ?? source.treeSha;
      if (source.runId === ctx.runId ||
          !ctx.session?.workflow?.getRun || !ctx.session.workflow.getRunDetail) {
        throw new Error("Corrected candidate requires the originating session's native workflow receipts.");
      }
      const detail = await ctx.session.workflow.getRunDetail(source.runId);
      if (detail.status !== "error" || detail.liveAgentCount !== 0) {
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
          detail.status !== "error" ||
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
    (current.headSha === a.source.headSha
      ? git("diff", "--cached", "--name-only", "-z").split("\0").filter(Boolean)
      : git("diff", "--name-only",
        reviewEvidence.ownedRebase ? `${current.headSha}^` : a.source.headSha, current.headSha)
        .split(/\r?\n/).filter(Boolean)
    ).some((path) => !a.source.paths.includes(path)))) {
    throw new Error("Corrected candidate contains unstaged, untracked or out-of-scope files.");
  }
  if (mode === "correct" &&
      gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha") !==
        reviewEvidence.preparedBaseTip) {
    throw new Error("Target advanced before corrective publication; revalidate on a new base.");
  }
  ctx.phase(phases[5]);
  const publication = await ctx.step("v3:publication:" + candidate, async () => {
    const p = await call("publication:" + candidate,
      "Verify current tree equals " + candidate + " before commit/push. Read live target; if base changes, block for revalidation. Reconcile an already-owned candidate commit/PR before mutation, never duplicate or amend another commit. If owned rebase already committed the correction, do not commit again; otherwise commit owned staged files with Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>. Push current branch with own-branch upstream. " + (mode === "correct" ? "Reuse ONLY source PR #" + a.source.prNumber + " at exact source head " + a.source.headSha + "; never create another PR. Re-read PR head/base/branch and original denial before push; block if changed. " : "Use create_pull_request TOOL in current caller workspace with explicit base (not gh pr create); reuse matching existing PR and refuse tool head/base mismatch. ") + "Body: linked issue, actual changeset/release intent or a justified docs/tests/CI-only exemption, actual validation. Apply topic/type labels and milestone; changeset:not-required only with rationale. Post/reconcile ONE top-level PR timeline comment marked Workflow run " + ctx.runId + ", containing both actual review results, fixes/rejections, commands, final HEAD SHA/tree. For corrected candidates explicitly describe original pair as old-tree evidence, cite source receipt and denial, and DO NOT imply corrected-tree approval. Never fabricate GitHub approval. Preserve newlines via body-file. Return published with actual PR/comment URLs, PR number, exact HEAD/tree and evidence. Actual review evidence: " + JSON.stringify(reviewEvidence),
      deliverySchema, undefined, true);
    if (p.status !== "published" || !Number.isSafeInteger(p.prNumber) || p.prNumber < 1 ||
        (mode === "correct" && p.prNumber !== a.source.prNumber) ||
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
          patchId(git("merge-base", a.source.headSha, moved.baseTip), a.source.headSha) !==
            patchId(moved.baseTip, previous) ||
          patchId(a.source.headSha, original.stagedScopeTree) !== patchId(previous, publication.headSha))
      : (git("rev-list", "--count", `${a.source.headSha}..HEAD`) !== "1" ||
          previous !== a.source.headSha)) {
      throw new Error("Corrected publication changed the previously reviewed feature patch.");
    }
    if (git("diff", "--name-only", previous, "HEAD").split(/\r?\n/).filter(Boolean)
      .some((path) => !a.source.paths.includes(path))) {
      throw new Error("Corrected publication includes out-of-scope changes.");
    }
    const pr = JSON.parse(gh("pr", "view", String(publication.prNumber), "--repo", repository,
      "--json", "state,headRefOid,headRefName,baseRefName,url,statusCheckRollup"));
    if (pr.state !== "OPEN" || pr.headRefOid !== publication.headSha ||
        pr.headRefName !== a.source.branch || pr.baseRefName !== base ||
        pr.url !== publication.prUrl || publication.headSha === a.source.headSha) {
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
        !comment.body.includes(`Final HEAD: \`${publication.headSha}\``) ||
        !comment.body.includes(`Final tree: \`${candidate}\``) ||
        !comment.body.includes(a.source.treeSha) ||
        !comment.body.includes(`issuecomment-${a.source.denialCommentId}`)) {
      throw new Error("Fresh corrected publication comment does not bind source and new candidate.");
    }
    const baseTip = gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha");
    if (!sha(baseTip) || baseTip !== reviewEvidence.preparedBaseTip) {
      throw new Error("Corrected publication is stale against the current target.");
    }
    reviewEvidence.admissionBaseTip = baseTip;
  }
  ctx.phase(phases[6]);
  const ownership = await ctx.step("v3:admission-owner:" + publication.headSha, async () => {
    const grant = await call("admission-owner:" + publication.headSha,
      "Before any merge, obtain an explicit exclusive-admission decision from the parent/current coordinator for PR " +
      publication.prNumber + " at exact head " + publication.headSha + " and tree " + candidate +
      (mode === "correct" ? " against current target tip " + reviewEvidence.admissionBaseTip : "") +
      ". Use an actual coordinator response, not your own declaration or an assumed global lock. If unavailable, return blocked. Return approved with the coordinator session ID, response/message ID and concrete authorization evidence; do not merge. PR: " + JSON.stringify(publication),
      ownershipSchema, undefined, true);
    if (grant.status !== "approved" || grant.treeSha !== candidate ||
        typeof grant.coordinatorSessionId !== "string" || !grant.coordinatorSessionId.trim() ||
        typeof grant.authorizationId !== "string" || !grant.authorizationId.trim() ||
        typeof grant.evidence !== "string" || !grant.evidence.trim()) {
      throw new Error("Exclusive coordinator admission is not evidenced for this candidate.");
    }
    return grant;
  }, { volatile: true });
  if (mode === "correct" &&
      gh("api", `repos/${repository}/branches/${encodeURIComponent(base)}`, "--jq", ".commit.sha") !==
        reviewEvidence.admissionBaseTip) {
    throw new Error("Target tip changed after coordinator grant; request fresh admission on retry.");
  }
  const admission = await ctx.step("v3:live-admission:" + publication.headSha, async () => {
    const r = await call("live-admission:" + publication.headSha,
      "Fresh live checks: re-read PR state/base/head, target tip, actual review comment and CI to completion. Head must be " + publication.headSha + ", tree " + candidate + ", base " + base + ". Reconcile already-merged PR by exact merged-tree equivalence. Exclusive parent authorization for this head: " + JSON.stringify(ownership) + ". Block if that authorization has been withdrawn or cannot be verified; this is coordination, not a global lock. Never admit simultaneously. Green exact candidate only: gh pr merge --rebase --match-head-commit " + publication.headSha + ". No squash/protection changes. Fix attributable CI failures only with affected validation; any changed head/tree/rebase must BLOCK for new candidate rather than merging stale evidence. Justified unrelated flake reruns allowed. Confirm actual MERGED state, merge SHA and merged tree, then manually close non-default-target issue if still open and remove status:in-progress. Return merged with original publication head/tree/number/comment and concrete fresh CI/merge evidence. PR: " + JSON.stringify(publication),
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
  ctx.phase(phases[7]);
  const cleanup = await stage("cleanup:" + publication.headSha,
    "Verify merged candidate and clean current worktree. Clean only this run's remote source branch if it still exists at exact published head; never another branch, force-remove worktree or recursively delete roots. This running session cannot archive itself/delete its checked-out branch. Return exact creator/app local cleanup handoff with branch/path/head, do not pretend local cleanup occurred. No extra product work. Coordinator authorization: " + JSON.stringify(ownership) + ". Merge evidence: " + admission.evidence);
  return { status: "merged", issueNumber: a.issueNumber, repository, baseBranch: base, pr: admission, reviewEvidence, cleanupHandoff: cleanup.evidence, release: false, deployment: false };
}
