---
"agentweaver": patch
---

Fix sandbox preview creation for Python apps ("app.py"/"main.py" entrypoints):
the resolved preview command invoked a bare `python` binary, which does not
exist on the agent sandbox image (only `python3` is installed). Every preview
attempt for a Python-only app failed with `process_exited: exitCode=127
... python: not found`. The resolver now emits `python3 ...` for both
entrypoints.
