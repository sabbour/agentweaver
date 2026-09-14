---
"agentweaver": patch
---

Keep `run_command` input and output collapsed in the transaction trace detail panel. A long build or test command used to expand its full stdout and stderr inline, which pushed the rest of the trace off screen and made a run hard to read. Command text and output now stay collapsed until you open them.
