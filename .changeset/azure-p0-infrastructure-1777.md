---
---

[Issue #1777](https://github.com/sabbour/agentweaver/issues/1777) added the
dedicated v1 P0 Azure infrastructure definitions (`infra/bicep/`), the
Kustomize base layout (`deploy/k8s/base/`), and dependency-free Node CLI
tooling for read-only plan, exact-SHA confirmed deploy, and a blocking
acceptance entrypoint (`scripts/azure/`). This is infrastructure-as-code and
validation tooling, not a product library: no `foundation.json` component
changed version, and no Azure resource has been provisioned. See
[docs/specs/1777-azure-p0-infrastructure.md](../docs/specs/1777-azure-p0-infrastructure.md)
for scope and boundaries.

The tools bind the actual account, dedicated resource ownership, reviewed source,
and Bicep input receipt. Configuration cannot satisfy the blocked runtime gate.
Monitor uses all five private DNS zones, with both associations before its endpoint.
The native PostgreSQL bootstrap remains a separately approved prerequisite.
Blob diagnostics preserve primary and conditional cleanup errors.
The existing Node CI collector runs credential-free infrastructure regressions.
