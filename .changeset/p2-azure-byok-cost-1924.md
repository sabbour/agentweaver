---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
---

Price trusted Azure BYOK usage from an explicitly configured, versioned `EventsAndSessions:Cost:Azure` rate card. Other BYOK providers stay `Unpriced`, and a changed rate card never reprices earlier rows.
Add `UsageSpendEvidenceContract` and the internal `usage/spend-evidence` route, which return run and period totals, dispatch witnesses, and a monotonic accounting cursor from one ledger snapshot.
Add `AzureProvisionedThroughputAllocator`, a report-only allocation of a fixed provisioned-throughput charge by token share with exact largest-remainder rounding.
