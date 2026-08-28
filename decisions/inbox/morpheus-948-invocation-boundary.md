### 2026-08-28: Fence automation invocation at run reservation
**By:** Morpheus
**What:** Triggered runs with an `automation-invocation:` source marker must obtain their unattended capability snapshots from the durable, activation-fenced invocation before activation.
**Why:** This preserves the exact activation identity across run retries through the existing snapshot-inheritance lifecycle and fails closed on absent, revoked, ambiguous, or mismatched authority without enabling trigger sources.
