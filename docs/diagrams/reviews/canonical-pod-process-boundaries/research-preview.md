## Summary

The requested five diagram identities remain appropriate, but their content must distinguish **identity authorization from secret delivery**, **platform-owned preview orchestration from model-free command discovery**, and **Kubernetes object creation from validated HTTPS publication**. The infrastructure overview should merge into the shared/guide-owned canonical only after its owner fixes worker replicas and obsolete secret arrows. Current source also exposes several qualifications not fully captured by the audit: the HPA uses CPU **and memory**, preview approval expiry preserves a private process for retry, API-side preview credentials are persisted best-effort, and the checked-in network rules are broader—and have more exceptions—than the prose claims. Sources and tests were read; no tests, cluster probes, edits, or agent launches were performed.

**Absolute checkout examined:** `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`. All `sabbour/agentweaver:` citations below refer exclusively to files in that checkout.

## 1. Audit dispositions and ownership

| Scoped concept | Disposition and bounded action |
|---|---|
| `purpose-bound-credentials` | **Retain table/prose.** Correct credential-specific persistence/lifetime claims. |
| `credential-configure-reference` | **Reuse** corrected `sandbox-pod-execution-fig6`; do not introduce a token-flow duplicate. |
| `aks-infrastructure-map` | **Merge** `infra-deployment-fig1` into corrected `canonical-aks-components`; owner handoff required. |
| `public-route-selection` | **Retain** `infra-deployment-fig4`, correcting exact route coverage. |
| `workload-secret-path` | **Redesign** `infra-deployment-fig2`; API/worker consume CSI secrets, not MCP. |
| `build-deploy-convergence` | **Retain** `infra-deployment-fig3`; implemented build/retag convergence, four images, worker/HPA deployment. |
| `production-storage` | **Retain prose**, removing SQLite/RWO production advice. |
| `production-network-policy` | **Retain prose**, replacing absolute/no-ingress/FQDN-only claims with checked-in rules. |
| `deterministic-preview` | **Redesign** `live-preview-provisioning-fig1`; explicit skip/failure/approval/publication branches. |
| `preview-lifetime` | **Retain prose**, not another lifecycle graphic. |
| `preview-control-credentials` | **Retain prose**, separating durable server-side storage from pod memory. |
| `preview-gateway-path` | **Redesign** `sandbox-browser-preview-fig1`, but it is now **shared-owned**, outside this agent’s drawing scope. |
| `preview-agent-approval` | **Redesign** `sandbox-browser-preview-fig2`; denial/expiry must not reach publication. |
| `preview-replica-lifecycle` | **Retain prose**, including both session identifiers, cluster-state resolution, cleanup and local fallback. |
| `pod-credential-and-preview-references` | **Reuse** credential table and preview canonical; correct the direct-vault rebuild instruction. |

Audit evidence: `sabbour/agentweaver:.github/skills/docs-diagram-audit/reports/deep-dive-execution.json:156-177`, `:259-324`, `:330-400`, `:516-525`. Current shared assignment includes **both** `canonical-aks-components` and `sandbox-browser-preview-fig1`: `sabbour/agentweaver:.github/skills/docs-diagram-audit/reports/plan-shared.json:4-33`.

### Shared-owner blockers

1. **`canonical-aks-components` is not safe to reuse unchanged.**
   - Existing `Worker ×1 + HPA` must become baseline **Worker ×2; HPA 2–3**.
   - Remove CSI→MCP and AgentHost→Key Vault arrows.
   - Keep API/worker CSI mounts and the server-side runtime secret-store path.
   - Do not copy the retired generic warm pool from `infra-deployment-fig1`.
   - Existing API “reads” versus worker “writes” PostgreSQL labels should not imply exclusive read/write ownership.

   Existing defects: `sabbour/agentweaver:docs/diagrams/src/canonical-aks-components.json:89-98`, `:236-274`; retired generic pool: `sabbour/agentweaver:docs/diagrams/src/infra-deployment-fig1.json:132-150`. Required merge/handoff is explicit at `sabbour/agentweaver:.github/skills/docs-diagram-audit/reports/plan-deep-dive-execution.json:275-286`.

2. **`sandbox-browser-preview-fig1` needs the shared owner to replace “creates objects only”.**
   - API creates Service/HTTPRoute **and validates the generated HTTPS URL**.
   - API does **not** directly probe the pod’s preview port.
   - Show Service+HTTPRoute as two newly created objects, plus a patch to the existing pod—not “three objects the API creates.”

   Implementation: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:202-239`, `:242-308`.

Neither shared asset was modified.

## 2. Minimal corrected diagram models

### `infra-deployment-fig2` — identity and actual secret consumers

Use two visibly different arrow types.

**Authorization lane, dashed**
```text
API SA / worker SA
  → their OIDC federated credentials
  → agentweaver-api-identity
  → Key Vault authorization
```

The identity has **Secrets User and Secrets Officer**, not only Secrets User. API and worker have distinct federation subjects. AgentHost has a separate managed identity with **no Key Vault roles**. Sources: `sabbour/agentweaver:scripts/azure/steps/15-setup-identity.mjs:245-261`, `:315-367`, `:375-438`.

**Secret-data lane, solid**
```text
Key Vault
  → Secrets Store CSI + agentweaver-secrets SPC
     → mounted secret files → startup exports Auth__ApiKey → API / worker
     → synced Kubernetes Secret

Key Vault
  ↔ API/worker application SecretClient / ISecretStore
```

That second branch is essential: applications are **not exclusively file consumers**. Program registers `SecretClient(DefaultAzureCredential)` and `KeyVaultSecretStore`; OAuth certificate loading also uses `SecretClient` directly. Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:231-250`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:319-364`.

**Small exclusion/callout, not another token graph**
```text
MCP: no secret mounts
AgentHost: no vault/CSI token consumer;
           receives purpose-bound /configure payload from server side
```

CSI and startup evidence: `sabbour/agentweaver:k8s/base/secret-provider-class.yaml:16-61`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:90-96`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:90-97`. MCP has empty mounts/volumes: `sabbour/agentweaver:k8s/base/mcp-deployment.yaml:45-56`, `:78-83`.

**Do not draw:** Key Vault→RBAC as secret data, CSI→MCP, direct AgentHost vault fetch, or per-run SPC creation.

### `infra-deployment-fig3` — image convergence and ordered deployment

```text
Resolve environment + desired image tags
  → Resolve four image identities
     ├─ changed / unavailable image → build + push
     └─ reusable image → retag/import
  → Render concrete manifests
  → Namespace/domain prerequisites
  → Service accounts, CSI, RBAC, quota, storage
  → Network policies
  → Services, runtime config, Gateway + routes
  → AgentHost template → warm pool [CRD available]
  → API/frontend/MCP deployments → worker + HPA/PDB
  → Rollout checks + verification
```

Retagging is implemented with `az acr import`; four image definitions are API, frontend, MCP and AgentHost. Worker uses the API image. Sources: `sabbour/agentweaver:scripts/azure/steps/20-build-push-images.mjs:352-381`; `sabbour/agentweaver:scripts/azure/image-spec.mjs:80-141`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:88-97`.

Deployment ordering and CRD gate: `sabbour/agentweaver:scripts/azure/steps/30-deploy.mjs:52-113`, `:427-481`, `:516-561`.

**Qualification:** call this convergence on **desired image tags**, not an inviolable single-tag rule. `AGENTHOST_IMAGE_TAG` defaults to `IMAGE_TAG` but supports an explicit override: `sabbour/agentweaver:scripts/azure/variables.mjs:294-298`.

### `infra-deployment-fig4` — exact public route selection

Keep the small routing sequence:

```text
Client → Gateway HTTPS listener [TLS termination]
       → HTTPRoute match
           ├─ API routes → API Service :8080 → API pod :8080
           ├─ MCP routes → MCP Service :8080 → MCP pod :8080
           └─ / fallback → Frontend Service :80 → frontend pod :8080
```

Route labels:
- API prefixes: `/api`, `/auth`, **`/openapi`**.
- API exact routes: two AS/OIDC discovery documents and configured `/oauth/authorize`, `/token`, `/register`, `/resume`, `/revoke`, `/jwks`.
- MCP: `/mcp` prefix; exact protected-resource discovery variants.
- Exact `/mcp/health` rewrites to `/healthz`.
- Worker is **not a public backend**.

Sources: `sabbour/agentweaver:k8s/base/httproute-api.yaml:25-63`; `sabbour/agentweaver:k8s/base/mcp-httproute.yaml:14-46`; `sabbour/agentweaver:k8s/base/httproute-frontend.yaml:27-34`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:111-117`.

Avoid a broad “all `/oauth`” arrow: the manifest uses specific exact matches.

### `live-preview-provisioning-fig1` — platform-owned preview with real terminal branches

Suggested participants: **Assembly/PreviewStep**, **command resolver**, **AgentHost runner**, **app+forwarder**, **approval gate/operator**, **preview service/Gateway**.

```text
Build & Test verdict
├─ DECLINED → no preview; terminating assembly path
└─ APPROVED / REQUEST_CHANGES
   → existing outcome? return
   → infrastructure unavailable? preview_skipped
   → heuristic command resolution
      → bounded model fallback only if unresolved
      → still unresolved? preview_failed
   → map source cwd into effective execution workspace
   → authenticated start process
   → observe actual app port; app health
   → bind public forwarder; forwarder health
   → approval
      ├─ denied → preview_failed; stop process; no publication
      ├─ expired → preview_failed + retry context;
      │            keep healthy process private; no publication
      └─ approved → recheck process and active run
         → create Service + HTTPRoute; retain backing claim
         → validate exact generated HTTPS URL
            ├─ failure/cancellation → rollback route+Service;
            │                        reconcile retention; no ready
            └─ success → record preview_ready; keep process
   → return to assembly gate processing
```

Core source: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:95-165`, `:167-237`, `:238-324`; declined contract: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:222-239`.

Publication and rollback: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:272-328`. Tests explicitly distinguish unresolved commands, model fallback, denial and retained expired approvals: `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewStepTests.cs:194-277`, `:567-597`.

**Do not label every return “human review continues”.** Return to authored gate processing is accurate; a request-changes verdict can take another path. Also, current assembly code can promote **preview-only** request-changes feedback to approved after preview failure, with conservative classification fallback—see prose correction below.

### `sandbox-browser-preview-fig2` — approved agent publication only

```text
Run-bound agent tool
  → POST /api/runs/{runId}/sandbox/preview
  → validate run/port + contributor access/internal-service allowance
  → AgentPreviewGate
     ├─ explicit auto-approval → Approved
     └─ human approval
        ├─ deny → HTTP 403; no StartPreview
        ├─ expire → HTTP 408 + retry context; no StartPreview
        └─ grant → Approved
  → [Approved only] active-run check;
                   process health recheck when session supplied
  → shared preview-start/publication path
     ├─ publication failure → no ready URL; cleanup
     └─ HTTPS validated → return preview_url
```

Authorization and terminal returns: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:86-137`; post-approval checks and publication: `:139-196`.

Source provenance should name `PreviewRunnerToolProvider` / `PreviewPublishTool`, not `AgentweaverApiTools`. Provider registration: `sabbour/agentweaver:apps/Agentweaver.AgentHost/PreviewRunner.cs:184-199`.

**Input correction:** the public tool schema exposes **`port` and `session_id`**, with run ID server-bound—not “only the port.” Verified by `sabbour/agentweaver:tests/Agentweaver.Tests/Sandbox/StartPreviewToolTests.cs:70-93`. Keep Gateway capability-token `session_id` distinct from this PreviewRunner process session.

## 3. Infrastructure prose corrections

### Workloads, replicas and resources

Replace “three long-running application workloads” with:

> “The deployment separates API, worker, frontend and MCP workloads. API and worker use the API image in different roles; worker orchestration has no public Gateway route.”

Current manifest values:

| Workload/container | Baseline replicas | CPU request / limit | Memory request / limit |
|---|---:|---:|---:|
| API | 2 | 500m / 2 | 512Mi / 4Gi |
| Worker | 2 | 500m / 2 | 512Mi / 4Gi |
| Frontend | 2 | 100m / 500m | 128Mi / 256Mi |
| MCP | 1 | 100m / 1 | 128Mi / 512Mi |
| AgentHost container | warm pool 2 pods | 300m / 800m | 1Gi / 2Gi |
| Executor sidecar | same pods | 700m / 1200m | 2Gi / 4Gi |

Sources: `sabbour/agentweaver:k8s/base/api-deployment.yaml:13-18`, `:368-374`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:13-18`, `:222-228`; `sabbour/agentweaver:k8s/base/frontend-deployment.yaml:10-10`, `:38-44`; `sabbour/agentweaver:k8s/base/mcp-deployment.yaml:10-10`, `:56-62`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:218-227`, `:332-340`; `sabbour/agentweaver:k8s/base/sandbox-warmpool-agenthost.yaml:27-32`.

**Important source-over-comment correction:** worker HPA is **2–3 replicas, CPU 70% and memory 80%**, not CPU-only. Worker PDB keeps one available. `sabbour/agentweaver:k8s/base/worker-hpa.yaml:57-82`, `:100-112`.

Keep resources as a table/operational note, not component-map node clutter. ResourceQuota bounds pod/PVC/storage/claim counts, not aggregate CPU/memory: `sabbour/agentweaver:k8s/base/quota.yaml:9-18`.

### Identity and storage

Recommended replacements:

- **Vault configuration:** “`KEYVAULT_NAME` must be supplied for the environment; there is no default.” The current first paragraph’s `agentweaver-kv` default is false. `sabbour/agentweaver:scripts/azure/variables.mjs:85-100`.
- **Secret consumers:** “API and worker consume CSI-delivered application secrets and use a workload-identity-authenticated server-side secret store. MCP mounts no secrets. AgentHost receives brokered purpose-bound configuration and has no vault roles.”
- **Do not assert MCP federation exists merely because its ServiceAccount carries identity annotations.** Setup code creates API, worker and AgentHost federations; MCP’s manifest contains stale vault-use comments. Keep actual consumer facts, not those comments, in the diagram. `sabbour/agentweaver:scripts/azure/steps/15-setup-identity.mjs:315-438`; `sabbour/agentweaver:k8s/base/serviceaccount-mcp.yaml:12-35`.
- **Storage:** “PostgreSQL is primary application-state storage; the shared workspace is a separate 50Gi Azure Files Premium RWX PVC mounted by API, worker and AgentHost.” Mount ownership is uid/gid 1000, file 0644, directories 0755. `sabbour/agentweaver:k8s/base/pvc-workspace.yaml:4-5`, `:43-51`; `sabbour/agentweaver:k8s/base/storageclass-workspace.yaml:18-32`.
- Remove production `memory.db` backup and RWO/Recreate recovery advice. API/worker use RollingUpdate. **Do not say every RWO artifact was removed:** base deployment still lists `pvc-data.yaml`, defining an old 10Gi RWO claim, though current API/worker workspace mounts use RWX. `sabbour/agentweaver:scripts/azure/steps/30-deploy.mjs:55-68`; `sabbour/agentweaver:k8s/base/pvc-data.yaml:1-12`.
- Replace “all production data lives in PostgreSQL” with “primary application state lives in PostgreSQL”; workspace files and Key Vault secrets are separate persistence domains.
- Remove “backup job” from actual apply order; no such item appears in the deploy groups. `sabbour/agentweaver:scripts/azure/steps/30-deploy.mjs:52-113`.

### Network policy: facts and actual gaps

Recommended replacement for the broad networking paragraphs:

> “Checked-in policies default-deny selected application and AgentHost traffic, then add explicit control-plane, preview, service, database, DNS and HTTPS exceptions. AgentHost preview ingress admits same-namespace preview-Gateway pods on TCP 3000–9000; API and worker can reach AgentHost control traffic on TCP 8088. AgentHost egress permits public-IP HTTPS with explicit private/link-local exclusions, plus DNS and API/MCP exceptions. This is not an enforced external-domain allowlist for Kata pods.”

Evidence:
- App selection/default deny and broad API/MCP HTTPS: `sabbour/agentweaver:k8s/base/networkpolicy-default-deny.yaml:10-41`, `:102-123`.
- Preview ingress: `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:43-60`.
- Public HTTPS/exclusions and Kata DNS-interception explanation: same file `:94-137`.
- API/worker control ingress: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:28-48`.
- Explicit API control egress: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-api-egress.yaml:25-38`.
- AgentHost→API/MCP exceptions: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-egress.yaml:46-75`.

**Source-visible qualifications/gaps that must not be papered over:**

1. **Worker→API ingress is not paired with its stated egress.** Worker policy allows internal `:8080`, but checked-in API ingress admits Gateway, MCP and AgentHost—not worker. This is a manifest coverage gap, **not a live-connectivity test result**. Sources: `sabbour/agentweaver:k8s/base/networkpolicy-worker.yaml:61-83`; `sabbour/agentweaver:k8s/base/networkpolicy-default-deny.yaml:125-171`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:50-74`.

2. **TCP 8088 is inside 3000–9000.** Therefore “only API/worker can reach the A2A listener” is not true solely from these additive rules: preview-Gateway pods are also admitted to that port by the range rule. Authentication/mTLS remain separate protections. Do not change “only Gateway can reach preview ports” into an absolute claim covering 8088. Sources: `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:49-60`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-48`.

3. **The public-IP rule is not a domain restriction.** Its IPv4 exclusions do not include `100.64.0.0/10`, despite a comment grouping CGNAT with link-local. Quote the actual CIDRs rather than promising exclusion of every non-public range. `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:107-134`.

4. **No-token/read-only-root blanket statements are false for AgentHost.** The current template sets `automountServiceAccountToken: true` and AgentHost `readOnlyRootFilesystem: false`; do not conflate the executor’s credential boundary with the AgentHost container. `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:95-114`.

5. Historical “live-confirmed” comments are not current measured effective-policy evidence. No cluster measurements were performed here.

## 4. Preview, credential and scoped pod prose corrections

### Deterministic route and publication

For the opening of `live-preview-provisioning.md`, use:

> “PreviewStep is a platform-owned orchestration stage. It starts and observes the application in the retained AgentHost pod, obtains approval, creates the preview resources, and validates the exact generated HTTPS URL before reporting readiness. It does not directly probe the sandbox’s preview port from the API pod. Command discovery uses heuristics first and a bounded model fallback when needed.”

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:121-165`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:202-212`, `:301-328`.

The deterministic browser data path is:

```text
Browser → HTTPS {token}-preview.{zone}
        → shared preview Gateway / per-preview HTTPRoute
          [upstream Host rewritten to localhost]
        → ClusterIP Service :80 → pod publicPort
        → 0.0.0.0 public-port forwarder → 127.0.0.1 appPort
```

The forwarder scans an available port, skips the actual app port, and uses a randomized starting offset—it does not always select 3000. Sources: `sabbour/agentweaver:apps/Agentweaver.AgentHost/TcpPortForwarder.cs:75-110`; HTTPRoute rewrite: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:1130-1163`.

Keep the two readiness checks distinct:
- in-pod app/forwarder health;
- external publication through the exact generated hostname.

Regression tests withhold ready through DNS failure and HTTP 503, then require success on that URL; permanent failures/cancellation remove publication. `sabbour/agentweaver:tests/Agentweaver.Tests/SandboxPreviewPublicationTests.cs:58-99`, `:138-201`.

### Approval, verdict and retry qualifications

- Replace “all failed preview paths stop the process” with: **denial and non-retryable failures stop it; approval expiry retains the healthy private process with fresh-approval retry context.** `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewStepTests.cs:567-597`.
- Replace old owner-only authorization descriptions with **contributor run access, with internal-service allowance**, and remove the claim that a shared service credential is itself cryptographically limited by a tool closure. The endpoint’s actual check is `RequireRunAccessAsync(... ProjectRole.Contributor, allowInternalService: true)`. `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:86-94`.
- Refresh `start_preview` source-table entries and “model supplies only port” wording using the provider and signature evidence above.
- **New correction beyond the audit:** do not promise the Build & Test decision is always unchanged. Current code can turn REQUEST_CHANGES into approved when preview failed and feedback is positively classified as preview-only. Classification absence/error preserves request-changes. `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1075-1097`, `:4030-4054`.

### Replica and orphan lifecycle

Retain the existing textual lifecycle with these precise statements:

- Claim status resolves `agent-*` first and then retained `run-*`; no API-replica pod registry is needed for preview resolution. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:995-1029`.
- Durable HTTPRoute annotations hold run binding, timestamps, target port, capability token and optional **PreviewRunner session ID**. `:1097-1117`.
- Keepalive updates idle expiry, best-effort touches the supervised process and reasserts claim/eviction retention. `:479-528`.
- Initial idle expiry and hard maximum are both computed from the configured lifetime; keepalive moves idle expiry, **not the original hard maximum**. `:272-276`, `:488-495`.
- `PreviewActive` derives from positive unexpired route evidence. Final-route deletion returns `Previewable`; stopping one of several routes preserves retention. `:788-867`; tests `sabbour/agentweaver:tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs:583-640`.
- Orphan routes use expiry/pod existence; unmatched Services are swept after the two-minute grace. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:126-127`, `:872-923`, `:933-976`.
- Run/token binding is cluster-backed and rejects another run. `:1037-1055`; tests `sabbour/agentweaver:tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs:361-389`.

**Do not overstate retention guarantees:** the implementation uses `_options.LifetimeMinutes * 60 + 600` for active claim TTL and best-effort patches; it does not calculate that TTL from each project-specific route maximum. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:553-595`, `:796-805`.

### Persisted API-side preview token versus pod memory

Replace blanket “credentials must not be persisted” with:

> “Do not log credential values. AgentHost receives preview-control credentials through `/configure` and keeps them in process memory. The server side persists the preview-control value under a deterministic per-run secret-store key so another replica can retrieve it. Each launch mints a fresh value; actual release and orphan cleanup delete it best-effort.”

Evidence:
- 32-byte random mint/deterministic key: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerCredential.cs:24-47`.
- Delivery/persistence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:1264-1285`, `:1362-1392`.
- Pod memory: `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:73-87`, `:160-170`.
- Cross-replica retrieval: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:711-725`.
- Release defers while preview-active, otherwise deletes credential and revokes repository credential: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:956-985`.
- Orphan cleanup does the same after claim deletion: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs:90-113`.

**Persistence is best-effort**, not guaranteed: a failed write still delivers the credential to pod memory, degrading cross-replica recovery. No configured vault URI also selects an in-memory server store. `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:1378-1391`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:231-250`.

Keep environment scrubbing as a separate defense; tests verify missing/wrong credentials fail closed when configured and known secret-bearing child environment entries are removed. `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewRunnerAuthAndScrubTests.cs:49-119`.

### BYOK, GitHub and purpose separation

Keep the credential table, but scope each row:

- **Copilot:** redeemed immutable run capability with `UnattendedCopilot` purpose.
- **Repository:** independently redeemed `UnattendedRepository` purpose.
- **BYOK:** alternative provider configuration; Copilot credential is only required when BYOK is absent.
- **Turn bearer:** production executor always mints 256 bits, even though the request schema permits omission.
- **Preview control:** independent random credential accepted alongside the turn bearer on preview-runner endpoints; durable server-side/pod-memory split described above.
- **MCP broker:** required **exclusively for OperatorAssistant purpose**, not a generic optional credential available to every run.

Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:17-25`, `:79-118`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:511-527`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:257-269`.

Replace “all credentials are short-lived and expiry-enforced” with credential-specific wording. Copilot provider checks the configured run and expiry; BYOK configuration carries an API key but **no expiry field**. Turn/preview secrets are lifecycle-bound, not shown here with embedded expiry validation. `sabbour/agentweaver:apps/Agentweaver.AgentHost/AgentHostGitHubCapabilityCredentialProvider.cs:11-21`; `sabbour/agentweaver:packages/Agentweaver.Domain/ByokProviderConfiguration.cs:11-20`.

Also qualify “AgentHost rejects a turn without its configured token”: middleware enforces it **when nonempty**; preview-runner allows the no-credential development case only when neither token is set. `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:386-399`, `:963-991`.

### Scoped `sandbox-pod-execution.md` edits to recommend

- At its credential/egress section, replace “only model endpoint/bridge/git remotes; everything else denied” with the actual public-HTTPS and explicit east-west rules above.
- State **server-side secret-store persistence** explicitly, avoiding any reading that AgentHost persists preview credentials.
- Replace “expose back through the API” with **“API provisions and validates a Gateway-direct route.”**
- Replace rebuild step 6’s direct Key Vault user-token fetch/no-broker instruction with the purpose-bound Copilot-or-BYOK `/configure` contract.
- The nearby readiness claim is also wrong: `/healthz` returns **200 standby before configure**. Hand this correction to the fig6 owner; do not redraw its sequence in this scope. Source: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:404-411`.

Affected current prose: `sabbour/agentweaver:docs/deep-dive/sandbox-pod-execution.md:402-404`, `:625-644`, `:674-699`, `:739-739`.

## 5. Inline concepts to retain

Retain, rather than add graphics for: credential purpose table; PostgreSQL versus workspace persistence; resource/HPA settings; exact network exceptions and limitations; replica-safe claim/route state; token/run binding; orphan Service cleanup; preview keepalive/retention; approval-expiry retry; Gateway capability URL versus PreviewRunner session; public Gateway URL versus API-host-local `kubectl` fallback. These are the audit’s explicit prose/table retention choices, not missing diagram requirements: `sabbour/agentweaver:.github/skills/docs-diagram-audit/reports/deep-dive-execution.json:156-177`, `:303-324`, `:341-362`, `:390-400`, `:516-525`.

**Boundary:** findings certify checked-in source/test behavior only. Shared canonical assets still require their owners’ correction and publication; no current cluster-policy effectiveness, diagram rendering quality or test execution is claimed.
