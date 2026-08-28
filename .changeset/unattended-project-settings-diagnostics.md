---

"agentweaver": minor

---

Add a redacted unattended automation readiness view to Project Settings. The view verifies the
live Copilot App registration, uses fixed remediation codes, and avoids exposing GitHub
credentials or provider details. Remove legacy per-project GitHub identity, webhook provisioning,
and webhook-secret settings controls in favor of the Repo App's App-level webhook.
