---
"agentweaver": patch
---

Fix a React ref-write-during-render bug in the landing page workflow demo, and remove the hard dependency on a PATH-available `openssl` binary for RSA key/random-byte generation in the Azure provisioning scripts (now uses Node's built-in `crypto` module).
