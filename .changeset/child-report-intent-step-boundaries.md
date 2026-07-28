---
'agentweaver': patch
---

Fix child/subtask chat timelines collapsing under a single "Step 1" when the run stream only includes raw `report_intent` tool calls by treating those calls as step boundaries in the frontend timeline builder.
