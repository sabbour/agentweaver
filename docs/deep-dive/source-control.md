# Source Control

Use this page to understand how a run connects its repository identity to Git
and GitHub operations. Source Control does not decide whether a workflow has
approval to merge.

Three boundaries cooperate: the provider adapter performs repository
operations, the workspace manager owns isolated file state, and Orchestrator
owns current authorization, accepted provider pins, approval-linked intent,
and repository merge serialization.

## Find the implementation

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

## Keep repository identity and credentials explicit

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

## Prepare and recover a workspace

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

## Create pull requests and verify merge evidence

Before creating or reusing a pull request, provide the expected repository,
branches, and head and base SHAs. The adapter reuses a pull request only when
one exact open match exists; a superficially similar pull request is not
enough.

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
record is written only after optional migration 032 is applied; before that,
the write is skipped and the create or reuse result is unchanged. The project
pull request list reads these records and makes no provider call.

## Admit webhooks through the owner path

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

## Diagnose failures and recover safely

Failures distinguish invalid bindings, stale revisions, permission denial,
rate limiting, remote unavailability, and uncertain remote outcomes. An
unusable write response does not prove GitHub rejected an effect. Owners must
reconcile it before repeating work that could create another remote effect.

Workspace recovery uses the saved manifest. Provider recovery uses the
immutable run pin. Neither path replaces the target or credential with an
ambient default.

For supported provider setup, see the
[provider configuration guide](../guide/configure-providers.md).

## Related content

- [Source Control contracts and endpoints](../reference/contracts.md)
- [Projects configuration contract](../architecture/projects-config.md)
- [Provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
- [Environment and BuildTest lifecycle](./environment-sandbox.md)
- [Orchestration](./orchestration.md)
