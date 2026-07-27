---
"agentweaver": patch
---

Fix GitHub event-triggered workflows never firing for projects created via the "import from GitHub" flow. The webhook receiver matched `project.Origin.SourceRepository` against the delivery payload's `repository.full_name` ("owner/repo"), but `CreateFromGitHubAsync` stores the full HTTPS clone URL, so real deliveries returned 204 and fired nothing. Both sides are now normalized to canonical `owner/repo` before comparison, fixing both the import (URL) and connect (owner/repo) creation paths. Verified end-to-end against staging with a real repo and live webhook delivery.
