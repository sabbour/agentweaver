# Agent definition — Reference

Terse reference for **agent definition generation & per-project materialization**: how the GitHub Copilot
agent file `.github/agents/agentweaver.agent.md` is generated from the MCP tool source, embedded into the
API, and written into every new project.

**No HTTP API.** This feature adds **no new routes, DTOs, or config keys.** It is a build-time generator
plus a creation-time side effect inside `ProjectService`. The only "interface" is the `gen-docs.mjs` CLI and
the materialized file on disk.

## Generated files

One generator, `scripts/gen-docs.mjs`, produces three targets from the single source of truth
`apps/Agentweaver.Mcp/Tools/*.cs` (the `[McpServerTool]` / `[Description]` attributes):

| Target | What is generated | Source of truth |
|---|---|---|
| `docs/reference/mcp-tools.md` | The full MCP tool index (every tool + one-line description). | `apps/Agentweaver.Mcp/Tools/*.cs` |
| `.github/agents/agentweaver.agent.md` | **Only** the Tool map block between the markers (see below). All other prose is hand-written and preserved. | Same — Tool map only |
| `apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md` | A **byte-identical copy** of the agent file above, embedded into the API. | The agent file above |

### The marker block

Inside `.github/agents/agentweaver.agent.md`, the generated region is delimited exactly by:

```
<!-- BEGIN GENERATED:tool-map -->
... generated Tool map grouped by category ...
<!-- END GENERATED:tool-map -->
```

The generator replaces only the bytes between these markers (`applyToolMapBlock`, `gen-docs.mjs:192`) and
reads the rest of the file back as the template, so frontmatter, mental model, operating principles, and
playbooks stay verbatim. Editing the file outside the markers is safe; editing inside them is overwritten on
the next regenerate.

## Regenerate

```bash
node scripts/gen-docs.mjs          # rewrite all three generated targets
node scripts/gen-docs.mjs --check  # exit 1 if any committed target is stale (CI)
```

The script is dependency-free Node (no `npm install`). Run it after adding, renaming, or re-describing any
MCP tool, then commit the changed files.

## CI gate

The `docs-drift` workflow runs the generator in `--check` mode on every pull request. `--check` re-derives
all three targets and compares them to what is committed; a stale file prints `DRIFT: ...` and the job exits
non-zero (`.github/workflows/docs-drift.yml:37`). This makes a drifted agent file a hard build failure rather
than a silent inconsistency.

```
OK: docs/reference/mcp-tools.md is in sync.
OK: .github/agents/agentweaver.agent.md is in sync.
OK: apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md is in sync.
```

## Materialization behavior

On project creation, `ProjectService.TryMaterializeAgentDefinition` (`ProjectService.cs:485`) writes the
embedded template into the new project. It is called from both `CreateBlankAsync` (`ProjectService.cs:90`)
and `CreateFromGitHubAsync` (`ProjectService.cs:183`).

| Property | Behavior |
|---|---|
| **Destination** | `{project.WorkingDirectory}/.github/agents/agentweaver.agent.md` (`AgentDefinitionTemplate.RelativeFilePath`). |
| **Content** | The embedded `EmbeddedResource` template, byte-identical to the committed `.github` file. |
| **Idempotent / non-clobbering** | Skips the write and returns `false` when the file already exists — user edits and repo-shipped agent files are never overwritten (`AgentDefinitionTemplate.cs:56`). |
| **Directory creation** | Creates the `.github/agents/` directories as needed. |
| **Best-effort** | Catches `IOException` / `UnauthorizedAccessException` / `SecurityException`, logs a warning, and returns; **never fails project creation** (`AgentDefinitionTemplate.cs:63`). |
| **Both create paths** | Applies to blank projects and GitHub-cloned projects alike. |

## Where the file lands in a project

```
{project working directory}/
└── .github/
    └── agents/
        └── agentweaver.agent.md   ← materialized here, once, if absent
```

GitHub Copilot discovers agent files under `.github/agents/`, so the materialized file becomes a selectable
agent ("Agentweaver Driver") for that project with no further setup.

## Tests

| Guard | Test |
|---|---|
| Embedded template == committed `.github` file | `AgentDefinitionTemplateTests.EmbeddedTemplate_MatchesCommittedRepoFile` |
| `TryMaterialize` writes the file | `AgentDefinitionTemplateTests.TryMaterialize_WritesFileIntoProjectDir` |
| Idempotent + non-clobbering | `AgentDefinitionTemplateTests.TryMaterialize_IsIdempotent_AndDoesNotClobberUserEdits` |
| New project contains the file | `ProjectServiceCreateTests` (PC-11) |

## See also

- [Agent definition — Deep Dive](../deep-dive/agent-definition.md) — the full generation + materialization flow.
- [Agent definition — User Guide](../experience/agent-definition.md) — the file from a user's perspective.
- [MCP tool index](./mcp-tools.md) — the generated list of all `agentweaver-*` tools.
- [MCP server reference](./mcp.md) — per-tool parameter reference.
