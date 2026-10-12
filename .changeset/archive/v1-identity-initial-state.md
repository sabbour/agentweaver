---
"Agentweaver.Identity.Broker": patch
---

Provide default-off, create-only setup for initial signing material, retained
key-ring storage, and an explicit public acceptance-client configuration.
Preserve existing application state and reject incompatible partial inputs.
Align only the existing P0 route hostname fields to the native managed domain;
do not change Gateway placement or claim public readiness from configuration.
