---
"agentweaver": minor
---

Align the remaining Microsoft Agent Framework packages onto the 1.19.x line:
`Microsoft.Agents.AI.Workflows` 1.11.1 → 1.19.0 and `Microsoft.Agents.AI.A2A`
1.11.1-preview.260625.1 → 1.19.0-preview.260822.1.

This completes the dependency alignment started when `GitHub.Copilot.SDK` was
bumped to 1.0.11 alongside `Microsoft.Agents.AI.GitHub.Copilot` 1.19.0 (the SDK
became strong-named in 1.0.4, so the adapter had to be rebuilt against the
signed assembly). That change moved `Microsoft.Agents.AI.Abstractions` to 1.19.0
while `Workflows` and `A2A` stayed on 1.11.1, leaving the framework split across
two release lines; this brings them back onto a single line.

`Microsoft.Agents.AI.Workflows` moves off the prerelease-adjacent 1.11.1 build
onto stable 1.19.0. The public surface is additive across the range — no types
or members used by Agentweaver were removed — so no source changes were needed.
Transitively this also advances `Microsoft.Agents.AI` to 1.19.0,
`Microsoft.Extensions.AI.Evaluation` to 10.9.0 and
`Microsoft.Extensions.VectorData.Abstractions` to 10.7.0; none of the APIs
dropped in those packages are referenced by Agentweaver.
