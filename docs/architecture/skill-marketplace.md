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
    participant G as Gateway
    participant P as Projects & Config
    participant DB as projects_config
    participant GH as Public GitHub
    participant S as Skills content service
    participant O as Immutable content store
    C->>G: Browse source ID + expected source revision + query/page
    G->>P: Forward bearer, tenant selector, and request
    P->>DB: Recheck current read role; read active source revision
    DB-->>P: Repository, requested ref, source revision
    P->>GH: Resolve requested ref to commit SHA
    GH-->>P: Full commit SHA
    P->>GH: Read recursive tree at that exact commit
    GH-->>P: Bounded tree metadata
    P->>P: Discover paths, sort, filter, then paginate
    P->>GH: Fetch SKILL.md only for current-page candidates
    GH-->>P: Page manifest bytes
    P->>DB: Recheck current caller authority and source revision
    P-->>G: Page, requested ref, source revision, resolved commit SHA
    G-->>C: Owner response unchanged
    C->>G: Preview/import source revision + resolved commit SHA + selected path
    G->>P: Forward bearer, tenant selector, and request
    P->>DB: Recheck current read/write role and read active source revision
    DB-->>P: Current source configuration
    P->>GH: Read selected manifest and bounded resources at the same commit
    GH-->>P: Exact selected bytes
    P->>DB: Recheck actor, project authority, and source revision
    alt Preview
        P->>S: Validate selected bytes and calculate content digest
        S-->>P: Preview metadata and digest
        P-->>G: Preview
        G-->>C: Owner response unchanged
    else Import
        P->>S: Import bytes with expected digest, idempotency key, and source provenance
        S->>O: Store immutable content revision
        O-->>S: Stored digest
        S-->>P: Import receipt
        P-->>G: Receipt
        G-->>C: Owner response unchanged
    end
    Note over P,GH: Browse fetches only current-page manifests. Preview/import reads stay pinned to the selected commit.
    Note over S,O: Skills owns content validation, immutable revisions, idempotency, and assignment.
```

| Route | Operation |
| --- | --- |
| `GET /api/projects/{projectId}/skill-marketplaces/sources` | List active sources. Pass `includeRemoved=true` to include retained removal tombstones. |
| `POST /api/projects/{projectId}/skill-marketplaces/sources` | Add a source at revision 1. |
| `PUT /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}` | Replace source settings using `expectedRevision`. |
| `DELETE /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}?expectedRevision={n}` | Tombstone a source using `expectedRevision`; returns the removed source at its incremented revision. |
| `GET /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/browse?expectedSourceRevision={n}&query={text}&page={n}&pageSize={n}` | Browse a pinned source revision. `query`, `page`, and `pageSize` are optional. |
| `POST /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview` | Preview selected bytes from `expectedSourceRevision`, `resolvedCommitSha`, and `selectedPath`. |
| `POST /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import` | Import selected bytes with the previewed `expectedContentDigest`, an `idempotencyKey`, and optional `skillId` plus `expectedRevision`. |
| `GET /api/projects/{projectId}/skills?cursor={cursor}&pageSize={n}` | Read the current project skill inventory and retained revision metadata. Both query values are optional. |

The skill inventory is a separate current-admin read. It requires `api.read`,
`projects.admin`, active membership in the current tenant, and current
TenantAdmin or project Owner authority. The owner rechecks authority after the
read and permits archived projects. Invalid cursors, page sizes outside 1–50,
repeated keys, and unknown query keys return `400`. Invalid authentication
returns `401`. A viewer or an administrator with lost or revoked access receives
`403`. Inactive membership rejected during initial caller resolution also
returns `403`. During the inventory recheck, the owner returns `404` for a
foreign project or membership found inactive or stale.

Gateway responses use `Cache-Control: no-store`.

The Gateway exposes versioned aliases for the source preview and import routes:
`POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview`
and `/api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import`.
It forwards the validated bearer and optional tenant selector, and preserves the
owner's status and response body. The owner remains responsible for current
project authority, source revision checks, and reading the selected files.

The default page size is 25. The cursor is opaque and uses unpadded Base64URL.
It binds the project and page size. The cursor sorts skill IDs in C collation,
ascending. It sorts revisions in descending order within each skill. This order
does not create a snapshot across pages. The page contains the current
configuration revision, assignments, retained revision metadata, page size, and
next cursor. It does not contain skill bytes, object keys, or a revocation
reason. It does not prove that a runtime loaded an assigned skill.

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

Browse resolves the configured ref to a full Git commit SHA and reads the
recursive tree at that immutable commit. It discovers `SKILL.md` paths beneath
the optional source subpath and sorts candidate locations ordinally. It filters
by candidate name or location before it applies pagination. Page size is limited to 50.
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

Browse and selected-content reads have a 15-second upstream timeout. Each
rechecks the caller's current project authority and exact source row after
upstream I/O. An authority change denies the operation. A source update or
removal returns `409` instead of returning stale results. Missing or removed sources
return `404`. Invalid requests return `400`. Malformed upstream metadata or
paths return `502`. Upstream unavailability returns `503`. A timeout returns
`504`. A truncated or oversized catalog returns `422`.

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
bytes to the sole Skills content service. Import must preserve the exact commit,
path, source revision, content identity, operation ID, and expected skill
revision. The Skills service owns accepted content receipts and runtime
assignment. The source-selected preview/import adapter is implemented in this
Projects owner candidate. It uses the existing content services and does not add
a second loader or catalog. A moving branch/tag or later source removal must
not replace already accepted bytes. Assignment still uses the existing Skills
assignment service.

Preview requires current project read authority. Import also requires
`projects.admin` and current project write authority. Both routes re-resolve the
caller after upstream I/O. They require the same actor and membership revision.
They also recheck the active source revision before they return or import
content.
Requests reject query parameters and responses use `Cache-Control: no-store`.
Errors use Problem Details with a stable `code`:

| Status | Error |
| --- | --- |
| `400` | Invalid request or skill content. |
| `403` | Authority failure. |
| `404` | Source not found. |
| `409` | Stale source or skill revision, digest mismatch, or conflicting idempotency key. |
| `422` | Source data exceeds the size limit. |
| `500` | Content-store integrity error. |
| `502` | Upstream data is malformed. |
| `503` | Upstream service or content storage is unavailable. |
| `504` | Request timed out. |

The checked-in v1 Gateway catalog maps source list/create/update/tombstone,
pinned browse, selected-content preview/import, inline Skills preview/import,
project assignment, and metadata-only inventory reads to these Projects owner
paths. Its OpenAPI contract includes the owner DTOs, exact required source
revision query values, optional inventory cursor/page-size parameters with their
bounds, and 3 MiB request-body limits for both inline and selected-source
preview/import. The first-party MCP catalog is built from that same OpenAPI
document. The Gateway remains a forwarder; these contracts do not prove a joined
or deployed owner implementation.

The retained Web project settings page uses the source management and pinned
browse routes, then offers explicit selected-source preview and import. The
source request contains no file bytes; the owner receipt must match the preview
digest before the imported skill can be assigned to agents in the current
project cast. Local file preview/import remains a separate path. The API has no
actor-safe runtime-loaded status producer; the page reports that gap instead of
inferring runtime use from an assignment or accepted run configuration. The
source and Gateway routes are present in v1; deployment and live availability
are not established by this client integration.

## Validation

Run the controlled source/browse tests and Projects & Config suite from the
repository root:

```powershell
dotnet test tests\Agentweaver.Projects.Config.Tests\Agentweaver.Projects.Config.Tests.csproj --configuration Release
```

The marketplace browse tests use controlled HTTP transports. Source persistence
tests use the suite's disposable PostgreSQL container; no live GitHub repository
or model call is required.
