---
"agentweaver": patch
---

Stop the worker heartbeat reaper from deleting live preview-backed `SandboxClaim`s (#578; supersedes the refuted TTL-renewal hypotheses in #560/#564/#570/#571/#574). Root cause (confirmed via kube-audit attribution to `system:serviceaccount:agentweaver:agentweaver-worker`): `AgentHostReaperService` runs from **worker** pods, but the worker deployment carried no `Sandbox__Preview__*` config, so its `SandboxPreviewService` had a null cluster client and `HasActivePreviewAsync()` permanently false-negated — every orphan sweep deleted the backing claim of a completed/`AssembleReady` child that still had a live preview, killing the preview URL.

Fix (both angles, complementary):

- **Config parity** — `k8s/base/worker-deployment.yaml` now mirrors the API deployment's `Sandbox__Preview__Enabled=true` + gateway env, so the worker's DI actually builds an in-cluster client and the reaper can read durable cluster preview state.
- **Fail-safe cluster reads** — `SandboxPreviewService.HasActivePreviewAsync`, `RenewBackingClaimTtlAsync`, and `SetBackingPodSafeToEvictAsync` now gate on the presence of a cluster client (`_client is null`) rather than the local `Enabled` provisioning flag, so a live route in cluster state stays authoritative for any process that can see it even if that process is not the one that provisions preview routes.
- **RBAC** — the `agentweaver-worker-sandbox` Role now grants read on `httproutes` and `patch` on `sandboxclaims`/`pods` so the worker reaper's preview probe (list HTTPRoutes) and its defer-branch TTL renewal (#560) and safe-to-evict pin (#574) succeed instead of silently 403-ing back into the delete path. The worker still never creates or deletes preview routes — that stays with the API.

Live verification against staging is pending (the shared staging environment was torn down by the subscription's routine 3-day GC and is being re-provisioned); validated locally via the .NET unit suite.
