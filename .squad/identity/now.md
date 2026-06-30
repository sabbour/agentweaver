---
updated_at: 2026-06-11T15:46:22-07:00
focus_area: 003-projects implementation plan COMPLETE and committed (b9061aa); next = generate Phase 0 tasks and begin implementation
active_issues: []
---

# What We're Focused On

Branch `003-projects`. Implementation plan complete:

**Status:** 
- Spec 003-projects clarified and locked (commit pending from prior session)
- Implementation plan authored, reviewed (4 rounds), and committed (b9061aa on branch 003-projects)
- Dual-reviewer gatekeeping established; decision precedent locked

**Plan Summary (b9061aa):**
- 12 sections + Review Resolutions (section 13)
- Architecture decisions locked: project identity (SQLite), Copilot OAuth (no stored key), per-run model threading, race-safe delete (atomic reservation + CAS + compensation), token tenancy (by GitHubTokenScope), sign-out fail-closed, IProjectWorkspaceProvider seam, run.cancelled for delete
- Dual-reviewer feedback cycle: Tank → (locked) Morpheus → (locked) Smith → (locked) Link (owner-accepted compensation)
- 8 architecture decisions from Morpheus round 2, 1 TOCTOU race fix from Smith round 3, 1 compensation fix from Link round 4

**Next (Phase 0 — Domain + Persistence):**
1. Generate implementation tasks from plan.md sections
2. Code domain model: ProjectId, ProjectMetadata, Project aggregate
3. Implement SqliteProjectStore: CRUD, availability checks, state machine (Active → Deleting)
4. Implement IProjectWorkspaceProvider abstraction and LocalDirectoryWorkspaceProvider
5. Data migration strategy for project records

**Planning-time Security Items (8 queued from FR-005):**
1. OAuth scope minimization (repo + read:user scopes)
2. Secure token storage (OS keychain locally; encrypted cloud)
3. Token lifecycle (refresh, expiry, revocation, clean sign-out)
4. GitHub auth ≠ Copilot entitlement (distinct errors)
5. Hosted-cloud multi-tenant reconciliation (per-caller/tenant)
6. Centralized token redaction (Principle XI)
7. Clone transport hygiene (credential helper/askpass)
8. Foundry credential separation

**Ready for Implementation:** All architectural decisions locked. Phase 1 (project lifecycle), Phase 2 (run lifecycle + hosted-cloud), and Phase 7 (comprehensive tests) awaiting Phase 0 domain layer. Security spike (FR-005 dual-grant validation) planned early in Phase 2.
