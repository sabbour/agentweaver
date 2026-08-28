### 2026-08-28: Publish trusted trigger tasks only after durable invocation binding
**By:** Morpheus
**What:** Schedule and event trigger tasks are created in Backlog, bound to their server-owned invocation, and then moved to Ready; failed publication deletes the provisional task and its fenced invocation.
**Why:** A coordinator can no longer reserve a run from an unbound task, while removing a failed provisional pair permits the same activation occurrence to retry without weakening fail-closed pickup.
