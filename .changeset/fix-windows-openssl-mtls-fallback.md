---
"agentweaver": patch
---

Fix `azure:deploy-from-local` and other provisioning commands failing on Windows when `openssl` isn't on `PATH`: the mTLS certificate generation step now falls back to the `openssl` binary bundled with Git for Windows.
