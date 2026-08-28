---
"agentweaver": patch
---

Fix an authorization bypass in the GitHub capability snapshot lifecycle: a missing/unparseable
`Run.ProjectId` could previously let root, child, retry, and resume launches succeed with zero
GitHub capability snapshots, since only an explicitly blank-origin project may skip capture. Root
construction and inherited child/retry snapshots now both fail closed (`github_capability_unavailable`)
whenever the project id is missing, instead of treating an absent project id as an automatic pass.
