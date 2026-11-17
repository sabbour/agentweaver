# Specification: project-scoped ACS Policy reads

**Status:** Proposed for v1.1.0. This slice adds read routes only.

## Source baseline

- On `origin/v1` at `acec026530c1b8ee98ea2faa338130b2383bd1e4`, `NativeAcsPolicyStore.ReadAsync` reads any scope and verifies the complete artifact hash (`NativeAcsPolicyStore.cs:391–421`). History and exact-revision reads are Platform-only (`:553–668`).
- The database has one head per `(scope_type, scope_id)` (`ProjectsConfigDbContext.cs:740–753`). `ProjectsConfigService.ListProjectsAsync` filters by current TenantAdmin or project read roles (`ProjectsConfigService.cs:445–481`); `FindProjectAsync` checks current membership, tenant, binding, and read role (`:1118–1174`).
- Gateway publishes three Platform source reads and rejects undeclared history query keys (`GatewayRouteCatalog.cs:480–493`, `GatewayOwnerClient.cs:2183–2193`).
- v0.x YAML reads defaulted a missing file and rejected malformed data (`v0.34.2:apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs:36–57`). Its preservation tests show why reads must retain complete source bytes (`v0.34.2:tests/Agentweaver.Tests/Sandbox/SandboxPolicyPreserveTests.cs:56–104`); the YAML format is not reused.

## Proposed contract

- Add `GET /api/policies/projects` to list existing Project heads visible to the caller. Return bounded metadata only, ordered by scope ID, with `pageSize` (default 20, range 1–100) and an opaque keyset cursor.
- Add `GET /api/policies/projects/{projectId}` for the current complete artifact, `GET .../{projectId}/revisions` for newest-first history, and `GET .../{projectId}/revisions/{revision}` for exact retained bytes.
- Reuse the existing head, revision, file, and hash records. Preserve each head's stable Policy ID. An authorized project with no head returns an empty history page with null lineage fields. A missing current head or future exact revision returns 404.
- List only scopes with stored heads. Include no Platform head or inherited view. Do not read OPA or change source, revision, audit, receipt, or outbox state.
- Require `api.read`, an active membership, and current project read authority: TenantAdmin in the selected tenant, or Owner, Contributor, or Viewer on that project. PlatformAdmin alone grants no project access; reads do not require `projects.admin`. The list requires an unbound token. A project-specific read accepts an unbound token or a token bound to that exact project; it rejects a different project, a run binding, or a purpose binding. Recheck authority after each store read.
- Bind history cursors to scope type, scope ID, Policy ID, snapshot revision, and page size. Protect list cursors and bind them to the selected tenant, caller identity, current project-read grants, page size, and last returned scope ID. Reject a cursor with `400 native_acs_policy_scope_list_request_invalid` when membership or project-read grants change; the caller can restart the list. Each list page uses one bounded, set-based query to check current membership and roles while reading current heads. Cursors do not grant access or pin a cross-page snapshot, and they do not expose Project IDs as clear text.
- Return exact stored bytes only after the existing hash check. Keep malformed selectors as 400, revoked or unauthorized scopes as the existing safe denial, corrupt committed source as 500, and database availability failures as 503. Propagate cancellation.
- Publish finite Gateway routes with query allow-lists, tenant selection, `Cache-Control: no-store`, the 5 MiB response cap, and MCP exclusion.

## Tests, docs, and release

- Test the read-role matrix, tenant and project isolation, unbound checks, list/history pagination, exact bytes and stable IDs, missing/corrupt source, authority revocation during reads, read-only storage, Gateway forwarding, and OpenAPI contracts.
- Update the Projects and Gateway API references, architecture and testing pages, and the documentation source map. Keep service diagrams unchanged.
- Add minor changesets for `Agentweaver.Projects.Config` and `Agentweaver.Gateway`. No schema, write, preview, inherited-view, or rollback change is included.
