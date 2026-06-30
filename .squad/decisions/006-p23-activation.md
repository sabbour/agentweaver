# Decision: spec-018 P2/P3 Activation — 2026-06-27

**Status**: ACTIVATED  
**By**: Link (asabbour@microsoft.com)  
**Cluster**: agentweaver-aks-2 / namespace agentweaver

## Summary

P2 (Postgres) and P3 (worker + HPA) are now live.

## Activation details

| Step | Result |
|------|--------|
| C1 Postgres connectivity | PASSED — PostgreSQL 16.14 reachable from cluster |
| C2 EF migrations | PASSED — 20260627000000_InitialPostgres applied |
| C3 Data migration | PASSED — Projects 3/3, Runs 31/31, BacklogTasks 19/19 |
| C4 Flip to Postgres | PASSED — api-deployment.yaml Database__Provider=Postgres |
| C5 Scale API | PASSED — 2/2 replicas, RollingUpdate |
| C6 Worker + HPA | PASSED — 1/1 worker Running, /healthz OK |

## Live topology

- **API**: 2 replicas (RollingUpdate), image 92e4d74c-fix2, Postgres primary
- **Worker**: 1 replica (min 1 / max 3 via HPA), image 92e4d74c-fix2, App__Role=worker
- **Database**: agentweaver-pg.postgres.database.azure.com (private VNet, Standard_D2ds_v4)
- **Sandbox mode**: in-api (pod-per-run held for P1.5)

## Code fixes discovered during activation (fix1, fix2)

1. Dockerfile missing COPY for Agentweaver.Api.Data + Agentweaver.Api.Migrations.Postgres
2. BacklogDecomposeEndpoints.cs used concrete SqliteBacklogTaskStore instead of IBacklogTaskStore  
3. IBacklogTaskStore missing GetExistingTitlesFromSourceAsync method
4. Widespread use of SqliteRunStore as concrete DI parameter (14 files) — all changed to IRunStore
5. api-deployment.yaml and worker-deployment.yaml had HOME=/data (not writable without PVC) — changed to HOME=/tmp
6. worker-deployment.yaml probe paths were /api/ping+/api/health (web tier) — worker registers /healthz+/readyz

## Held

- pod-per-run (P1.5 gaps)
- Web HPA
- KEDA worker scaler
- Worker dedicated ServiceAccount