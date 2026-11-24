---
"Agentweaver.Identity.Broker": patch
"Agentweaver.Projects.Config": patch
---

Fence concurrent BYOK credential `PUT` and `DELETE` operations in `Identity.Broker` using a durable `DisablePending` reservation in `identity_broker.model_provider_credential_write_claims`, deterministic same-operation completion, and `Projects.Config` `CancelAsync` tombstone evidence. Add `--settle-credential-operations` to `Projects.Config` and enforce pre-migration `identity-broker` / `projects-config` quiescence, table-owner settlement locks, and `identity-broker` roll-forward guards in `deploy:kustomize` and `db:maintenance`.
