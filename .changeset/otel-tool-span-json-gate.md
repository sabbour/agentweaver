---
"agentweaver": patch
---

Gate `gen_ai.tool.call.result` OTel span tag on JSON-shaped output to prevent plain-text file contents and shell output from leaking into App Insights traces. JSON objects and arrays are tagged (and redacted via the existing `RedactJsonStringIfApplicable` pipeline); all other result formats are silently omitted.
