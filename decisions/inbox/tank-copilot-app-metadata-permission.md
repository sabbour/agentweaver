### 2026-08-27: Permit only GitHub's mandatory metadata read permission
**By:** Tank
**What:** Copilot App registration validation accepts only `permissions: { "metadata": "read" }`; all other permission maps remain fail-closed.
**Why:** GitHub adds this mandatory non-repository permission to every App registration, while Agentweaver must continue rejecting all extra repository permissions.