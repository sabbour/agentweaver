---
"agentweaver": patch
---

Stop repeating successful startup recovery sweeps while an API or worker replica
is running. New coordinator runs are no longer mistaken for interrupted runs
before they have created their first checkpoint.
