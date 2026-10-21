---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
---

Price trusted Azure BYOK usage from an explicitly configured, versioned `EventsAndSessions:Cost:Azure` rate card. Runtime token measurements labelled `tokens` are priced; any other provider unit stays `Unpriced`. Only dispatch-correlated usage that passes `RequireTrustedAzureByok` is priced; admitted native Azure BYOK usage without a dispatch message ID stays `Unpriced`. Other BYOK providers stay `Unpriced`, and a changed rate card never reprices earlier rows.
Add `UsageSpendEvidenceContract` and the internal `usage/spend-evidence` route, which return run and period totals, dispatch witnesses, and a monotonic accounting cursor from one ledger snapshot.
Add `AzureProvisionedThroughputAllocator`, a report-only allocation of a fixed provisioned-throughput charge by token share with exact largest-remainder rounding.
