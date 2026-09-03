---
"agentweaver": patch
---

Fix interactive Sessions silently ignoring the platform model provider, and pin-for-life provider
selection.

Session provider SELECTION resolved against the project the caller happened to be viewing, while the
same session's credential CHECK was deliberately PLATFORM-scoped. Per
`EffectiveModelProviderResolver`'s documented precedence an active project GitHub Copilot binding
always beats platform-level BYOK, so a lingering project binding silently overrode a deployment-wide
switch to BYOK — Sessions kept reporting and behaving as GitHub Copilot — and the two scopes could
disagree about which provider the run was even on. `AssistantRunService` now resolves the session
provider at platform scope (`projectId: null`), matching the credential fence
(`platformScoped: true`) that both `AssistantRunService.PrepareAgentHostCapabilityAsync` and
`RemoteOperatorAssistantAgent.EnsureAgentHostCapabilityAsync` already used. A session's `ProjectId`
stays on the run purely as incidental MCP/UI context.

The effective provider is also re-resolved at the START OF EVERY TURN instead of once at session
creation, so a mid-conversation platform provider change takes effect on the next message rather than
requiring a brand-new session. A changed provider is applied transparently to the next turn: the
persisted `ModelSource` is repointed (new `IRunStore.UpdateModelSourceAsync`), the conversation keeps
its history, and when the new provider is GitHub Copilot the platform-scoped capability gate runs
immediately so an unusable platform connection fails fast with the "Connect GitHub" CTA.
