---
"Agentweaver.Identity.Broker": patch
---

Apply the canonical post-migration runtime grants through the guarded operator
bootstrap before Broker installation. Preserve read-only verification and the
separate migration role.

Correct native Kubernetes alias comparison, the Probe PostgreSQL database
boundary, and exact-byte verification of single-image registry descriptors.
Partial publication receipts retain the actual pushed digest and never permit
an automatic retry.
