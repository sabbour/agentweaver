---
"agentweaver": patch
---

Make release deployments recoverable instead of all-or-nothing. Three changes:

- **Timeouts now actually work on Windows.** `az` is a `.cmd` shim launched via `cmd.exe`, so killing the timed-out child left the `python.exe` grandchild alive holding the output pipes — the capture promise never settled and the deploy hung indefinitely despite its timeout. Timed-out commands are now terminated by process tree.
- **Transient registry failures are retried.** Idempotent ACR operations (`import`, retag, untag) retry with exponential backoff and jitter on connection resets, throttling, and timeouts, instead of failing an otherwise healthy deployment. Deterministic errors still fail immediately. Staging-tag cleanup can no longer fail a deployment at all.
- **Deployments can resume.** Completed build and deploy stages are checkpointed outside the repository, so `--resume` continues from the stage that broke rather than repeating the full four-image promotion; `--restart` discards that state. Verification always re-runs, even on a resume, and a successful deployment clears its own checkpoint.
