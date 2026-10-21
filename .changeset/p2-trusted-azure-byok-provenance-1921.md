---
"Agentweaver.Identity": minor
"Agentweaver.EventsAndSessions": patch
---

Add `RuntimeUsageSourceReceiptContract.RequireTrustedAzureByok`, which proves trusted Azure BYOK provenance for one owner-issued usage receipt and returns its dispatch and pinned configuration facts.
Events rejects any receipt that claims Azure BYOK and fails this check, so untrusted usage never enters the ledger.