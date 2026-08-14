# Seraph — History (Summarized)

## 2026-07-29 — Entra-first RBAC and issue #641 hardening
- Tank's Entra-first design fixed the security direction: single-tenant Entra login, Tier-1 app roles, Tier-2 project RBAC, GitHub as linked capability, and hard server-side authorization before linked-token use.
- Reviewed issue #641's event-trigger security design and required boolean-only, ReDoS-safe comment matching, raw-comment redaction, and explicit incremental webhook consent with a safe fallback.
- Wrote the QA matrix for issue #641 covering predicates, webhook resilience, auto-provisioning, natural-language trigger generation, and UI round-trip behavior.
