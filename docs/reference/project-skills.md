---
title: Project skills reference
---

# Project skills reference

See Catalog acquisition, idempotent content and progressive disclosure for the shared visual model.

Project skills are exposed through the web UI, REST API, and MCP tools. The web UI uses the same REST
surface as MCP.

## REST routes

Catalog and assignment routes use `/api/projects/{id}/skills`; defaults use `/api/projects/{id}/skill-defaults`, project marketplaces use `/api/projects/{id}/skill-marketplaces`, and curated discovery uses `/api/skill-marketplaces`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/projects/{id}/skills` | List catalog skills with status, provenance, source, content hash, resources count, and assigned agents. |
| `GET` | `/api/projects/{id}/skills/{skillId}` | Get one skill, including instructions and bundled text resources. |
| `POST` | `/api/projects/{id}/skills` | Create or update a manual skill from `name`, optional `displayName`, `description`, and required `instructions`. |
| `DELETE` | `/api/projects/{id}/skills/{skillId}` | Delete a skill and its assignments. |
| `POST` | `/api/projects/{id}/skills/generate` | Generate an unsaved skill draft server-side from `description` or `prompt`. |
| `POST` | `/api/projects/{id}/skills/sync` | Discover and sync skills from the connected repository. |
| `POST` | `/api/projects/{id}/skills/import/preview` | Preview candidate skills from `owner/repo`, a `https://github.com` repo/tree/blob URL, or a raw `https://raw.githubusercontent.com` SKILL.md URL. |
| `POST` | `/api/projects/{id}/skills/import` | Import selected candidates from `owner/repo`, a `https://github.com` repo/tree/blob URL, or a raw `https://raw.githubusercontent.com` SKILL.md URL. |
| `POST` | `/api/projects/{id}/skills/upload` | Upload files, folders, or a `.zip` archive as skill content. |
| `GET` | `/api/skill-marketplaces` | List enabled administrator-curated marketplaces. |
| `POST` | `/api/projects/{id}/skill-marketplaces/{marketplace}/browse` | Browse or search a curated marketplace without changing the catalog. |
| `POST` | `/api/projects/{id}/skill-marketplaces/{marketplace}/import` | Import selected marketplace candidates through the normal repository-import pipeline. |
| `GET` | `/api/projects/{id}/skills/assignments` | List all skill-to-agent assignments in the project. |
| `PUT` | `/api/projects/{id}/skills/{skillId}/assignments/{agentName}` | Assign a skill to an agent. |
| `DELETE` | `/api/projects/{id}/skills/{skillId}/assignments/{agentName}` | Unassign a skill from an agent. |

### Import source restrictions

`import` and `import/preview` parse the supplied source through an allow-list before any clone or
fetch. Only two hosts are accepted: `github.com` (HTTPS only, default port, no `userinfo`) and
`raw.githubusercontent.com` (which must point directly at a `SKILL.md`). The `owner/repo` shorthand
maps to the canonical `https://github.com/{owner}/{repo}.git` clone URL. Any other scheme (`http`,
`git`, `ssh`, `file`), a non-default port, embedded credentials, or a different host is rejected with
a `400` and a message such as *"Unsupported skill source host … Only github.com and
raw.githubusercontent.com are allowed."* This is an SSRF guard, not a convenience limit.

## DTOs

### Skill

| Field | Meaning |
| --- | --- |
| `id` | Project-local skill id. |
| `name` | `SKILL.md` frontmatter name. Unique per project. |
| `description` | Short description shown in the catalog and prompt metadata. |
| `provenance` | `built-in`, `connected-repo-sync`, `repo-import`, `file-upload`, `manual`, or `marketplace`. |
| `source_repository` | Connected repo identifier or imported repo URL when applicable. |
| `source_location` | Skill folder path in the source when applicable. |
| `marketplace_name` | Curated marketplace identity when provenance is `marketplace`. |
| `status` | `active`, `missing`, or `malformed`. |
| `content_hash` | Stable hash used for idempotent sync/import/upload. |
| `resource_count` | Number of bundled text resources. |
| `assigned_agents` | Agent names currently assigned to the skill. |
| `created_at`, `updated_at` | Catalog timestamps. |

`GET /skills/{skillId}` also returns `instructions` and `resources[]` with
`relative_path` and `content`.

### Acquisition result

Create, sync, import, and upload return:

| Field | Meaning |
| --- | --- |
| `results[]` | Per-candidate outcome with `location`, `name`, `kind`, `skill_id`, and validation `errors`. |
| `marked_missing[]` | Connected-repo skills marked missing because their source location disappeared. |

## MCP tools

The MCP skill tools mirror the REST surface except multipart upload, which is web-only:

| Tool | Purpose |
| --- | --- |
| `skill_list` | List catalog skills with assignments and status. |
| `skill_get` | Fetch a full skill including `SKILL.md` instructions and resources. |
| `skill_delete` | Delete a catalog skill and assignments. |
| `skill_create` | Create or update a manual skill. |
| `skill_generate` | Generate an unsaved skill draft server-side. |
| `skill_sync` | Sync recognized skill directories from the connected repository. |
| `skill_import_preview` | Preview candidate skills from `owner/repo`, HTTPS GitHub repository/tree/blob URLs, or HTTPS `raw.githubusercontent.com` URLs pointing directly to `SKILL.md`. |
| `skill_import` | Import one or more previewed locations from `owner/repo`, HTTPS GitHub repository/tree/blob URLs, or HTTPS `raw.githubusercontent.com` URLs pointing directly to `SKILL.md`. |
| `skill_assignments_list` | List all project assignments. |
| `skill_assign` | Assign a skill to an agent. |
| `skill_unassign` | Remove an assignment. |

See the generated [MCP tool index](./mcp-tools.md) for the full authoritative tool list.

## Source

| Concern | Source |
| --- | --- |
| REST route map and status codes | `apps/Agentweaver.Api/Endpoints/SkillEndpoints.cs:15` |
| Web client route wrappers and multipart upload | `apps/web/src/api/client.ts:373` |
| TypeScript DTOs | `apps/web/src/api/types.ts:1306` |
| MCP skill tools | `apps/Agentweaver.Mcp/Tools/SkillTools.cs:40` |

## Curated marketplaces

Administrators configure trusted marketplace definitions in the API `SkillMarketplaces:Definitions` configuration section. Each definition has a name, GitHub repository, optional subpath/layout note, branch, and `enabled` flag. Disabled or removed definitions disappear from browse results but never delete skills already imported from them. The built-in configuration includes GitHub Awesome Copilot (`github/awesome-copilot` at `skills`) and Azure Skills (`microsoft/skills` at `.github/plugins/azure-skills/skills`). Browse failures return an unavailable-source response and do not modify the project catalog.

## Defaults and project marketplace sources

| REST action | Contract | MCP tool |
|---|---|---|
| `POST /api/projects/{id}/skill-defaults/preview` | Preview bundled role defaults for a confirmed team/predefined blueprint; return a state-bound digest without writes. | `skill_defaults_preview` |
| `POST /api/projects/{id}/skill-defaults/apply` | Atomically apply the matching preview; stale digest returns `409`. | `skill_defaults_apply` |
| `GET /api/projects/{id}/skill-marketplaces` | Curated and project-added sources. | `skill_marketplace_sources_list` |
| `POST /api/projects/{id}/skill-marketplaces/sources` | Add a project source. | `skill_marketplace_source_add` |
| `DELETE /api/projects/{id}/skill-marketplaces/sources/{name}` | Remove a project-added source, not a curated definition. | `skill_marketplace_source_remove` |

`skill_marketplaces_list`, `skill_marketplace_browse`, and `skill_marketplace_import` discover, preview, and import marketplace candidates. Adding a source does not import or assign skills. Curated names win collisions; project sources cannot shadow them. The import parser's strict HTTPS host rules are distinct from marketplace-source parsing.

<details id="diagram-context-project-skills-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Skills: catalog to safe delivery</td></tr>
<tr><td>takeaway</td><td>Only successful shared-filesystem writes produce pointers; all other delivery is inline.</td></tr>
<tr><td>group-title-0</td><td>ACQUIRE · VALIDATE · ASSIGN</td></tr>
<tr><td>group-title-1</td><td>DELIVERY BRANCHES · EXECUTION</td></tr>
<tr><td>Skill sources</td><td>Skill sources</td></tr>
<tr><td>Skill sources</td><td>Checkout · repo · marketplace</td></tr>
<tr><td>Skill sources</td><td>Upload and manual entry are also inputs</td></tr>
<tr><td>Skill sources</td><td>SkillCatalogService.cs:335-372</td></tr>
<tr><td>Active assignments</td><td>Active assignments</td></tr>
<tr><td>Active assignments</td><td>Look up skills for this agent</td></tr>
<tr><td>Active assignments</td><td>No active assignments → no skill block</td></tr>
<tr><td>Active assignments</td><td>SkillPromptComposer.cs:45-81</td></tr>
<tr><td>Project skill catalog</td><td>Project skill catalog</td></tr>
<tr><td>Project skill catalog</td><td>Parse / validate / content-hash upsert</td></tr>
<tr><td>Project skill catalog</td><td>Missing / malformed sources are tracked</td></tr>
<tr><td>Project skill catalog</td><td>SkillCatalogService:1045-1156</td></tr>
<tr><td>Shared worktree available</td><td>Shared worktree available</td></tr>
<tr><td>Shared worktree available</td><td>Best-effort stale-folder cleanup</td></tr>
<tr><td>Shared worktree available</td><td>Attempt each assigned skill materialization</td></tr>
<tr><td>Shared worktree available</td><td>SkillPromptComposer.cs:63-106</td></tr>
<tr><td>Explicit agent assignment</td><td>Explicit agent assignment</td></tr>
<tr><td>Explicit agent assignment</td><td>Catalog skills bind to agents</td></tr>
<tr><td>Explicit agent assignment</td><td>Delivery selects active assigned entries</td></tr>
<tr><td>Explicit agent assignment</td><td>SkillPromptComposer.cs:49-54</td></tr>
<tr><td>Successful write only</td><td>Successful write only</td></tr>
<tr><td>Successful write only</td><td>Prompt metadata + SKILL.md path</td></tr>
<tr><td>Successful write only</td><td>Agent reads relevant skill instructions</td></tr>
<tr><td>Successful write only</td><td>SkillPromptComposer.cs:91-98,145</td></tr>
<tr><td>Defaults preview / apply</td><td>Defaults preview / apply</td></tr>
<tr><td>Defaults preview / apply</td><td>Confirmed team + preview digest</td></tr>
<tr><td>Defaults preview / apply</td><td>Apply recomputes; stale digest rejects</td></tr>
<tr><td>Defaults preview / apply</td><td>SkillDefaultsService.cs:231-250</td></tr>
<tr><td>Inline full instructions</td><td>Inline full instructions</td></tr>
<tr><td>Inline full instructions</td><td>Pod-local / unavailable filesystem</td></tr>
<tr><td>Inline full instructions</td><td>Also used for each failed materialization</td></tr>
<tr><td>Inline full instructions</td><td>SkillPromptComposer.cs:99-160</td></tr>
<tr><td>Skill sources</td><td>validate</td></tr>
<tr><td>Project skill catalog</td><td>assign</td></tr>
<tr><td>Explicit agent assignment</td><td>lookup</td></tr>
<tr><td>Active assignments</td><td>shared FS</td></tr>
<tr><td>Shared worktree available</td><td>write succeeds</td></tr>
<tr><td>Active assignments</td><td>no FS</td></tr>
<tr><td>Shared worktree available</td><td>write fail</td></tr>
<tr><td>scope</td><td>Defaults preview has no side effects; explicit apply is digest-checked. Cleanup is best-effort.</td></tr>
<tr><td>groups</td><td>ACQUIRE · VALIDATE · ASSIGN; DELIVERY BRANCHES · EXECUTION</td></tr>
</tbody></table>
</details>
