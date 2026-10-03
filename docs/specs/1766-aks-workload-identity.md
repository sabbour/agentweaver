# Story: Compose Key Vault redemption with AKS workload identity

**Issue:** [#1766](https://github.com/sabbour/agentweaver/issues/1766).
**Status:** v1 P0 library slice, not deployed integration.

As a trusted Identity/control-plane host, I can explicitly supply the tenant ID,
client ID, and path of an AKS-projected service-account token file when constructing
the existing Azure Key Vault Secrets adapter. The adapter uses Azure Identity's
`WorkloadIdentityCredential` and Azure Key Vault SDK to retrieve the *requested*
secret version; it never selects a developer credential or a latest version.

The host authorizes the run, purpose, and reference before redemption and when
refreshing credentials. The projected assertion and returned secret exist only in
the credential/SDK pipeline and in the short-lived in-memory result, not in provider
descriptors, bindings, database records, snapshots, source files, or logs. Unsafe
content-logging options fail at composition. Missing configuration or an unavailable
projected file fails explicitly; authentication is lazy and cancellable. Azure
Identity owns its token caching and token-file refresh behavior; this adapter
does not promise immediate rotation or implement a second cache.

Account-free tests must drive both the real Azure Identity OAuth exchange and
the real Key Vault SDK request through fake in-memory HTTP transports. A deployed
AKS identity, Key Vault permissions, Identity authorization, OAuth broker, credential
delivery service, Kubernetes provisioning, and cloud acceptance are separate work.
