---
"Agentweaver.Identity.Broker": patch
---

Attach the exact-host Broker and Portal routes to one wildcard-domain HTTPS
listener so HTTP/2 connection coalescing does not return a route-not-found
response during Identity Broker sign-in.
