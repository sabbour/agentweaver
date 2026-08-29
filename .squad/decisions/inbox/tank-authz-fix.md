# Tank decision: legacy AllowedTeam overlap stays warn-and-continue

- Context: PR #631 changed GitHub org authorization from legacy `AllowedOrg` + `AllowedTeam`
  AND semantics to a mixed OR rule list. If operators configure `AllowedOrg=org` and the legacy
  `AllowedTeam=org/team`, appending the team as an extra OR rule silently widens access to the
  full org.
- Decision: keep startup as warn-and-continue, but detect the overlap and emit a prominent warning
  that names the exact org/team and the effective rule set. Do not append the legacy team as an
  independent OR rule in that overlap case.
- Rationale: this preserves runtime compatibility while making the widened access impossible to miss
  in logs. It also matches existing auth/config handling here: the codebase hard-fails for
  production-breaking or auth-disabling misconfiguration (for example `OAuthConfigGuard` and
  `TestingBypassGuard`), but uses warnings for deprecated/ambiguous auth rule inputs that still have
  a deterministic fail-closed or operator-actionable interpretation.
