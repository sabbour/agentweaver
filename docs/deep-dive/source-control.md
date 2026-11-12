# Source Control

## 1–4. Goals, constraints, context and strategy

Use this page to understand how a run connects its repository identity to Git
and GitHub operations. Source Control does not decide whether a workflow has
approval to merge.

Three boundaries cooperate: the provider adapter performs repository
operations, the workspace manager owns isolated file state, and Orchestrator
owns current authorization, accepted provider pins, approval-linked intent,
and repository merge serialization.

Explicit pins and expected heads prevent a later repository state from
silently replacing accepted work. The trade-off is conflict handling:
callers must refresh a changed preview or reconcile an uncertain write instead
of overwriting it.

<span id="find-the-implementation"></span>

## 5. Building blocks

| Source | Responsibility |
| --- | --- |
| `GitHubSourceControlAdapter.cs` | GitHub negotiation, issues, pull requests, reviews, and merge evidence |
| `GitWorkspaceManager.cs` and `GitWorkspaceContracts.cs` | Isolated checkout, recovery, diff, capture, workspace identities, and failures |
| `GitWorkspaceCapturePackage.cs` | Retained capture representation |
| `GitWorkspaceBuildTestSeed.cs` and `GitWorkspaceBuildTestOutput.cs` | Repository preparation and collected output |
| `GitHubAppInstallationTokenIssuer.cs` | Repository-scoped App tokens |
| `GitHubWebhookSignatureVerifier.cs` | Raw-body signature and repository checks |
| `SourceControlContracts.cs` | Provider-neutral operations and binding contracts |

The implementation is in `packages/Agentweaver.SourceControl/` and
`packages/Agentweaver.Abstractions/`.

## 6. Runtime view

### Keep repository identity and credentials explicit

The GitHub adapter negotiates the numeric repository ID, creation-based
resource generation, and permission-derived capabilities. The owner pins that
resource to the run. An operation context carries the repository, provider
binding, and Broker-redeemed credential.

`VerifyCurrentBindingAsync` rereads repository identity and permissions. A
renamed or replaced resource cannot silently become another run target. The
adapter accepts only the canonical HTTPS `api.github.com` origin.

Checkout uses an explicit credential and pinned remote. Git runs without
inherited `GIT_*` settings, with an empty global configuration, bounded output,
and redacted diagnostics. The GitHub authorization header enters the child
environment rather than the clone URL.

`GitHubAppInstallationTokenIssuer` requests one numeric repository with
`contents:write` and `pull_requests:write`; `issues:write` is optional. It
validates the returned permission ceiling and expiry, then invalidates the
supplied private-key credential after the attempt.

### Read and commit team files explicitly

Project team synchronization is an explicit user action on the Agents page.
The Gateway exposes `GET` and `POST /api/v1/projects/{projectId}/team/sync`;
both require a current project Owner and a short-lived GitHub App repository
selection code in `X-Agentweaver-Team-Sync-Selection`. The GET operation
compares the configured repository with the current Projects cast and returns
a no-store preview containing the configuration revision, a change-set hash,
and added, modified, or removed `.squad/` paths. The user reviews that preview
before the POST operation can commit it.
The hash binds the selected installation ID and provider repository ID as well
as the configured name, branch head, configuration revision, file paths, and
file content. A renamed or replaced repository cannot reuse an earlier preview.

Projects & Config remains the only roster owner. Orchestrator renders
`.squad/team.md` and the active cast members' charter files from its current
configuration; it does not load repository files as a second team registry.
Only canonical active charter paths and the generated team file are changed.
Alumni and unrelated `.squad/` content remain untouched. No files are mirrored
automatically.

The commit request carries both the preview hash and configuration revision.
Orchestrator recomputes the preview, rejects a changed repository head, file
set, or project revision before writing, and rechecks project/repository
authority and the revision immediately before the branch update. A conflict
requires a fresh preview; it is not retried as a write.

The read operation requests a repository-scoped `contents:read` token. A
commit requests `contents:write` only. Identity verifies the current project
owner and repository selection before minting the token. Orchestrator checks
current authority after minting, after a read and before returning its
snapshot, and immediately before a commit updates the branch. The token is
returned only over the internal service channel and is invalidated after the
provider operation. The internal path uses the existing certificate-pinned
Issue Sync channels; it does not expose a public token-minting route.

Reads reject incomplete trees, symlinks, submodules, invalid paths, and files
or trees over the configured limits. Commits accept only regular paths beneath
`.squad/`. Before creating Git objects, the adapter checks both the expected
branch head and every expected blob ID from the preview. It also checks that
the complete resulting tree stays within 250 files and 4 MiB of content,
including replacements and deletions. A mismatch returns a conflict without
sending a write request. The final branch update is non-forced, so a concurrent
change cannot be overwritten.

Repository synchronization is separate from the digest-checked AgentSuite
export and import. Repository sync reads and writes the selected `.squad/`
files in a configured GitHub repository. AgentSuite export and import transfer
a reviewed portable archive and verify its digest; import stages a casting
proposal for confirmation. They do not read or write a repository.

### Browse the configured repository

The Workspace browser reads committed files from the repository already saved
in project configuration. Its Gateway routes are
`GET /api/v1/projects/{projectId}/source-control/refs`,
`GET /api/v1/projects/{projectId}/source-control/tree?ref={commitSha}`, and
`GET /api/v1/projects/{projectId}/source-control/files/{path}?ref={commitSha}`.
The browser uses the existing Identity Repo App selection flow; Orchestrator
matches its short-lived selection code to the configured repository, requires
current project read authority, and obtains an exact-repository
`contents:read` token from Identity. It rechecks current authority after each
provider read and invalidates the temporary credential. Secret-auth mode does
not enable this browser. The routes do not accept a repository host, owner, or
name override.

Refs return at most 100 branches and tags per page, each resolved to a commit
SHA. The tree and file routes require that full SHA, so switching branches
cannot mix a moving tree with a later file. Recursive trees are limited to
25,000 entries and 8 MiB; a provider-truncated or oversized tree fails rather
than appearing complete. The Gateway allows up to 56 MiB for the serialized
tree response to accommodate bounded JSON escaping. File reads must match a
regular blob in that commit's tree and are limited to 1 MiB. Relative paths
reject traversal and encoded separators. Binary files are identified without
returning text content; symbolic links, submodules, and unsupported tree
entries are not viewable. Responses are no-store, and unavailable or revoked
authority is not replaced with cached or local files.

This configured-repository view is not Run Changes' produced-output capture,
and does not expose uncommitted local worktree changes or repository import.
Neither it nor the existing produced-run browser writes to the repository.

### Prepare and recover a workspace

Before preparing a workspace, you need an accepted repository binding, the
exact base commit, and an explicit credential for checkout.

`GitWorkspaceManager.PrepareAsync` derives a workspace directory from run and
workspace identity. It writes a versioned ownership manifest in a preparing
state before cloning.

1. Validate the repository and remote.
2. Fetch the exact base SHA and check out the requested branch.
3. Mark the ownership manifest ready.
4. During recovery, verify the saved run, repository, base, and workspace
   incarnation.

Recovery does not adopt an arbitrary directory because its path looks correct.
Path checks reject symbolic links, junctions, and other reparse-point paths at
ownership boundaries.
Capture includes dirty, deleted, and untracked content without rewriting the
Git index. The result binds files and diff to the workspace incarnation and
accepted source.

BuildTest seed creation exports a credential-free repository bundle and file
inventory. Preparation verifies the saved binding before installing that
seed. A ready prepared workspace is not reseeded. A separate output path seals
the complete baseline and declared output bytes for owner admission.

### Create pull requests and verify merge evidence

Before creating or reusing a pull request, provide the expected repository,
branches, and head and base SHAs. The adapter reuses a pull request only when
one exact open match exists; a superficially similar pull request is not
enough.

For a workflow-opened pull request, Orchestrator saves the operation before it
calls the provider. The operation ID is the stable effect identity. The owner
stores a request fingerprint that excludes the decision version and execution
fence, plus a separate fingerprint for the original authorization context.
The source-control PR path has no executable-action grant ID, so the owner does
not create or infer one.

On an exact retry, the owner checks the saved operation. If it has a pull
request number, the owner reads that same pull request and checks its immutable
result identity. If the owner has no saved number, it calls the adapter with
creation disabled. The adapter can reconcile only the exact operation marker.
It does not send another create request. If the exact result is missing,
ambiguous, or changed, the owner stops. If a create may have completed but no
result is saved, the owner records an indeterminate outcome. A saved result is
immutable and is not replaced. This process prevents a retry from authorizing
a second pull request; it does not claim that a remote write is exactly once.
This path requires the original decision version and execution fence. It does
not resume an operation under a new fence; checkpoint recovery is a separate
capability.

Merge-readiness checks read effective branch rules and classic protection.
They resolve required checks against check-run and commit-status evidence for
the exact head. Unknown rules, unsupported merge requirements, and ambiguous
evidence do not become successful checks.

`MergePullRequestAsync` submits the expected head SHA and selected merge
method. GitHub's confirmation supplies the merge commit. Workflow approval and
repository serialization remain owner responsibilities; green checks alone
do not create approval.

When a run creates or reuses a pull request, the Orchestrator records the
number, URL, provider title, state, head and base, run, and disposition. The
reviewed PostgreSQL grant plan applies migration 032, which creates the records
table and grants the runtime its exact read and write permissions. Before the
maintenance run, the owner skips the write and the project list reports that
records are unavailable. After the migration, the list reads stored records
and makes no provider call.

### Admit webhooks through the owner path

`GitHubWebhookSignatureVerifier.VerifyRawBody` verifies the `sha256=` keyed
hash message authentication code (HMAC) over the original request bytes with
a fixed-time comparison. It invalidates
the one-use webhook credential before returning.

Payload parsing then checks repository name and numeric ID against the pin,
validates delivery ID, and extracts typed event fields. A valid signature
alone is not run authority. The current Orchestrator integration requires an
authenticated, run-bound relay before owner admission. The package does not
provide a standalone webhook service or authorize direct unauthenticated
GitHub delivery.

### Diagnose failures and recover safely

Failures distinguish invalid bindings, stale revisions, permission denial,
rate limiting, remote unavailability, and uncertain remote outcomes. An
unusable write response does not prove GitHub rejected an effect. Owners must
reconcile it before repeating work that could create another remote effect.

Workspace recovery uses the saved manifest. Provider recovery uses the
immutable run pin. Neither path replaces the target or credential with an
ambient default.

For supported provider setup, see the
[provider configuration guide](../guide/configure-providers.md).

## 7–9. Deployment, shared concepts and decisions

There is no standalone Source Control service: Orchestrator hosts owner routes,
and `Agentweaver.SourceControl` supplies adapters/workspace code. Environment
owns the workspace volume; isolated Git workspaces live on that storage.
GitHub is an external trust boundary. See [deployment](./infra-deployment.md).

Credentials come from [Identity](./identity-secrets.md), approval and merge
serialization from [Orchestrator](./orchestration.md), and retained output
from [Events](./events-sessions.md). Provider capabilities do not authorize a
merge. The [canonical ADR view](./00-system-overview.md#9-architecture-decisions)
owns decision status; implementation here does not accept a broader proposal.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| Team preview's branch head or blob IDs change before commit | Conflict before write; final branch update is non-forced | Controlled GitHub/team-sync tests |
| Workspace directory has a foreign manifest or reparse path | Reject recovery rather than adopt the directory | Real isolated Git/workspace tests |
| Merge response is ambiguous or head changes | Do not fabricate success or repeat an uncertain merge | Provider/owner controlled tests; not live branch protection |

A remote write can succeed before transport failure. The repository owner
must reconcile saved intent and provider state before another effect.
`contents:write` is a capability ceiling, not workflow approval.

**Terms:** repository pin identifies the resource; workspace incarnation
identifies owned files; preview hash binds a proposed change; merge receipt
records a provider-verified result.

## Related content

- [Source Control contracts and endpoints](../reference/contracts.md)
- [Projects configuration contract](../architecture/projects-config.md)
- [Provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
- [Environment and BuildTest lifecycle](./environment-sandbox.md)
- [Orchestration](./orchestration.md)
