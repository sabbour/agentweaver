# Specification: Key Vault redemption with AKS workload identity

**Status:** Library contract implemented in source. This document does not establish deployed integration or cloud acceptance.

For Identity and other trusted control-plane hosts that redeem a specific secret
version from Azure Key Vault.

## Problem and goals

The host needs a workload identity credential without selecting a developer's
credential or changing the requested secret version. Secret access must remain
bound to current run authority while asynchronous authentication completes.

## Non-goals

This library does not provision Kubernetes, grant Key Vault access, implement an
OAuth broker, or replace the host's durable grant authority.

## Intended behavior and requirements

A trusted Identity/control-plane host explicitly supplies the tenant ID,
client ID, and path of an AKS-projected service-account token file when constructing
the existing Azure Key Vault Secrets adapter. The adapter uses Azure Identity's
`WorkloadIdentityCredential` and Azure Key Vault SDK to retrieve the *requested*
secret version; it never selects a developer credential or a latest version.

## Contracts and boundaries

The host can compose `Agentweaver.Identity.AuthorizedSecretRedemption` with the
adapter to authorize the exact actor, project, run, purpose and secret version
before redemption and after asynchronous acquisition. An active, unexpired grant
is required on each refresh; credential expiry is bounded by the grant. The
projected assertion and returned secret exist only in
the credential/SDK pipeline and in the short-lived in-memory result, not in provider
descriptors, bindings, database records, snapshots, source files, or logs. Unsafe
content-logging options fail at composition.

## Failure and recovery

Missing configuration or an unavailable
projected file fails explicitly; authentication is lazy and cancellable. Azure
Identity owns its token caching and token-file refresh behavior; this adapter
does not promise immediate rotation or implement a second cache.

## Acceptance and validation

Account-free tests must drive both the real Azure Identity OAuth exchange and
the real Key Vault SDK request through fake in-memory HTTP transports. A deployed
AKS identity, Key Vault permissions, a durable Identity grant authority, OAuth broker, credential
delivery service, Kubernetes provisioning, and cloud acceptance are separate work.

## Rollout and compatibility

Compose the adapter only in a trusted host with explicit identity settings and
current grant checks. Keep deployment permissions and live acceptance separate
from library validation. See [run-bound secret authorization](secret-redemption-authorization.md)
for the authority and credential-lifetime contract.
