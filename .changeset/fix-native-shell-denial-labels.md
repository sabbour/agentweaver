---
'agentweaver': patch
---

Fix denied native Copilot shell attempts showing up in the run activity feed as raw shell tools like `bash` instead of the sandboxed `run_command` label, and make repeated native-shell denials within the same run more explicit so the model stops retrying the disabled tool.
