---
"Agentweaver.FoundationProbe": patch
---

Accept the approved `agentweaver-kv-v1` Key Vault name as a collision fallback
when validating the Foundation Probe target for the `agentweaver` resource
group. Require the Key Vault resource ID and vault URI to match the same
approved name; retain the original `agentweaver-kv` target and reject other
names or mismatched pairs.
