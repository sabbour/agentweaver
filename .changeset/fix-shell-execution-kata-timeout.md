---
"agentweaver": patch
---

fix(sandbox): increase shell watchdog grace and default timeout for Kata VM environments

In Kata hardware-isolation, SIGTERM relay through the Kata agent can take tens of seconds
on a cold or loaded node. The previous 60-second watchdog grace was being consumed before
the process fully exited, causing the watchdog to fatally abort agent turns with
"Shell execution exceeded its hard deadline of ~2 minutes" even when the executor had
already sent the cancellation signal.

Changes:
- `WatchdogTimeoutGrace`: 60 s → 5 min — gives Kata processes enough time to die after
  the executor's `CancelAfter` fires, preventing false-positive `shell_execution_timeout` failures.
- `DefaultTimeoutMs`: 30 s → 5 min for non-Build/Test agent contexts — prevents premature
  cancellation of legitimate long-running commands (npm install, git clone, cargo build, etc.)
  when the model doesn't supply an explicit `timeout_ms`.
