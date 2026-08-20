---
"agentweaver": patch
---

fix: null-guard legacy history.json deserialization

Guards against NullReferenceException when creating projects from repositories
that contain legacy history.json files with null entries, preventing a crash
on project creation for affected repositories.
