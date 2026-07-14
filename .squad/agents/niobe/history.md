

## 2026-07-06 v0.9.0 staging wave
- Shipped orientation-aware SpineEdge and centered TB ranks for coordinator graphs; tests passed.
## 2026-07-14T10:15:00-07:00
Established pagination contract (PagedResult<T> envelope) across list endpoints; fixed int32 overflow in Paging.Of() found by reviewer. Needs peer review (breaking change) before merge; dozer is downstream frontend consumer.

