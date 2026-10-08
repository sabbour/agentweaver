---
"Agentweaver.Abstractions": minor
"Agentweaver.Secrets.AzureKeyVault": minor
---

Add a trusted credential-version writer contract and its native Azure Key Vault adapter.
The adapter creates one version, reads that exact version, and validates its value and metadata.
It disables content logging and automatic retries, returns only an opaque reference, and does not perform owner-state CAS.
The host still supplies current authorization and explicit write permission.
Existing P0 read-only role assignments remain unchanged.
