---
"agentweaver": patch
---

Replace the coordinator's keyword and file-extension heuristics for Build & Test gate
applicability with a small, tool-less LLM classification that fails safely by retaining
the gate when the model is unavailable, times out, or returns an ambiguous response.
