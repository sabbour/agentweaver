---
title: Project skills reference
---

# Project skills reference

Project skills are exposed through the web UI, REST API, and MCP tools. The web UI uses the same REST
surface as MCP.

## REST routes

All routes are project-scoped under `/api/projects/{id}/skills`.

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
| `GET` | `/api/projects/{id}/skills/assignments` | List all skill-to-agent assignments in the project. |
| `PUT` | `/api/projects/{id}/skills/{skillId}/assignments/{agentName}` | Assign a skill to an agent. |
| `DELETE` | `/api/projects/{id}/skills/{skillId}/assignments/{agentName}` | Unassign a skill from an agent. |
| `POST` | `/api/projects/{id}/skill-defaults/preview` | Preview predefined blueprint defaults for the confirmed team; returns a digest and makes no changes. |
| `POST` | `/api/projects/{id}/skill-defaults/apply` | Atomically apply a matching preview using `blueprint_id` and `digest`; stale previews return `409`. |

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
| `provenance` | `built-in`, `connected-repo-sync`, `repo-import`, `file-upload`, or `manual`. |
| `source_repository` | Connected repo identifier or imported repo URL when applicable. |
| `source_location` | Skill folder path in the source when applicable. |
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
| `skill_import_preview` | Preview candidate skills from GitHub/Git/raw SKILL.md sources. |
| `skill_import` | Import one or more previewed locations from GitHub/Git/raw SKILL.md sources. |
| `skill_defaults_preview` | Preview explicit bundled defaults and receive the apply digest. |
| `skill_defaults_apply` | Atomically apply a matching defaults preview. |
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
