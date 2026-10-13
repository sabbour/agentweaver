---
---

Issue #1860 adds controlled API, MCP, and UI journey adapters and a fail-closed
CLI preflight. Live P1 journeys remain blocked until the admitted services
provide safe same-invocation fixture setup, dispatch, and cleanup. This is
validation tooling only; it does not change a versioned product component or
perform live acceptance.
