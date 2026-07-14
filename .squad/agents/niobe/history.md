

## 2026-07-06 v0.9.0 staging wave
- Shipped orientation-aware SpineEdge and centered TB ranks for coordinator graphs; tests passed.
## 2026-07-14T10:15:00-07:00
Established pagination contract (PagedResult<T> envelope) across list endpoints; fixed int32 overflow in Paging.Of() found by reviewer. Needs peer review (breaking change) before merge; dozer is downstream frontend consumer.

## 2026-07-14T15:15:00Z — Coordinator correctness + paging wave
Niobe's tri-state declared-output parser, #200 span-parenting hardening, and MemoriesPage pagination/live-checklist work all fed the v0.9.50-rc1 batch. The broader HPA/KEDA rollout stays separate, and #316 now tracks the still-missing UI for per-agent memory/sessions.
