---
"agentweaver": patch
---

Fixed a bug (#388) where a reviewer (Build & Test gate, RAI, rubber-duck, or
any steering-driven revision) sending a review/revision request to a target
agent WIPED that agent's run-tree message stream instead of appending to it.
The shared in-place-resume/revision-injection mechanism (used by
`CoordinatorAssemblyService.ExecuteInPlaceSteerAsync`,
`CoordinatorDispatchService.TryInjectSteeringRevisionAsync`, and
`CoordinatorSteeringService`'s recovery path) removed and recreated the
child/coordinator run's `RunStreamStore` entry to clear the completed flag
before resuming, which discarded every event recorded before the review.
`RunStreamStore`/`RunStreamEntry` now expose a `Reopen()` operation that
clears the completed/awaiting-review flags in place while preserving the
recorded history, so the new review/revision turn is appended after the
target agent's prior messages instead of replacing them.
