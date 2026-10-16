---
"agentweaver": patch
---

Pin the patched `System.Security.Cryptography.Pkcs` 10.0.13 in the API so the transitive SqlClient dependency no longer resolves the vulnerable 9.0.4 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).
