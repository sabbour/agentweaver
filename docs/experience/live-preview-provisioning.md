# Live-preview provisioning

Live-preview provisioning is what makes the **Build & Test** gate feel like a real running artifact, not just a log summary. When the assembled work produces a web app or service, Agentweaver starts it inside the sandbox pod, exposes it through the preview Gateway, and shows the URL before you make the final human-review decision.

For the event and tool contracts, see the [reference](../reference/live-preview-provisioning.md). For how the coordinator enforces the outcome, see the [deep dive](../deep-dive/live-preview-provisioning.md).

## When a preview appears

A preview can appear on coordinator runs that reach the platform-owned **Build & Test** gate. The backend decides applicability from the assembled diff:

- docs-only work is skipped as not applicable;
- app/server-looking work is preview-required;
- ambiguous non-doc work defaults to preview-required so absence is visible.

If preview is required, Build & Test starts the app, discovers the actual port, health-checks it, and asks to expose it. If the existing preview approval toggle is off, you will see the normal tool-approval card before any URL is provisioned. There is no separate preview bypass.

## What you see in the run tree

The coordinator run page projects the latest preview event onto the **Build & Test** row:

| State | What it means | What you can do |
| --- | --- | --- |
| **Open preview** | The Gateway URL is ready. | Open it in a new tab and inspect the assembled app before review. |
| **Preview pending approval** | The run is waiting on the existing tool-approval gate. | Approve or deny the request from the timeline approval card. |
| **Preview unavailable** | The preview failed, was denied/timed out, or no final preview outcome was recorded. | Continue to human review; inspect the reason and decide whether to approve, request changes, or decline. |

The same preview state appears in the human-review artifacts panel so you do not need to search the event timeline for the URL.

## Step by step

1. Start or open a coordinator orchestration.
2. Confirm the outcome spec and let the child subtasks complete.
3. Wait for collective assembly to run RAI and **Build & Test**.
4. If Build & Test reaches a previewable app, approve the preview request when the standard tool-approval card appears.
5. Use **Open preview** on the Build & Test row or human-review panel to inspect the running assembled app.
6. Complete human review:
   - approve if the app and diff are correct;
   - request changes if the running app exposes a product issue;
   - decline if the combined output should not land.

## What to expect

- **The port is discovered, not assumed.** The preview runner observes the actual bound port from logs or socket state before calling `start_preview`.
- **Approval policy is unchanged.** The same `AgentPreviewGate` toggle and tool-approval path govern the preview.
- **Preview is visible but not mandatory to continue.** A failed or missing preview is shown as **Preview unavailable**, but it does not block human review.
- **The URL points at the assembled tree.** During human review, the retained Build & Test AgentHost pod and worktree keep the app running against the combined output.
- **Cleanup is automatic.** Terminal run cleanup stops Gateway previews and releases the Build & Test resources best-effort.

## Related reading

- [Live-preview provisioning — Deep Dive](../deep-dive/live-preview-provisioning.md)
- [Live-preview provisioning — Reference](../reference/live-preview-provisioning.md)
- [Reviewing and Merging](../guide/review.md#build--test-preview)
- [Sandbox browser preview](./sandbox-browser-preview.md)
