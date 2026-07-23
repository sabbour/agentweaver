---
"agentweaver": minor
---

Add LLM-assisted skill marketplace catalog parsing (step-1b): a project can now add a curated marketplace source by GitHub repo URL. A new catalog indexer auto-detects skills from the repo tree (deterministic `SKILL.md` heuristic with a bounded, fail-closed Copilot classifier fallback), caches the parsed index per repo revision, and paginates browse from it (anonymous-first, page-lazy descriptions). Project sources are persisted per project (SQLite + Postgres) with add/list/remove endpoints; existing config marketplaces are unchanged.
