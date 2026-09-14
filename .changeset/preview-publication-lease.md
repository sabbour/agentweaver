---
"agentweaver": patch
---

Stop a run from cancelling its own live preview. Publishing a preview takes 90-120 s (port-forward, DNS convergence, health probe) and the final `sandbox.preview_ready` batch commits only while the run row is still active. An agent that finished its work inside that window cancelled the publication through the run's own completion token, and the preview process was torn down as `preview_not_published`.

Publication now claims a short, database-backed lease on the run. While the lease is held, every terminal transition defers until the publication commits or the lease expires, so `preview_ready` always precedes the terminal event. The lease is bounded twice — it carries its own expiry, and a deferral cap releases it — so a replica that crashes mid-publication cannot park a run. The lease is released around the preview approval wait, so a run is never held open for an operator.
