# spec-018 Deploy Runbook

## What Shipped (Phase 1 — behavior-preserving rollout)

| Item | Value |
|------|-------|
| **Date** | 2026-06-27 |
| **Image tag** | `92e4d74c` |
| **Cluster** | `agentweaver-aks-2` / namespace `agentweaver` |
| **Database** | `Database__Provider=sqlite` (unchanged) |
| **App role** | `App__Role` unset → defaults to `web` |
| **Sandbox mode** | `Sandbox__Backend=kubernetes` (pod-per-run NOT active yet; `in-api` dispatch remains default until A2A path is exercised) |
| **Replicas** | 1 / strategy Recreate (unchanged) |

### Images pushed to ACR

```
agentweaverregistry.azurecr.io/agentweaver-api:92e4d74c       sha256:ad9c72e...
agentweaverregistry.azurecr.io/agentweaver-frontend:92e4d74c  (cc26 Succeeded)
agentweaverregistry.azurecr.io/agentweaver-mcp:92e4d74c       sha256:e688f90...
agentweaverregistry.azurecr.io/agentweaver-sandbox:92e4d74c   sha256:a00fdb5...
```

### What was applied

- `k8s/networkpolicy-agenthost.yaml` — updated: added `agentweaver-worker` to ingress from-selector on port 8088 (alongside existing `agentweaver-api`; additive, safe).
- `k8s/api-deployment.yaml` — image `921fedc` → `92e4d74c`; new env vars `ConnectionStrings__Postgres` + `ConnectionStrings__MemoryDb` (both `optional: true`) and `Database__Provider=sqlite` added.
- `k8s/frontend-deployment.yaml` — image updated to `92e4d74c`.
- `k8s/mcp-deployment.yaml` — image updated to `92e4d74c`.

### What was NOT applied (held)

- `k8s/worker-deployment.yaml`
- `k8s/worker-hpa.yaml`
- `k8s/networkpolicy-worker.yaml`

### Pre-deploy secrets created (persistent)

| Secret | Keys |
|--------|------|
| `agentweaver-postgres` | `host`, `port`, `database`, `username`, `password`, `connectionstring` |
| `agentweaver-a2a-ca` | `ca.crt` |
| `agentweaver-a2a-server-tls` | `tls.crt`, `tls.key`, `ca.crt` |
| `agentweaver-a2a-client-tls` | `tls.crt`, `tls.key`, `ca.crt` |

### Health gate result

```
agentweaver-api-67f5fb9b85-nkldl   1/1   Running   0 restarts
GET /api/health → {"status":"ok"}
```

### Rollback (if needed)

```bash
kubectl -n agentweaver rollout undo deploy/agentweaver-api
kubectl -n agentweaver rollout undo deploy/agentweaver-frontend
kubectl -n agentweaver rollout undo deploy/agentweaver-mcp
# Verify:
kubectl -n agentweaver rollout status deploy/agentweaver-api
kubectl -n agentweaver exec deploy/agentweaver-api -- curl -sf http://localhost:8080/api/health
```

---

## Remaining Cutover Steps (attended, in order)

### Prerequisites

- Tank's Postgres migration set must be merged and image rebuilt (Agentweaver.Api.Data + Agentweaver.Api.Migrations.Postgres EF Core migration bundle for the `agentweaver` database)

---

### Step C1: Verify Postgres connectivity

```bash
# From any pod that can reach 10.225.0.5:5432
kubectl -n agentweaver run pg-test --rm -it --image=postgres:16 --restart=Never -- \
  psql "host=agentweaver-pg.postgres.database.azure.com port=5432 dbname=agentweaver sslmode=require" \
  -c "SELECT version();"
```

**Rollback**: N/A (read-only check)

---

### Step C2: Apply EF Core migrations via efbundle init container (single replica)

The API Dockerfile builds an `efbundle` binary that runs SQLite migrations. When `Database__Provider=Postgres`, the init container runs the Postgres migrations bundle.

```bash
# First, verify the existing API pod's init container runs efbundle successfully against SQLite:
kubectl -n agentweaver logs -l app=agentweaver-api -c migrate-memory-db

# The api-deployment.yaml init container already sets Database__Provider=sqlite.
# For Postgres, temporarily patch the init container env (via a separate manifest):
# See k8s/api-deployment.yaml initContainers[0].env — change Database__Provider to Postgres.
# Apply the manifest, watch init container logs:
kubectl -n agentweaver logs -f -l app=agentweaver-api -c migrate-memory-db
```

**Validation**: Init container exits 0; Postgres database `agentweaver` has the EF migration history table populated.

**Rollback**: Revert `Database__Provider` back to `sqlite` in api-deployment.yaml, apply, verify.

---

### Step C3: Run data migrator (SQLite → Postgres)

```bash
# One-shot job using SqliteToPostgresMigrator tool (in the API image)
kubectl -n agentweaver run data-migrator --rm -it \
  --image=agentweaverregistry.azurecr.io/agentweaver-api:92e4d74c \
  --restart=Never \
  --env="Database__Provider=Postgres" \
  --env="ConnectionStrings__Postgres=$(kubectl -n agentweaver get secret agentweaver-postgres -o jsonpath='{.data.connectionstring}' | base64 -d)" \
  --env="ConnectionStrings__MemoryDb=/data/agentweaver.db" \
  --overrides='{"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"agentweaver-data"}}],"containers":[{"name":"data-migrator","volumeMounts":[{"name":"data","mountPath":"/data"}]}]}}' \
  -- dotnet Agentweaver.Api.dll --migrate-data
```

> **NOTE**: This is Tank's responsibility. Coordinate with Tank on the exact command and validation criteria.

**Validation**: Row counts match between SQLite source and Postgres.

**Rollback**: Data migrator is read-only on Postgres (inserts only). Rolling back means keeping `Database__Provider=sqlite` — no Postgres rows are read by the API yet.

---

### Step C4: Flip Database__Provider to Postgres on API

Edit `k8s/api-deployment.yaml`:
```yaml
- name: Database__Provider
  value: "Postgres"   # was: "sqlite"
```

Apply:
```bash
# Substitute vars and apply
export ACR_LOGIN_SERVER=agentweaverregistry.azurecr.io IMAGE_TAG=<current-tag>
export IDENTITY_CLIENT_ID=31d79e6e-647f-4402-b953-60d598ca9054
export KEYVAULT_NAME=agentweaver-kv TENANT_ID=72f988bf-86f1-41af-91ab-2d7cd011db47
export HOST=agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io
envsubst < k8s/api-deployment.yaml | kubectl apply -f -
kubectl -n agentweaver rollout status deploy/agentweaver-api --timeout=180s
kubectl -n agentweaver exec deploy/agentweaver-api -- curl -sf http://localhost:8080/api/health
```

**Validation**: Health check passes; create a test run and verify it persists in Postgres.

**Rollback**:
```bash
# Flip back to sqlite
kubectl -n agentweaver rollout undo deploy/agentweaver-api
```

---

### Step C5: Remove SQLite constraint, enable RollingUpdate + replicas:2

Once Postgres is verified as the primary datastore, the SQLite single-writer constraint is lifted:

Edit `k8s/api-deployment.yaml`:
```yaml
replicas: 2
strategy:
  type: RollingUpdate
  rollingUpdate:
    maxSurge: 1
    maxUnavailable: 0
```

Apply and verify:
```bash
envsubst < k8s/api-deployment.yaml | kubectl apply -f -
kubectl -n agentweaver rollout status deploy/agentweaver-api --timeout=180s
kubectl -n agentweaver get pods -l app=agentweaver-api
```

**Validation**: 2 running API pods, both serving `/api/health`.

---

### Step C6: Deploy worker + HPA + NetworkPolicies

Apply the held manifests:

```bash
# Apply worker network policies first
envsubst < k8s/networkpolicy-worker.yaml | kubectl apply -f -

# Apply worker deployment and HPA
envsubst < k8s/worker-deployment.yaml | kubectl apply -f -
envsubst < k8s/worker-hpa.yaml | kubectl apply -f -

# Watch rollout (worker init container runs EF Postgres migrations — may take 1-2 min)
kubectl -n agentweaver rollout status deploy/agentweaver-worker --timeout=300s
kubectl -n agentweaver get pods -l app=agentweaver-worker
```

**Validation**:
```bash
# Worker health check (port 8081)
kubectl -n agentweaver exec deploy/agentweaver-worker -- curl -sf http://localhost:8081/healthz
# Logs should show coordinator heartbeat and run-pickup loops starting
kubectl -n agentweaver logs -l app=agentweaver-worker --tail=50
```

**Rollback**:
```bash
kubectl -n agentweaver delete deploy/agentweaver-worker
kubectl -n agentweaver delete hpa/agentweaver-worker-hpa
kubectl -n agentweaver delete pdb/agentweaver-worker-pdb
# NetworkPolicies are additive; leave in place (harmless without worker pods)
```

---

### Step C7: Web tier HPA (optional, post-worker)

Once the worker handles background processing and the web tier is stateless (Postgres), apply web HPA:

```bash
# See commented template in k8s/worker-hpa.yaml (bottom section)
# Recommended: min 2 / max 5 / CPU 60%
kubectl -n agentweaver apply -f - <<EOF
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: agentweaver-api-hpa
  namespace: agentweaver
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: agentweaver-api
  minReplicas: 2
  maxReplicas: 5
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 60
EOF
```

---

### Step C8: KEDA upgrade (optional, post-worker)

Replace CPU-based HPA with PostgreSQL queue-depth scaler once KEDA is confirmed available:

```bash
kubectl api-resources --api-group=keda.sh | grep ScaledObject
```

See KEDA upgrade path in `k8s/worker-hpa.yaml` comments for the `ScaledObject` manifest.

---

## Infrastructure Reference

| Resource | Details |
|----------|---------|
| PostgreSQL server | `agentweaver-pg` (RG: `agentweaver-rg`) |
| FQDN | `agentweaver-pg.postgres.database.azure.com` (private, VNet-injected) |
| Private IP | `10.225.0.5` (subnet `aks-postgres` `10.225.0.0/28`) |
| SKU | `Standard_D2ds_v4` / GeneralPurpose / ZoneRedundant HA |
| PG version | 16 |
| Database | `agentweaver` |
| Admin user | `pgadmin` (password in secret `agentweaver-postgres`) |
| Private DNS | `privatelink.postgres.database.azure.com` → A record `agentweaver-pg` → `10.225.0.5` |
| NetworkPolicy | `allow-api-postgres-egress` (API → `10.225.0.0/28:5432`); worker policy HELD |

## A2A mTLS Reference

| Secret | Contents | Expiry |
|--------|----------|--------|
| `agentweaver-a2a-ca` | CA cert | 730 days (2028-06-27) |
| `agentweaver-a2a-server-tls` | Server cert + key + CA | 365 days (2027-06-27) |
| `agentweaver-a2a-client-tls` | Client cert + key + CA | 365 days (2027-06-27) |

To rotate: `bash scripts/aks/gen-a2a-mtls-certs.sh --force`
