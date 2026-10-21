---
---

Run ordered Guardrails checks on model-turn user input during runtime action admission, with Azure AI Content Safety Prompt Shields as the supported default check.
Send findings to AGT for the final decision, fail closed on reordered, stale, unavailable, or timed-out checks, and record immutable evidence in Orchestrator migration 028.
Send model-turn text only after the Orchestrator returns HTTP 428 for a run that selects Guardrails. Tool-approval and concurrency behavior are unchanged.
