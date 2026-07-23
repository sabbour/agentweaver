---
"agentweaver": patch
---

Harden the GitHub webhook trust boundary with regression tests that lock in existing
security properties: reject a delivery signed with a different project's secret (proving
per-project secret scoping, not a shared global secret), and prove prompt-injection text
smuggled in issue/comment payload fields never reaches the fired backlog task. Also correct
a stale doc comment that claimed no webhook receiver was wired.
