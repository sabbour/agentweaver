---
"agentweaver": patch
---

Let a recovered run publish a healthy preview without waiting out an obsolete API
request's Gateway-convergence window. The previous lifecycle loses its publication
lease on renewal, while the replacement can safely publish one ready outcome.
