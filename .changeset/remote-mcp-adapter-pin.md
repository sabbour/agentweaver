---
"Agentweaver.Abstractions": minor
"Agentweaver.Environment": minor
---

Pin credential-free Streamable HTTP adapter options to each Remote MCP configuration revision,
backfill existing Environment records without changing their configuration digests, reject
malformed nested tool-schema structures before catalog pinning, and retain the original
nine-parameter public configuration constructor.

Add Environment's planned migration path to database maintenance so the service schema and
runtime grants are applied through its separate migration identity.
