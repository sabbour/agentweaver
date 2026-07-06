# Orchestration console and gates

The v0.7.12 orchestration experience is an operator console: the run tree selects scope, the graph explains execution, the session pane shows what happened, and one composer messages the coordinator. The same wave makes the Outcome plan visible as a phase and makes Build & Test a platform-owned gate that can activate preview before human review.

For implementation details see the [deep dive](../deep-dive/orchestration-console-and-gates.md). For HTTP, event, YAML, and editor contracts see the [reference](../reference/orchestration-console-and-gates.md).

## When you use it

Use the console for coordinator runs at `/projects/:projectId/orchestrations/:runId`. The route is opened after **Start orchestration** creates a coordinator run. It is also the target for coordinator rows from the board and orchestration list.

## What the page shows

::: info Screenshot is a placeholder
Replace this placeholder after the AKS docs capture pass. It should show the four-zone coordinator console with header controls, run tree, graph, and the session pane open on **Messages**.
:::

![Orchestration operator console](/screenshots/orchestration-console.png)

### Header zone

The top zone identifies the orchestration and exposes run-level controls. It keeps operator context above the workspace: status, goal identity, live stream state, and controls such as auto-approve, autopilot, retry/stop when available. The selected workflow badge is reconstructed from the persisted `coordinator.workflow_selected` event so it survives reloads (`apps/web/src/pages/CoordinatorRunPage.tsx:1207`).

### Run tree zone

The run tree is the scope selector. It includes the Coordinator plus planned nodes such as **Outcome plan** and **Work plan**, then all subtasks once the work plan is available. Planned subtasks appear even before child dispatch, so you can see the whole plan while only active children stream messages (`CoordinatorRunPage.tsx:1231`; `AgentSessionPanel.tsx:1652`).

### Graph zone

The graph is the execution model. Before confirmation, it shows the coordinator and Outcome plan path without pretending assembly stages are committed work. After confirmation, it shows the work plan and downstream subtasks, with loopbacks and gate status rendered by the shared workflow graph components (`CoordinatorRunPage.tsx:1200`, `:1244`; `WorkflowGraphPanel.tsx:616`).

### Session zone

The right pane is the operational surface. Choose **Messages** for the selected session stream, **Changes** for diffs, or **Files** for output files (`AgentSessionPanel.tsx:1705`). The composer always says **Message coordinator...**. When you selected a child, the UI still posts to the coordinator but includes the child run id as target context (`AgentSessionPanel.tsx:1580`, `:1813`).

## Outcome plan flow

1. Start an orchestration. The Outcome plan phase appears while the coordinator drafts intent.
2. Read the drafted goal, outcome, scope, assumptions, and open questions.
3. Choose **Confirm plan** to unblock dispatch, or **Clarify plan** to revise.
4. If you clarify, the same phase stays selected and the composer is prefilled with **Clarify the outcome plan:** so the next instruction goes through the coordinator (`AgentSessionPanel.tsx:1438`).
5. After confirmation, the panel collapses to a concise confirmed view with **View full plan** available (`OutcomePlanPanel.tsx:554`).

The panel does not disappear during expected early server states. If `GET /outcome-spec` returns `404` while the coordinator is still drafting, it shows **Drafting the Outcome plan...** and polls every 2 seconds (`OutcomePlanPanel.tsx:234`, `:328`, `:551`).

## Build & Test flow

When a software workflow reaches Build & Test, the gate uses the platform prompt, not an editable workflow prompt. Expect it to:

1. inspect the repository for build and test commands;
2. run the build and all available tests;
3. fail closed on compile, test, lint, or verification failure;
4. for web apps/services, start the preview server, detect the actual bound port, verify with a request, and register it with `start_preview(port=PORT)`;
5. route `approved` forward to human review, `request-changes` back to implementation, or `declined` to terminal.

This is the gate that prepares a running preview for stakeholders before the human-review step (`BuildTestTurnExecutor.cs:12`, `:17`, `:169`).

## Authoring workflows

In the workflow visual editor, choose **Add node** to add **RAI Check**, **Rubberduck Review**, **Human Review**, or **Build & Test** directly. Merge and Scribe are not authorable palette choices; existing definitions can show them as read-only platform-owned tail steps (`VisualWorkflowEditor.tsx:590`, `:654`; `workflowYaml.ts:33`).

## What to expect

- The tree controls what the session pane shows; the graph shows dependencies and gates.
- One composer is enough. Use it for whole-run steering, Outcome plan clarification, or child-targeted direction.
- Outcome plan confirmation is still the dispatch gate: no child work starts until it is confirmed.
- Build & Test is always platform-owned for `build_test` nodes. Do not add a custom prompt to that node.
- Already-resolved or expired child tool approvals now resolve gracefully with state, so refreshes and duplicate clicks no longer leave a stale actionable card as the only explanation (`RunEndpoints.cs:1544`, `:1592`).

## Related reading

- [Orchestration console and gates — Deep Dive](../deep-dive/orchestration-console-and-gates.md)
- [Orchestration console and gates — Reference](../reference/orchestration-console-and-gates.md)
- [Coordinator orchestration](./coordinator-orchestration.md)
- [Workflows & backlog](./workflows-backlog.md)
- [Sandbox browser preview](./sandbox-browser-preview.md)
