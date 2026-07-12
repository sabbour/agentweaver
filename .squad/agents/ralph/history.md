
📌 Team update (2026-07-10T05:55:00-07:00): Public HTTP staging baseline found #196 approval regression and #207 OOM/finalizer defect; fresh projects were cleaned and the mainline API checkpoint preserved. Resume north-star validation after bounded defect work. — recorded by Scribe

## 2026-07-10T16:54:50Z — #207 frozen evidence inventory
- Facts-only inventory posted and verified: https://github.com/sabbour/agentweaver/issues/207#issuecomment-4937601132 (21,674 chars; author `sabbour`; checklist/checkpoint markers verified).
- Earliest violated invariant: no stable durable finalizer identity for parent + semantic cause + generation; completed-only Scribe dedup permits replay/concurrent duplicates.
- Distinct pod-per-run isolation defect filed and verified: https://github.com/sabbour/agentweaver/issues/209.
- Repository evidence revision: `5d22febc`; no source edits/build/deploy/implementation.
- Staging evidence: `v0.9.19-rc1`, API desired 2/ready 1, one pod CrashLoopBackOff with 65 restarts and last `OOMKilled`; AgentHost warm pool 2/2; PostgreSQL; pod-per-run.
- Preserved north-star checkpoint: deleted project `805e6ee4-54ee-46c4-9224-297bc1dcad5e`, coordinator `f3f7cff1-026c-4e83-bac3-f238d4086fc3`, last parent sequence 885 / child sequence 856; no cleanup obligations remain.
- Next action: after blocker deployment, create a fresh public-API-only project and restart the complete PM-to-preview baseline.
