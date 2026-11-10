---
---

Let a registered separate target use Identity Broker provider setup (`render` and `activate`), P1 PostgreSQL `--inspect` and `--apply-pending-migrations`, and the full `db:maintenance` migration run. Provider setup and the Kustomize provider preflight now start from `deploy/k8s/targets/<target>.json`. Only P1 initialization reads the Projects Config migration principal. The default `agentweaver` target behaves as before.
