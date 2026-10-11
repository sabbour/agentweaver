# Skill marketplace sources

Projects & Config owns each project's configured skill sources and the bounded
catalog browse API. A source stores a GitHub repository, requested ref, optional
repository subpath, display name, lifecycle state, and optimistic revision. A
source revision identifies the configuration the caller selected; it is not a
content revision and does not grant permission to read or run a skill.

Source URLs must identify a public GitHub repository over HTTPS. Marketplace
reads are anonymous: this API does not use a caller's GitHub token and does not
provide private-repository access. Adding a source records its configuration
without claiming the upstream is reachable. `active` means configured and not
removed; current upstream availability is reported by browse errors rather than
cached as a success-shaped source state.

## Routes

All operations are project-scoped, require `api.read`, and recheck current
Projects authority. Reading or browsing requires a current project Owner,
Contributor, or Viewer role (or tenant administrator). Creating, updating, and
removing a source also require `projects.admin` and current project Owner or tenant
administrator authority. Responses use
`Cache-Control: no-store`.

```mermaid
sequenceDiagram
    autonumber
    participant C as Authorized project caller
    participant P as Projects & Config
    participant DB as projects_config
    participant GH as Public GitHub
    C->>P: Browse source ID + expected source revision + query/page
    P->>DB: Recheck current read role; read active source revision
    DB-->>P: Repository, requested ref, source revision
    P->>GH: Resolve requested ref to commit SHA
    GH-->>P: Full commit SHA
    P->>GH: Read recursive tree at that exact commit
    GH-->>P: Bounded tree metadata
    P->>P: Discover paths, sort, filter, then paginate
    P->>GH: Fetch SKILL.md only for current-page candidates
    GH-->>P: Page manifest bytes
    P->>DB: Recheck source revision
    P->>P: Recheck current caller authority
    P-->>C: Page, requested ref, source revision, resolved commit SHA
    Note over P,GH: Browse does not fetch candidate resource files or assign/run a skill.
```

| Route | Operation |
| --- | --- |
| `GET /api/projects/{projectId}/skill-marketplaces/sources` | List active sources. Pass `includeRemoved=true` to include retained removal tombstones. |
| `POST /api/projects/{projectId}/skill-marketplaces/sources` | Add a source at revision 1. |
| `PUT /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}` | Replace source settings using `expectedRevision`. |
| `DELETE /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}?expectedRevision={n}` | Tombstone a source using `expectedRevision`; returns the removed source at its incremented revision. |
| `GET /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/browse?expectedSourceRevision={n}&query={text}&page={n}&pageSize={n}` | Browse a pinned source revision. `query`, `page`, and `pageSize` are optional. |

Create accepts `{ "name": "Team skills", "repository": "owner/repository",
"requestedRef": "main", "subpath": "skills" }`; `name`, `requestedRef`, and
`subpath` are optional. The default name is the repository name and the default
ref is `main`. Update accepts the same fields and requires `expectedRevision`.
Repository inputs normalize to lowercase `owner/repository`; refs and relative
subpaths are validated before persistence.

Source responses contain `sourceId`, `name`, `repository`, `requestedRef`,
`subpath`, `revision`, and `state` (`active` or `removed`). Names are unique
without case among active sources in a project. Updates and removals use
compare-and-swap revision checks; stale writes return `409`. Removal increments
the revision and retains a tombstone instead of deleting the source row.

## Bounded browse

Browse resolves the configured ref to a full Git commit SHA, reads the recursive
tree at that immutable commit, discovers `SKILL.md` paths beneath the optional
source subpath, sorts candidate locations ordinally, filters by candidate name
or location, and only then applies pagination. Page size is limited to 50.
Search text is limited to 128 characters. The tree response is limited to 8 MiB
and 100,000 entries; the index is limited to 5,000 skill manifests. An
over-limit or GitHub-truncated tree fails explicitly instead of returning an
incomplete catalog.

Only the current page's `SKILL.md` manifests are fetched to populate their
descriptions. Other candidate manifests and skill resource files are not
downloaded during browse. Each response includes `sourceId`, `sourceRevision`,
`requestedRef`, `resolvedCommitSha`, paged `candidates` (`location`, `name`,
`description`), `total`, `page`, `pageSize`, and `hasMore`. Candidate locations
are canonical repository-relative directory paths; an empty location represents
a skill at the repository root.

Browse and selected-content reads are bounded by a 15-second upstream timeout.
Each rechecks the caller's current project authority and exact source row after
upstream I/O; an authority change denies the operation and a source
update/removal returns `409` instead of returning results for a stale source
revision. Missing/removed sources return `404`; invalid requests return `400`; malformed upstream
metadata/paths return `502`; upstream unavailability returns `503`; timeout
returns `504`; a truncated or oversized catalog returns `422`.

## Import boundary

Browse does not compute or cache a skill-content digest and does not acquire skill
resources. The source-side reader consumes the selected location and resolved
full commit from browse and never resolves the moving requested ref again. It
fetches the selected root `SKILL.md` and its bounded resources at that exact
commit. The manifest is limited to 512 KiB; the resource inventory is limited
to 64 files, 256 KiB per file, and 1 MiB total. Nested skill directories are
separate browse candidates and are excluded from their parent's resources.
Symlinks, unsafe or case-aliased resource paths, and tree/content size
mismatches fail explicitly. The sole Skills
validator remains responsible for the 256 KiB instruction-body limit and
deterministic content digest.

The source-backed preview/import adapter passes only server-fetched candidate
bytes to the sole Skills content service. Import must preserve that exact commit,
path, source revision, content identity, operation ID, and expected skill
revision; accepted content receipts and runtime assignment remain owned by the
Skills service. A moving branch/tag or later source removal must not replace
already accepted bytes. The Projects source reader is implemented, while its
selected-source preview/import contract is admitted. The client preview returns
metadata and a content digest, not file bytes. Import passes the source revision,
resolved commit, selected path, digest, and idempotency key to the owner, which
reads the pinned content. Route publication and deployment remain separate and
are not implied by this client integration.

The checked-in v1 Gateway catalog maps source list/create/update/tombstone,
pinned browse, local-file Skills preview/import, and project assignment to these
Projects owner paths. The separate selected-source preview/import contract is
admitted, but its publication and deployment remain pending. The current
OpenAPI contract includes the owner DTOs, exact required browse revision query,
and 3 MiB Skills request limits. The first-party MCP catalog is built from that
current OpenAPI document.

The retained Web project settings page uses the source management and pinned
browse routes, then offers explicit selected-source preview and import. The
source request contains no file bytes; the owner receipt must match the preview
digest before the imported skill can be assigned to agents in the current
project cast. Local file preview/import remains a separate path. The API has no
actor-safe runtime-loaded status producer; the page reports that gap instead of
inferring runtime use from an assignment or accepted run configuration.
Publication or deployment of the selected-source routes is not established by
this client integration.

## Validation

Run the controlled source/browse tests and Projects & Config suite from the
repository root:

```powershell
dotnet test tests\Agentweaver.Projects.Config.Tests\Agentweaver.Projects.Config.Tests.csproj --configuration Release
```

The marketplace browse tests use controlled HTTP transports. Source persistence
tests use the suite's disposable PostgreSQL container; no live GitHub repository
or model call is required.
