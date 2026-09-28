---
"agentweaver": patch
---

Present addressed messages at worker turn boundaries with fenced claim renewal and replay-safe logical IDs; add operator send, reply and retry actions. Idle wake remains unavailable until the scheduling/recovery owner exposes durable, budgeted wake admission and a safe same-run turn entry point; accepted messages do not imply a scheduled wake.
