---
"Agentweaver.Gateway": patch
---

Project public Gateway `acceptRunSelection` and `getRunSelection` responses to omit BYOK `credentialReference` and `providerBindingRevision.credentialVersion` while failing closed on malformed owner payloads.
