import { fileURLToPath } from "node:url";
import { defineWorkflow, joinSession } from "@github/copilot-sdk/extension";
import { runIssueToMerge } from "./workflow.mjs";

const cwd = fileURLToPath(new URL("../../../", import.meta.url));

await joinSession({
  workflows: [defineWorkflow({
    meta: {
      name: "agentweaver-issue-to-merge",
      description: "Project-scoped issue delivery in this isolated feature worktree. Defaults to zero-agent, zero-mutation rehearsal; delivery requires explicit authorization. Runs and journals belong to the initiating session. Does not release or deploy.",
      phases: [
        { title: "Scope and classify" },
        { title: "Implement, document and record release impact" },
        { title: "Validate candidate" },
        { title: "Parallel local reviews" },
        { title: "Resolve findings" },
        { title: "Publish PR and review evidence" },
        { title: "Monitor CI and admit" },
        { title: "Cleanup handoff" },
      ],
      argsSchema: {
        type: "object",
        required: ["task", "issueNumber", "milestone", "labels"],
        properties: {
          task: { type: "string" },
          issueNumber: { type: "integer" },
          milestone: { type: "string" },
          labels: { type: "array", items: { type: "string" } },
          repository: { type: "string" },
          baseBranch: { type: "string" },
          mode: { type: "string", enum: ["rehearse", "deliver", "correct"] },
          source: {
            type: "object",
            required: ["runId", "headSha", "treeSha", "branch", "prNumber", "publicationCommentId", "denialCommentId", "paths"],
            properties: {
              runId: { type: "string" },
              headSha: { type: "string" },
              treeSha: { type: "string" },
              reviewedTreeSha: { type: "string" },
              branch: { type: "string" },
              prNumber: { type: "integer" },
              publicationCommentId: { type: "integer" },
              denialCommentId: { type: "integer" },
              scopeCommentId: { type: "integer" },
              paths: { type: "array", items: { type: "string" } },
            },
          },
        },
      },
    },
    run: (ctx) => runIssueToMerge(ctx, cwd),
  })],
});
