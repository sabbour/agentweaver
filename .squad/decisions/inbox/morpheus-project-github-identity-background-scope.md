# Morpheus decision: project GitHub identity is durable execution context

- Context: PR #719 originally selected a linked GitHub identity through `HttpContext.Items`.
  That protected request-local consumers but disappeared when coordinator, runtime, assistant,
  skill, repository, or remote AgentHost work continued after the request.
- Decision: carry both the authenticated user subject and project ID into every project-scoped
  GitHub token consumer, then resolve the user's project override from durable storage. Preserve
  request-local selection only as a validated fast path after project RBAC. Backlog tasks persist
  the capture display login separately from the durable authentication subject used at pickup.
- Boundaries: missing project context keeps the user's global default; overrides remain keyed by
  `(project_id, entra_user_id)`; unlinked identities cannot be selected; webhook/schedule
  automation keeps its fail-closed automation principal; project roles are checked before request
  context is recorded or project-scoped assistant work is created.
- Rationale: explicit execution context prevents background work from silently reverting to another
  linked account without introducing reverse login-to-user lookup, cross-user credential borrowing,
  or global-default behavior changes.
