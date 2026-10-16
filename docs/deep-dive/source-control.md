# Source Control

## The mental model

Source Control connects a run's repository identity to Git and GitHub operations. It does not decide whether a workflow has approval to merge.

Three boundaries cooperate: the provider adapter performs repository operations, the workspace manager owns isolated file state, and the Orchestrator owns current authorization, accepted provider pins, approval-linked intent, and repository merge serialization.

## Where this lives

- `packages/Agentweaver.SourceControl/GitHubSourceControlAdapter.cs` — GitHub negotiation, issues, pull requests, reviews, and merge evidence.
- `packages/Agentweaver.SourceControl/GitWorkspaceManager.cs` — isolated checkout, recovery, diff, and capture.
- `packages/Agentweaver.SourceControl/GitWorkspaceContracts.cs` — workspace identities and failures.
- `packages/Agentweaver.SourceControl/GitWorkspaceCapturePackage.cs` — retained capture representation.
- `packages/Agentweaver.SourceControl/GitWorkspaceBuildTestSeed.cs` and `GitWorkspaceBuildTestOutput.cs` — repository preparation and collected output.
- `packages/Agentweaver.SourceControl/GitHubAppInstallationTokenIssuer.cs` — repository-scoped App tokens.
- `packages/Agentweaver.SourceControl/GitHubWebhookSignatureVerifier.cs` — raw-body signature and repository checks.
- `packages/Agentweaver.Abstractions/SourceControlContracts.cs` — provider-neutral operations and binding contracts.

## Core concepts and invariants

### Repository names do not replace resource identity

The GitHub adapter negotiates the numeric repository ID, creation-based resource generation, and permission-derived capabilities. The owner pins that resource to the run. An operation context carries the repository, provider binding, and broker-redeemed credential.

`VerifyCurrentBindingAsync` rereads repository identity and permissions. A renamed or replaced resource cannot silently become a different run target. The adapter accepts only the canonical HTTPS `api.github.com` origin.

### Credentials remain explicit

Checkout uses an explicit credential and the pinned remote. Git runs without inherited `GIT_*` settings, with an empty global configuration, bounded output, and redacted diagnostics. The GitHub authorization header enters the child environment rather than the clone URL.

`GitHubAppInstallationTokenIssuer` requests one numeric repository with `contents:write` and `pull_requests:write`. `issues:write` is optional. It validates the returned permission ceiling and expiry, then invalidates the supplied private-key credential after the attempt.

## Workspace lifecycle

`GitWorkspaceManager.PrepareAsync` derives a workspace directory from the run and workspace identity. It writes a versioned ownership manifest in a preparing state before cloning.

The manager validates the repository and remote, fetches the exact base SHA, checks out the requested branch, and marks the manifest ready. Recovery verifies the saved run, repository, base, and incarnation. It does not adopt an arbitrary directory because its path looks correct.

Path checks reject linked or reparse-point paths at ownership boundaries. Capture includes dirty, deleted, and untracked content without rewriting the Git index. The result binds the files and diff to the workspace incarnation and accepted source.

BuildTest seed creation exports a credential-free repository bundle and file inventory. Preparation verifies the saved binding before installing that seed. A ready prepared workspace is not reseeded. The separate output path seals the complete baseline and declared output bytes for owner admission.

## Pull requests and merge evidence

The adapter creates or reuses a pull request only for the expected repository, branches, and head/base SHAs. Reuse requires one exact open match; a superficially similar pull request is not enough.

Merge readiness reads effective branch rules and classic protection. It resolves required checks against check-run and commit-status evidence for the exact head. Unknown rules, unsupported merge requirements, or ambiguous evidence do not become successful checks.

`MergePullRequestAsync` submits the expected head SHA and selected merge method. GitHub's confirmation supplies the merge commit. Workflow approval and repository serialization remain owner responsibilities; the adapter does not create approval by observing green checks.

## Webhook admission

`GitHubWebhookSignatureVerifier.VerifyRawBody` verifies the `sha256=` HMAC over the original request bytes with a fixed-time comparison. It invalidates the one-use webhook credential before returning.

Payload parsing then checks the repository name and numeric ID against the pin, validates the delivery ID, and extracts typed event fields. Signature verification alone is not run authority.

The current Orchestrator integration requires an authenticated, run-bound relay before owner admission. The package does not expose a standalone webhook service or authorize direct unauthenticated GitHub delivery.

## Failure and recovery

Failures distinguish invalid bindings, stale revisions, permission denial, rate limiting, remote unavailability, and uncertain remote outcomes. An unusable write response is not proof that GitHub rejected the effect. Owners must reconcile it before repeating work that could create another remote effect.

Workspace recovery uses the saved manifest. Provider recovery uses the immutable run pin. Neither path replaces the target or credential with an ambient default.

## Related pages

- [Source Control contracts and endpoints](../reference/contracts.md)
- [Projects configuration contract](../architecture/projects-config.md)
- [Provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
- [Environment and BuildTest lifecycle](./environment-sandbox.md)
- [Orchestration](./orchestration.md)
