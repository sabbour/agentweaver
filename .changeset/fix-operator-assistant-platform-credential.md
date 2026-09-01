---
"agentweaver": minor
---

Fixed personal/Operator ("Assistant") sessions incorrectly requiring a PROJECT-scoped
run-bound GitHub Copilot capability snapshot, instead of always resolving their credential
from the platform-level Copilot connection.

Follow-up to #1116: that fix only corrected the *symptom* (surfacing the actionable
"Connect GitHub" 409 instead of an opaque 500) for a personal Assistant session whose run
happened to carry a non-null `ProjectId` — the project the caller was viewing when they
opened the chat. It did not address the underlying gap: that `ProjectId` is only incidental
UI context, never a real repo-scoped run, yet credential resolution still required THAT
project's own Copilot binding to be usable. If the incidental project's binding was broken
(the exact production case: an "Active" DB row with a missing Key Vault secret), the
personal session failed even though the platform-level Copilot connection was healthy.

`RunGitHubCapabilitySnapshotLifecycle.PrepareForUnattendedCopilotLaunchAsync` now accepts a
`platformScoped` flag. Both Assistant/Operator run launch paths
(`AssistantRunService.PrepareAgentHostCapabilityAsync` and
`RemoteOperatorAssistantAgent.EnsureAgentHostCapabilityAsync`) now pass `platformScoped:
true`, so these personal sessions always resolve their Copilot credential from the
platform-level connection (`PlatformDefaultCopilotBindings`) regardless of any incidental
`ProjectId`, and any failure now correctly surfaces the platform-settings "Connect GitHub"
CTA rather than a project-specific one. Project-scoped work (Coordinator runs, subtasks,
retries) is unchanged and continues to require its own project-bound capability snapshot.
