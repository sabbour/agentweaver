# Decoupled live-preview provisioning

When a coordinator run reaches **Build & Test**, Agentweaver now tries to show you the assembled app running before you make the final human-review decision. This preview is a platform step after Build & Test, not something the Build & Test agent may or may not do.

For implementation details, see the [deep dive](../deep-dive/live-preview-provisioning.md). For event and endpoint details, see the [reference](../reference/live-preview-provisioning.md).

## When it runs

The preview step runs after Build & Test for:

- Build & Test **approved**;
- Build & Test **requested changes**.

It skips when Build & Test **declines**, because the gate is already terminal. It also self-skips when the deployment cannot produce a reachable Gateway preview, and records that as a skipped preview outcome. There is no feature flag; this is the default behavior.

## What you see

| State | Meaning | What to do |
| --- | --- | --- |
| **Open preview** | The assembled app is reachable at a Gateway preview URL. | Open it and inspect the running result before approving or requesting changes. |
| **Preview pending approval** | The existing preview approval gate is waiting for your decision. | Approve or deny the normal tool-approval card. |
| **Preview unavailable** | The app could not be started, a port was not found, approval failed, or registration failed. | Continue review; preview failure does not block you. |
| No preview state | Preview was not applicable or was skipped by infrastructure. | Review the diff normally. |

The preview URL appears on the Build & Test row and in the human-review artifacts panel.

## Step by step

1. Start a coordinator run and confirm the outcome spec.
2. Let child subtasks finish and collective assembly run.
3. Build & Test evaluates the assembled tree.
4. If the verdict is approved or request-changes, Agentweaver starts the preview step.
5. If approval is required, approve the preview request in the normal tool-approval card.
6. Open the preview URL if it is available.
7. Complete human review: approve, request changes, or decline based on the diff and the running app.

## What to expect

- **Actual port discovery.** The platform starts the app and observes the port it really bound to; it does not assume port `3000` and does not inject `PORT=3000` or `--port`.
- **Pod-IP reachability.** AgentHost runs the app and TCP forwarder inside the sandbox pod, with the forwarder listening on `0.0.0.0` on an allowed public port, so the Gateway URL works even when the app only listened on `127.0.0.1`.
- **Gateway is the real path.** The API does not probe the sandbox pod directly; opening **Open preview** exercises the same Gateway hostname users rely on.
- **Verdict independence.** A Build & Test request-changes verdict can still produce a preview so you can inspect what failed or what needs polish.
- **Preview failure is non-blocking.** A failed preview is visible as **Preview unavailable**, but it never forces a changes request and never prevents human review.
- **Credential isolation.** The preview-runner credential is per-run, delivered in memory, scrubbed from child process environment, and deleted on terminal cleanup or orphan reaping.

## Related reading

- [Decoupled live-preview provisioning — Deep Dive](../deep-dive/live-preview-provisioning.md)
- [Decoupled live-preview provisioning — Reference](../reference/live-preview-provisioning.md)
- [Reviewing and Merging](../guide/review.md#build--test-preview)
- [Sandbox browser preview](./sandbox-browser-preview.md)
