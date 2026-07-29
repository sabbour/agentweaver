---
"agentweaver": minor
---

Generalize GitHub authorization from bare-org-only checks to mixed allow rules in
`Auth:GitHub:AllowedOrg`, supporting `org`, `org/*`, and `org/team-slug` entries
with OR semantics across the configured list.

Also harden the legacy `Auth:GitHub:AllowedTeam` compatibility shim: when it overlaps
with a bare-org rule for the same org, keep the effective rules org-wide, emit a
prominent warning that the old AND-style restriction is not preserved, and show the
resolved allow-list so operators can migrate to explicit `org/team-slug` rules.
