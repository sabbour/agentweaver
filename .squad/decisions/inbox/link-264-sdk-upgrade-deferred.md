### 2026-07-13: Defer Copilot SDK upgrade; ship Reject decisions only
**By:** Link
**Issue:** #264

NuGet has no `Microsoft.Agents.AI.GitHub.Copilot` release newer than `1.13.0-rc1`. A clean build with the current adapter (`1.11.1-rc1`) and an explicit unified `GitHub.Copilot.SDK` `1.0.6` reference still fails with CS0012 because the adapter requires the unsigned `GitHub.Copilot.SDK, Version=1.0.0.0, PublicKeyToken=null` identity. The newest available combination was also tested (`Microsoft.Agents.AI.GitHub.Copilot` `1.13.0-rc1` plus `GitHub.Copilot.SDK` `1.0.7-preview.2`, with its required Microsoft.Extensions abstractions at `10.0.9`) and fails with the same CS0012 errors.

Do not vendor or source-compile the MAF adapter. Keep the repository's working `GitHub.Copilot.SDK` `1.0.2` and the AgentHost CLI pin unchanged in issue #264. Ship only the permission-handler correction: governance, policy, fail-closed, and operator denials return `PermissionDecision.Reject(feedback)`.

SDK/CLI alignment (`GitHub.Copilot.SDK` `1.0.2` versus CLI `1.0.67` in AgentHost) remains a separate, lower-urgency follow-up. Revisit it when Microsoft publishes `Microsoft.Agents.AI.GitHub.Copilot` against a newer, strong-named `GitHub.Copilot.SDK` compatible with CLI `1.0.67` or later.
