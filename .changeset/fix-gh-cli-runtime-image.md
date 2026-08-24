---
"agentweaver": patch
---

Install GitHub CLI in the API runtime image and split the `github_cli` health diagnostic into separate installation and authentication checks so the health endpoint reports each concern independently.