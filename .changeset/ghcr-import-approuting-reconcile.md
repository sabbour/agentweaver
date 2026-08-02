---
"agentweaver": minor
---

`npm run azure:provision-infra` can now reuse the four container images already published to GHCR instead of always rebuilding them into ACR. Operators opt in with `--image-source ghcr --ghcr-ref <ref>`, where `<ref>` must be an immutable published release tag (`vX.Y.Z`) or `sha-<hex>` tag; the importer preflights all four images together, captures the destination ACR digests for provenance verification, redacts optional GHCR credentials, and refuses conflicting tag overwrites unless `--force` is passed.

Provisioning an existing AKS cluster now also reconciles legacy App Routing state only when needed. If the cluster predates the Gateway API / `nginx=None` policy, `10-create-cluster` detects the mismatch, enables the Istio-backed Gateway API path, and disables the managed nginx controller/default-domain drift with targeted idempotent updates; already-correct clusters remain untouched.
