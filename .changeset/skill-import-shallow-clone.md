---
"agentweaver": patch
---

fix(skills): shallow-clone GitHub skill repositories (depth=1) for faster import; guard against NullReferenceException when repo.Head is detached.
