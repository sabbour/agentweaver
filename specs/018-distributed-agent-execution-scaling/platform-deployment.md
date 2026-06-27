# Platform / Deployment Design — Distributed Agent Execution & Scaling

**Feature:** 018-distributed-agent-execution-scaling
**Author:** Link (Platform Engineer, Matrix squad)
**Requested by:** Ahmed Sabbour
**Date:** 2026-06-27
**Status:** DESIGN ONLY — no infra changes, no `kubectl`, no provisioning in this task.

> **Project rule honored:** This document does **not** take unilateral infra action. Every
> change below is expressed as a YAML manifest or a `scripts/aks/*` change to be reviewed and
> applied by the deploy pipeline — never `kubectl patch`/`kubectl edit` against a live cluster.

## Scope and cross-references

This doc owns the **platform / deployment** slice of the agreed architecture. It does **not**
re-litigate the architecture and does **not** duplicate sibling specs:

- **Application architecture & coordinator/worker split semantics, sandbox-as-agent-host
  contract, MAF transport bridge:** see Morpheus's `spec.md` (same folder,
  `specs/018-distributed-agent-execution-scaling/spec.md`). The app-entrypoint detail for the
  agent-host image is **deferred to Morpheus**; this doc covers image/runtime/identity/network.
- **Schema migration, run-leasing table design, EF Core provider swap, data backfill:** see
  Tank's `data-postgres-migration.md` (same folder,
  `specs/018-distributed-agent-execution-scaling/data-postgres-migration.md`). DB-side DDL,
  leasing semantics, and connection-pooling sizing live there; this doc covers the **AKS
  connectivity, identity, and deployment** side only.

### Agreed architecture (LOCKED — not relitigated here)

1. Agent execution (worker agents **and** the coordinator's own agent turns) moves into
   **per-run sandbox pods**.
2. The coordinator **orchestration loop** stays in the API/worker tier.
3. The database migrates to **Azure Database for PostgreSQL Flexible Server** (LOCKED).
4. A **web/worker deployment split** + **run leasing** enables horizontal scale.
5. Pods MAY hold a **run-scoped model credential** / use **AKS workload identity** — there is
   **no capability-token broker**.

## Current AKS baseline (investigated; cited)

| Concern | Current state | Citation |
|---|---|---|
| API deployment | `replicas: 1`, `strategy: Recreate`, single pod | `k8s/api-deployment.yaml:10-14` |
| API resources | req `500m`/`512Mi`, limit `2` CPU/`4Gi` | `k8s/api-deployment.yaml:156-162` |
| SQLite data PVC | `/data` RWO Azure Disk, single-writer | `k8s/api-deployment.yaml:144-189`, `docs/architecture-aks.md:294-301` |
| Workspace PVC | `/workspace` RWX Azure Files | `k8s/api-deployment.yaml:190-192`, `docs/architecture-aks.md:286-291` |
| Secrets | Key Vault via Secrets Store CSI, `mcp-*` objects | `k8s/api-deployment.yaml:197-202`, `k8s/secretprovider-mcp.yaml:16-36` |
| Workload identity | UAMI + federated cred on SA `agentweaver-api` | `k8s/serviceaccount-api.yaml:6-8`, `scripts/aks/15-setup-identity.sh:69-91` |
| Sandbox template | Kata VM isolation, `automountServiceAccountToken: false`, `runtimeClassName: kata-vm-isolation`, no SA token | `k8s/sandbox-template.yaml:16-18` |
| Sandbox resources | req `250m`/`256Mi`, limit `1` CPU/`4Gi` | `k8s/sandbox-template.yaml:39-45` |
| Warm pool | `replicas: 3` | `k8s/sandbox-warmpool.yaml:11` |
| Sandbox egress | DNS + GitHub IP range (NetworkPolicy); FQDN allowlist incl. `*.openai.azure.com`, `*.services.ai.azure.com` (Cilium) | `k8s/networkpolicy-sandbox.yaml:30-49`, `k8s/cilium-network-policy-sandbox.yaml:26-47` |
| Sandbox ingress | deny-all | `k8s/networkpolicy-sandbox.yaml:10-15` |
| API RBAC | `sandboxclaims` get/create/delete; `pods` get/create; `pods/exec` create | `k8s/rbac-api.yaml:9-29` |
| Claim creation | API creates `SandboxClaim run-{runId}`, polls `phase: Bound`, execs | `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:165-227` |
| Pod exec | WebSocket `/bin/sh -c <script>` into container `agentweaver-sandbox` | `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:247-285` |
| Preview tunnel | in-pod `kubectl port-forward` from API process | `apps/Agentweaver.Api/Sandbox/PortForwardService.cs:99-119` |
| Quota | `pods: 25`, `requests.cpu: 8`, PDBs `minAvailable: 1` | `k8s/quota.yaml:9-17`, `k8s/quota.yaml:39-77` |
| Sandbox image | `ubuntu:24.04`, `CMD ["sleep","infinity"]` | `apps/agentweaver-sandbox/Dockerfile:36` |
| Deploy flow | `envsubst` render + `kubectl apply`, ordered | `scripts/aks/30-deploy.sh:79-157` |

---

## 1. Azure Database for PostgreSQL Flexible Server — provisioning & connectivity

### 1.1 Connectivity model: Private access (RECOMMENDED) vs Public + firewall

Two supported networking modes for Flexible Server:

| Option | How it works | Verdict |
|---|---|---|
| **Public access + firewall rules** | Server has a public endpoint; AKS egress (NAT/outbound) IPs allowlisted | Simplest, but exposes a public DB endpoint and couples to fragile egress-IP allowlists. **Not recommended for prod.** |
| **Private access — VNet integration (delegated subnet)** | Flexible Server injected into a delegated subnet in the AKS VNet; reached over a private IP + Private DNS zone | **RECOMMENDED.** No public surface; clean NSG/NetworkPolicy story. |
| **Private endpoint (PE)** | PE in AKS subnet projects a private IP; works with the "public access" networking mode + PE | Acceptable alternative when VNet-injection is blocked by subnet topology. |

**Recommendation:** **Private access via VNet integration** with a dedicated delegated subnet in
the AKS VNet, plus the `privatelink.postgres.database.azure.com` Private DNS zone linked to the
VNet. This is consistent with the cluster's existing "no public app surface" posture (gateway is
the only ingress; `docs/architecture-aks.md:61-86`). New provisioning belongs in a new
`scripts/aks/17-provision-postgres.sh` (mirrors the `az`-driven style of
`scripts/aks/15-setup-identity.sh`), **not** applied ad-hoc.

### 1.2 Auth: passwordless workload identity (RECOMMENDED) vs Key Vault secret

The cluster already runs Azure Workload Identity end-to-end: OIDC issuer enabled, UAMI
`agentweaver-api-identity`, federated credential bound to
`system:serviceaccount:agentweaver:agentweaver-api`
(`scripts/aks/15-setup-identity.sh:69-91`), and the pod template carries
`azure.workload.identity/use: "true"` (`k8s/api-deployment.yaml:23-24`).

**Recommendation: Microsoft Entra (passwordless) auth via workload identity.** No DB password
ever lives in Key Vault or a pod env var; the app exchanges the federated SA token for an Entra
access token (audience `https://ossrdbms-aad.database.windows.net`) and presents it as the
Postgres password at connect time.

App-config implications (DB-side connection-string/provider details are Tank's; the **platform**
implications are):

- **Provision steps** (new `17-provision-postgres.sh`): set the Entra admin on the server; create
  a DB role mapped to the UAMI principal (`pgaadauth`/Entra role) with least-priv grants on the
  app schema; grant the role to the app database.
- **No new Key Vault object** and **no new CSI mount** for the DB — this is the cleaner path and
  avoids password rotation entirely.
- The connection host is the **private FQDN** of the Flexible Server; resolution depends on the
  Private DNS zone link (§1.1).
- The app needs an Entra token-credential provider wired to the existing workload-identity
  env (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE` injected by the
  webhook). Provider/Npgsql wiring is **Tank's** to specify.

**Fallback (documented, not preferred): password in Key Vault via CSI.** If Entra DB auth is
ruled out, store a `postgres-admin-password` (or app-user password) secret in the existing Key
Vault and surface it through a new `SecretProviderClass` object entry, mirroring the `mcp-*`
pattern exactly (`k8s/secretprovider-mcp.yaml:16-36`) and consumed the same way the API reads
`/mnt/secrets-store/mcp-api-key` today (`k8s/api-deployment.yaml:78-84`). This re-introduces
rotation burden — hence non-preferred.

### 1.3 High availability

Enable **Zone-redundant HA** on the Flexible Server (primary + standby in different AZs) for
prod, on a `GeneralPurpose` (or higher) tier that supports HA. Pair with **zone-redundant**
backups. Sizing/SKU is a capacity decision flagged as an open question (§6); the platform
default proposal is GP, zone-redundant HA, 7–35 day PITR retention.

### 1.4 Replacing the `/data` PVC — unblocking RollingUpdate + replicas > 1

The `/data` RWO Azure Disk exists **only** because SQLite is single-writer. The Deployment is
pinned to `replicas: 1` + `strategy: Recreate` precisely to prevent RWO multi-attach and SQLite
corruption (`k8s/api-deployment.yaml:10-14`, `docs/architecture-aks.md:294-301`). Both DBs
(`agentweaver.db`, `memory.db`) live on that disk (`docs/architecture-aks.md:272-284`).

Once Postgres is the backing store, the platform changes are:

- **Drop the `agentweaver-data` PVC and its mounts** (`k8s/api-deployment.yaml:144-146`,
  `186-189`) and remove `pvc-data.yaml` from the deploy ordering
  (`scripts/aks/30-deploy.sh:100`). Remove `Database__Path`/`HOME=/data` env
  (`k8s/api-deployment.yaml:44-47`, `93-96`).
- **Flip the deployment to `strategy: RollingUpdate`** and allow `replicas > 1` — the
  single-writer constraint is gone.
- **Migrations:** the `migrate-memory-db` init container (`k8s/api-deployment.yaml:34-68`)
  stays, but runs `efbundle` against **Postgres** instead of the SQLite file. With multiple
  replicas, the init container runs per-pod; rely on EF's migration-history table for idempotency
  (concurrent-apply guard is Tank's to confirm — see §6). Init-container env that pointed at
  `/data` (`k8s/api-deployment.yaml:44-51`) is replaced by the Postgres connection config.
- **`/workspace` RWX Azure Files PVC stays** (`k8s/api-deployment.yaml:190-192`) — it is RWX and
  multi-attach-safe, so it does not block horizontal scale and is still needed for git worktrees
  shared with sandbox pods (`k8s/sandbox-template.yaml:51-54`).
- **Backups:** `backup-cronjob.yaml` (which snapshots SQLite — `scripts/aks/30-deploy.sh:120`)
  is superseded by Flexible Server's managed backups; retire or repurpose it (flagged §6).

---

## 2. Web/Worker deployment split

Two Deployments **from the same image** (`agentweaver-api:${IMAGE_TAG}`) differentiated by a role
flag — e.g. `Agentweaver__Role=web` vs `Agentweaver__Role=worker` (new env, mirrors the existing
env-driven config in `k8s/api-deployment.yaml:90-143`). No second image to build/push; the
`scripts/aks/20-build-push-images.sh` flow is unchanged.

### 2.1 `agentweaver-web` Deployment

- Serves HTTP/auth/UI/MCP ingress; keeps the gateway ingress rules
  (`k8s/networkpolicy-default-deny.yaml:121-172`) and OAuth/secret env exactly as today.
- **Does not** claim runs or run MAF; orchestration endpoints enqueue work for workers.
- **Autoscale on request load:** HPA on CPU and/or a request-rate custom metric. Stateless once
  SQLite is gone → safe to scale `2..N`.

### 2.2 `agentweaver-worker` Deployment

- **Claims runs via leasing** (lease table/semantics owned by Tank's
  `data-postgres-migration.md`), runs the coordinator orchestration loop / MAF in-process, and
  **dispatches per-run sandbox pods** using the existing `KubernetesSandboxExecutor` path
  (`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:165-285`).
- Therefore the **worker** SA needs the sandbox RBAC (`k8s/rbac-api.yaml:9-29`) and workload
  identity, while **web** may drop sandbox RBAC (least privilege). This implies splitting the SA:
  keep `agentweaver-api` for web, add `agentweaver-worker` SA with its own federated credential
  (new step in `15-setup-identity.sh`) and bind the sandbox Role to it
  (`k8s/rbac-api.yaml:31-46`).
- **Autoscale on run/queue depth**, not CPU (claim-then-dispatch work is I/O-bound).

### 2.3 HPA vs KEDA

- **Web:** plain **HPA** (CPU + optional request-rate) is sufficient.
- **Worker:** prefer **KEDA** with a **PostgreSQL scaler** querying *unleased/queued run depth*
  (e.g. `SELECT count(*) FROM runs WHERE state='queued' AND lease IS NULL`). This scales on the
  true backlog and supports **scale-to-min** (not zero — workers must keep leasing/draining).
  The exact query/threshold is a contract with Tank's lease schema (§6). KEDA availability on the
  cluster is an **open question** (§6); if KEDA is unavailable, fall back to an HPA on a custom
  "queued runs" metric exported via OTEL/Prometheus (`k8s/api-deployment.yaml:134-141` shows OTEL
  is already wired).

### 2.4 PodDisruptionBudgets & graceful drain

- Add a PDB for `agentweaver-web` and `agentweaver-worker` mirroring the existing `minAvailable: 1`
  pattern (`k8s/quota.yaml:39-77`). For workers, prefer `maxUnavailable: 1` so leases drain one
  pod at a time.
- **Graceful worker drain on SIGTERM:** stop claiming new runs, **release held leases** (or let
  them lease-expire so another worker re-claims), and finish/checkpoint in-flight orchestration
  before exit. Requires a `preStop` hook / `terminationGracePeriodSeconds` tuned above the lease
  TTL. The drain semantics (lease release vs expiry) are jointly owned with Tank; platform
  provides the lifecycle hooks.

---

## 3. Sandbox pod execution at scale (agent-host pods)

Sandbox pods now run the **full agent** (worker agents + coordinator agent turns), not just shell
commands. Implications:

### 3.1 Warm-pool sizing & per-pod resources

- Today: warm pool `replicas: 3` (`k8s/sandbox-warmpool.yaml:11`), per-pod req `250m`/`256Mi`,
  limit `1` CPU/`4Gi` (`k8s/sandbox-template.yaml:39-45`). A `sleep infinity` pod is cheap; a
  pod hosting a live MAF agent + model I/O is **not**.
- **Raise resource requests** to reflect a real agent runtime (proposed: req `500m`/`1Gi`,
  limit `2` CPU/`4Gi`) — final numbers are a capacity decision (§6). The LimitRange `max` is
  `4`CPU/`4Gi` (`k8s/quota.yaml:35-37`); raising CPU limits beyond `1` is within current max.
- **Warm-pool budget** must be reconciled with concurrency targets and quota. Right-size
  `replicas` to the expected steady-state concurrent-run count; oversizing wastes Kata-VM
  capacity, undersizing adds claim latency (the executor polls every 2s for `Bound` —
  `KubernetesSandboxExecutor.cs:225`).

### 3.2 ResourceQuota changes

`k8s/quota.yaml:9-17` currently caps `pods: 25`, `requests.cpu: 8`, `requests.memory: 16Gi`,
`count/sandboxclaims: 20`. With heavier per-pod requests **and** multiple web/worker replicas,
these caps will throttle scale. The quota must be **raised deliberately** (a reviewed YAML change,
not a live patch) — proposed direction: lift `pods`, `requests.cpu/memory`, and `sandboxclaims`
to match the target concurrent-run SLO. Exact ceilings are an open question tied to node-pool
capacity (§6).

### 3.3 Run-scoped model credential injection (NOT baked into image)

The model credential must be injected **per pod at claim time**, never baked into
`agentweaver-sandbox` image layers. Two viable paths:

1. **Workload identity per sandbox pod (preferred, passwordless):** give the sandbox pod template
   a dedicated SA (e.g. `agentweaver-sandbox-runner`) with `azure.workload.identity/use: "true"`
   and a federated credential, then have the agent acquire an Entra token for the model endpoint
   at runtime. **Tension:** the sandbox template currently sets
   `automountServiceAccountToken: false` (`k8s/sandbox-template.yaml:18`) specifically to keep
   Kata-isolated pods tokenless. Workload identity needs a **projected** token volume. The
   resolution is to project **only** the workload-identity OIDC token (a narrowly-scoped projected
   `serviceAccountToken` volume with the AzureADTokenExchange audience), **not** re-enable the
   full k8s API SA token. This preserves the "no cluster API access from sandbox" property while
   enabling passwordless model auth.
2. **Run-scoped secret string injected at claim time:** the worker mints/fetches a short-lived,
   run-scoped model key and passes it into the pod for that run only. The current executor passes
   env via the exec shell script, not the pod spec
   (`KubernetesSandboxExecutor.cs:381-395`), and the SandboxTemplate sets
   `envVarsInjectionPolicy: Disallowed` (`k8s/sandbox-template.yaml:9`) — so this path needs a
   per-claim projected secret or a relaxation of that policy. Higher blast radius than (1).

**Recommendation:** path (1) — projected workload-identity token only, run agent acquires model
token itself. No long-lived secret in the pod, consistent with the cluster's passwordless posture.
(The app-side credential acquisition is Morpheus's entrypoint concern.)

### 3.4 Egress allowlist updates

Pods now need the **model endpoint** (already partially covered) and connectivity back to the
**API/worker** tier. Key clarification:

> **Sandbox pods should talk to the API/worker tier, NOT directly to Postgres.** All run-state
> reads/writes flow through the worker's leasing/orchestration APIs. Postgres stays reachable
> only from the web/worker tier. This keeps the DB blast radius tiny and avoids handing DB
> credentials to Kata-isolated, internet-egressing agent pods.

Concrete allowlist changes:

- **Model endpoint:** the Cilium FQDN allowlist already permits `*.openai.azure.com`,
  `*.services.ai.azure.com`, `*.cognitiveservices.azure.com`, `*.models.ai.azure.com`
  (`k8s/cilium-network-policy-sandbox.yaml:39-47`). Confirm the chosen model host matches; add
  the Entra token endpoint (`login.microsoftonline.com`) if path (1)/§3.3 is used.
- **API/worker reachability:** add an **egress** allow from sandbox pods to the web/worker
  Service on `8080` (see §4 — only if gRPC-over-ClusterIP is chosen; the exec path needs no new
  egress).
- **No Postgres FQDN** in the sandbox allowlist — intentionally omitted.
- The plain `NetworkPolicy` GitHub IP range (`k8s/networkpolicy-sandbox.yaml:44-49`) and Cilium
  FQDN policy must be kept in sync.

### 3.5 Dockerfile / image story

`apps/agentweaver-sandbox/Dockerfile` currently ends in `CMD ["sleep","infinity"]`
(`apps/agentweaver-sandbox/Dockerfile:36`) — a passive shell target. It must become the
**agent-host image**: same base toolchain (it already ships .NET 9, Python, Node, git —
`Dockerfile:5-22`), but the entrypoint runs the agent-host process that the worker connects to
over the MAF bridge.

Platform-side requirements for the image (the **app entrypoint command is deferred to
Morpheus**):

- **Runtime:** keep `runtimeClassName: kata-vm-isolation` and non-root `runAsUser: 1000`,
  `readOnlyRootFilesystem: true`, `drop: ALL`, `seccompProfile: RuntimeDefault`
  (`k8s/sandbox-template.yaml:16-38`) — agent-host must run within these constraints.
- **Identity:** projected workload-identity token volume per §3.3 (this changes the template, not
  the image).
- **Network:** if the bridge is gRPC (§4), the container must `EXPOSE`/listen on a fixed port and
  the SandboxTemplate gains a `containerPort`; if exec-stdio (§4), no port and no ingress.
- **Image pull:** `imagePullPolicy: IfNotPresent` (`k8s/sandbox-template.yaml:29`) stays; the
  warm pool relies on the image being cached on nodes.

---

## 4. Transport networking — API/worker ↔ agent-host pod

The MAF bridge needs the worker tier to drive agent turns inside the sandbox pod. Two transports:

| | **gRPC over ClusterIP** | **kube-exec-stdio (current mechanism)** |
|---|---|---|
| How | Sandbox pod runs a gRPC server; worker dials a `Service`/pod IP | Worker opens a WebSocket `pods/exec` stream and pipes MAF frames over stdio |
| New k8s surface | Needs a **Service** + a **NetworkPolicy east-west ingress** rule onto sandbox pods | **None** — reuses existing `pods/exec` RBAC (`k8s/rbac-api.yaml:25-29`) |
| Ingress to sandbox | **Opens ingress** to sandbox pods (today deny-all — `k8s/networkpolicy-sandbox.yaml:10-15`) | Sandbox stays **ingress deny-all**; exec is an egress-initiated control-plane channel |
| Kata isolation fit | Requires a listening socket inside the VM + a pod Service | Already proven through Kata exec today (`KubernetesSandboxExecutor.cs:247-285`) |
| Ops | Service discovery, port mgmt, mTLS to add | No new moving parts; same path the port-forward/exec code already uses |
| Throughput/streaming | Cleaner for long-lived bidi streaming | Workable; stdio framing over exec is the existing pattern |

**Recommendation: start with kube-exec-stdio.** It reuses the existing `pods/exec` RBAC, keeps
sandbox pods **ingress deny-all**, and avoids punching an east-west ingress hole into
Kata-isolated agent pods — the most conservative security posture and zero new NetworkPolicy
ingress. The prior `allow-mcp-to-api` east-west pattern
(`k8s/networkpolicy-default-deny.yaml:152-172`) shows the cost of any new ingress rule: it is a
deliberate, explicitly-scoped exception. We avoid adding one for sandbox ingress.

If exec-stdio proves a bottleneck for high-throughput streaming, **gRPC-over-ClusterIP is the
documented upgrade path**: add a sandbox `Service`, a `containerPort` in the SandboxTemplate, a
single tightly-scoped NetworkPolicy ingress (worker pods → sandbox `:port`), mTLS, and the
matching sandbox egress allow (§3.4). Defer until measured.

---

## 5. Rollout / migration sequencing on AKS

All changes land as **reviewed YAML + `scripts/aks` edits**, applied through the existing rendered
`kubectl apply` pipeline (`scripts/aks/30-deploy.sh:79-157`). **No `kubectl patch`/`edit`.** Keep
an **in-API fallback flag** (e.g. `Sandbox__Backend`/`Agentweaver__Role` + a
`Database__Provider=sqlite|postgres` toggle) so each step is reversible.

1. **Provision Postgres (no app cutover).** New `scripts/aks/17-provision-postgres.sh`: VNet
   subnet delegation, Flexible Server (zone-redundant HA), Private DNS zone link, Entra admin +
   UAMI DB role (§1). App still on SQLite. *Rollback: none needed (app untouched).*
2. **Identity wiring.** Extend `15-setup-identity.sh` with the worker SA federated credential and
   (if §3.3 path 1) the sandbox-runner SA. Add the DB role grant. *Rollback: drop fed creds.*
3. **Dual-write / read-switch by flag (Tank-owned migration).** Ship the EF Postgres provider
   behind `Database__Provider`. Run `efbundle` against Postgres via the init container against a
   single replica first. Backfill per Tank's plan. *Rollback: flip provider flag to `sqlite`.*
4. **Drop `/data` PVC, enable RollingUpdate.** Once Postgres reads/writes are verified, remove
   the data PVC + mounts (`k8s/api-deployment.yaml:144-189`), remove `pvc-data.yaml` from
   `30-deploy.sh:100`, set `strategy: RollingUpdate`, `replicas: 2`. *Rollback: re-add PVC +
   Recreate + provider=sqlite (data-loss-aware — backups required before this step).*
5. **Web/worker split.** Add `web-deployment.yaml` + `worker-deployment.yaml` (role flag),
   `worker-pdb`, split SAs/RBAC, retire the combined `api-deployment.yaml` (or keep it as `web`).
   Add to `30-deploy.sh` apply order after identity/RBAC. *Rollback: scale workers to 0; web
   falls back to the in-process orchestration path behind the role flag.*
6. **Autoscaling.** Add `hpa-web.yaml` and `keda-scaledobject-worker.yaml` (or `hpa-worker.yaml`
   fallback). *Rollback: delete the HPA/ScaledObject; fixed `replicas` remains.*
7. **Agent-host sandbox image + template.** Update `apps/agentweaver-sandbox/Dockerfile`
   entrypoint (Morpheus), projected-identity token volume, raised resources, quota bump
   (`k8s/quota.yaml`), egress updates (§3.4). Roll the warm pool. *Rollback: pin the previous
   sandbox image tag in the SandboxTemplate; warm pool re-pulls.*
8. **Transport.** Ship exec-stdio bridge (no manifest change beyond existing RBAC). Defer gRPC
   Service/ingress unless measured (§4). *Rollback: feature-flag the bridge off.*

Each step is independently revertible via flag or a reverse YAML apply, satisfying the
"keep the in-api fallback flag for rollback" requirement.

---

## 6. Open questions for the user

1. **Postgres connectivity:** VNet-integration (delegated subnet) vs Private Endpoint — which
   fits the existing AKS VNet/subnet topology? (Platform recommends VNet integration.)
2. **DB auth:** confirm **Entra passwordless via workload identity** (recommended) vs Key Vault
   password. Passwordless requires setting an Entra admin and mapping the UAMI to a DB role — OK?
3. **KEDA availability:** is KEDA installed/allowed on the cluster? If not, accept the
   OTEL/Prometheus custom-metric HPA fallback for worker autoscaling?
4. **Warm-pool budget & resources:** target concurrent-run count? This drives warm-pool
   `replicas`, per-pod requests/limits, and the ResourceQuota ceilings (`k8s/quota.yaml`).
5. **Sandbox identity for model auth:** OK to add a projected workload-identity token volume to
   the (currently tokenless) sandbox pod for passwordless model auth, or prefer run-scoped
   injected key? (Platform recommends projected token, OIDC-exchange audience only.)
6. **HA SKU/cost:** approve `GeneralPurpose` zone-redundant HA tier and PITR retention window
   (7–35 days)?
7. **Migration concurrency:** confirm EF migration-history idempotency is safe when the init
   container runs per-replica (multi-replica rollout) — coordinate with Tank.
8. **Backups:** retire `backup-cronjob.yaml` (SQLite snapshotter) in favor of Flexible Server
   managed backups — confirm.

---

## Plain-text summary

The platform plan turns the current single-replica, SQLite-on-RWO-disk AKS deployment
(`k8s/api-deployment.yaml:10-14`, `144-189`) into a horizontally scalable, Postgres-backed
system. PostgreSQL Flexible Server is provisioned with **private VNet access** and reached with
**passwordless Entra auth over the cluster's existing workload identity**
(`scripts/aks/15-setup-identity.sh:69-91`, `k8s/api-deployment.yaml:23-24`), with **zone-redundant
HA**. Removing SQLite lets us **drop the `/data` PVC**, switch to **RollingUpdate**, and run
**replicas > 1** (`docs/architecture-aks.md:294-301`). The single API Deployment splits into a
**web** tier (HTTP/UI, HPA on request load) and a **worker** tier (claims leased runs, runs MAF,
dispatches sandbox pods, KEDA-scaled on Postgres queue depth) from the **same image** via a role
flag, each with PDBs and graceful lease-draining. Per-run **sandbox pods become full agent hosts**
— the `sleep infinity` Dockerfile (`apps/agentweaver-sandbox/Dockerfile:36`) becomes the
agent-host image (entrypoint deferred to Morpheus), with raised resources/quota
(`k8s/quota.yaml:9-17`), a **per-pod projected workload-identity token** for passwordless model
auth (not baked into the image), and egress updates that keep pods talking to the **worker tier,
not Postgres**. The MAF transport recommendation is **kube-exec-stdio** (reuses existing
`pods/exec` RBAC, `k8s/rbac-api.yaml:25-29`; keeps sandbox ingress deny-all), with
**gRPC-over-ClusterIP** as a documented, deferred upgrade. Rollout is sequenced and fully
flag-reversible, applied via the existing `scripts/aks` render-and-apply pipeline — **never
`kubectl patch`**. Schema/leasing details are owned by Tank's `data-postgres-migration.md`;
agent/app architecture by Morpheus's `spec.md`.

**Open questions:** (1) VNet-integration vs private endpoint; (2) confirm passwordless Entra DB
auth; (3) KEDA availability; (4) warm-pool budget + sandbox resource sizing + quota ceilings;
(5) projected token vs injected key for sandbox model auth; (6) HA SKU/retention approval;
(7) per-replica EF migration concurrency safety; (8) retiring the SQLite backup CronJob.
