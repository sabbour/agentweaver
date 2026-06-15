# Plan: MCP Server (Spec 007)

**Branch**: `007-mcp-server`
**Spec**: `specs/007-mcp-server/spec.md`
**Date**: 2026-06-15

## Goal

Replace `apps/Scaffolder.Cli` with `apps/Scaffolder.Mcp` — a .NET 9 stdio MCP server that exposes all Agentweaver operations as structured MCP tool calls. The server is a pure thin proxy: every tool maps 1:1 to an existing REST API endpoint; no business logic lives here.

---

## Architecture Decisions

| Decision | Choice | Rationale |
|---|---|---|
| MCP SDK | `ModelContextProtocol.Server` (Microsoft/official NuGet) | Official .NET SDK, stdio-first, schema validation included |
| Transport | stdio | Standard for Copilot CLI auto-discovery; no HTTP port to manage |
| Credential passing | `SCAFFOLDER_API_URL` + `SCAFFOLDER_API_KEY` env vars only | SC-003: never in tool schemas or arguments |
| HTTP client | `HttpClient` via DI with `IHttpClientFactory` | Standard; one base address, one auth header set at startup |
| SSE → MCP bridge | `run_watch` connects to `/api/runs/{id}/stream`, emits `ProgressToken` notifications | FR-005: live streaming without buffering |
| Error mapping | `ApiError` wrapper that inspects HTTP status, maps to `McpError` | FR-006/SC-007 |
| Project structure | One class per tool group in `Tools/` folder | Clean, one file per group of 5–7 tools |

---

## Tool → Endpoint Mapping

### Projects (7 tools)

| Tool | Method + Path |
|---|---|
| `project_create` | `POST /api/projects` |
| `project_list` | `GET /api/projects` |
| `project_get` | `GET /api/projects/{id}` |
| `project_rename` | `PATCH /api/projects/{id}` |
| `project_relink` | `POST /api/projects/{id}/relink` |
| `project_delete` | `DELETE /api/projects/{id}` |
| `project_configure` | `PUT /api/projects/{id}/provider-settings` |

### Runs (6 tools)

| Tool | Method + Path |
|---|---|
| `run_submit` | `POST /api/projects/{id}/runs` |
| `run_status` | `GET /api/runs/{id}` |
| `run_watch` | `GET /api/runs/{id}/stream` (SSE → progress notifications) |
| `run_review` | `POST /api/runs/{id}/review` |
| `run_show_artifacts` | `GET /api/runs/{id}/files` |
| `run_get_file` | `GET /api/runs/{id}/files/{path}` |

### Team (5 tools)

| Tool | Method + Path |
|---|---|
| `team_get` | `GET /api/projects/{id}/team` |
| `team_cast` | `POST /api/projects/{id}/casting/proposals` → `POST .../confirm` |
| `team_member_add` | `POST /api/projects/{id}/team/members` |
| `team_member_retire` | `DELETE /api/projects/{id}/team/members/{name}` |
| `team_member_get_charter` | `GET /api/projects/{id}/team/members/{name}/charter` |

### GitHub Auth (3 tools)

| Tool | Method + Path |
|---|---|
| `github_signin` | `POST /api/auth/github/device` then poll `POST /api/auth/github/poll` |
| `github_signout` | `POST /api/auth/github/sign-out` |
| `github_status` | `GET /api/auth/github` |

### Sandbox Policy (2 tools)

| Tool | Method + Path |
|---|---|
| `sandbox_policy_get` | `GET /api/sandbox-policy` |
| `sandbox_policy_set` | `PUT /api/sandbox-policy` |

### Catalog (2 tools)

| Tool | Method + Path |
|---|---|
| `catalog_list_roles` | `GET /api/catalog/roles` |
| `catalog_list_scenarios` | `GET /api/casting/templates` |

---

## Project Layout

```
apps/Scaffolder.Mcp/
  Scaffolder.Mcp.csproj          .NET 9 console, OutputType=Exe
  Program.cs                     startup: read env, build IHost, start MCP server
  ScaffolderApiClient.cs         HttpClient wrapper; sets base URL + Bearer header; maps errors
  Tools/
    ProjectTools.cs              7 project tools
    RunTools.cs                  6 run tools (run_watch has SSE loop)
    TeamTools.cs                 5 team tools
    GitHubAuthTools.cs           3 auth tools (device-flow poll loop)
    SandboxPolicyTools.cs        2 policy tools
    CatalogTools.cs              2 catalog tools
```

---

## Implementation Phases

### Phase 1 — Project scaffold

1. Create `apps/Scaffolder.Mcp/Scaffolder.Mcp.csproj`
   - `OutputType=Exe`, `TargetFramework=net10.0` (same as CLI)
   - NuGet refs: `ModelContextProtocol.Server`, `Microsoft.Extensions.Hosting`
   - No project references (pure HTTP proxy — no domain/squad coupling)
2. Add to `scaffolders.sln`
3. `Program.cs`: read `SCAFFOLDER_API_URL` (default `http://localhost:5000`) and `SCAFFOLDER_API_KEY`; fail fast with clear message if key is missing; build `IHost` with `McpServerOptions`, register tool classes, start stdio transport

### Phase 2 — API client + error mapping

1. `ScaffolderApiClient.cs`
   - Wraps `HttpClient` with `BaseAddress` + `Authorization: Bearer {key}` default header
   - `GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync` helpers
   - All return `ApiResult<T>` (success + deserialized body, or `ApiError`)
   - `ApiError` carries HTTP status code + API error message; mapped to `McpException` with human-readable text (FR-006)
   - Separate `GetStreamAsync` for SSE that returns `IAsyncEnumerable<string>` of raw event lines

### Phase 3 — Project tools

`Tools/ProjectTools.cs` — implement all 7 project tools as `[McpTool]` methods on a class registered via DI. Each is a thin call to `ScaffolderApiClient`.

### Phase 4 — Run tools (including `run_watch`)

`Tools/RunTools.cs` — 6 tools. `run_watch` is the most complex:
- Calls `GET /api/runs/{id}/stream` as SSE
- For each `agent.message` event: emit MCP progress notification with the message text
- For each `tool.call` / `tool.result` event: emit summary progress notification
- When `done` SSE event received or stream closes: call `GET /api/runs/{id}` and return the final `RunDto` as the tool result

### Phase 5 — Team tools

`Tools/TeamTools.cs` — 5 tools. `team_cast` accepts `confirm: bool` parameter; if true, chains proposal → confirm in one call; if false, returns the proposal ID for a separate confirm call.

### Phase 6 — GitHub Auth tools

`Tools/GitHubAuthTools.cs` — 3 tools. `github_signin`:
- `POST /api/auth/github/device` → returns user code + verification URL
- Returns these to the AI client immediately as text (so the user can open the browser)
- Polls `POST /api/auth/github/poll` every 5s up to 120s, emitting progress notifications ("Waiting for browser login...")
- On success, returns confirmation; on timeout, returns an error

### Phase 7 — Sandbox policy + catalog tools

`Tools/SandboxPolicyTools.cs` and `Tools/CatalogTools.cs` — straightforward GET/PUT proxies.

### Phase 8 — Registration + cleanup + docs

1. Update `.mcp.json` — add `agentweaver` server entry:
   ```json
   "agentweaver": {
     "command": "dotnet",
     "args": ["run", "--project", "apps/Scaffolder.Mcp", "--no-build"],
     "env": {
       "SCAFFOLDER_API_URL": "http://localhost:5000",
       "SCAFFOLDER_API_KEY": ""
     }
   }
   ```
2. Remove `apps/Scaffolder.Cli` from `scaffolders.sln` and delete the directory
3. Create `docs/reference/mcp.md` — one section per tool group; describes each tool's parameters and return shape
4. Update `docs/guide/getting-started.md` — replace CLI setup steps with MCP server auto-discovery note

---

## Success Verification

For each phase, build passes (`dotnet build`) before committing. Final verification:

1. `dotnet run --project apps/Scaffolder.Mcp` starts without error when `SCAFFOLDER_API_KEY` is set
2. `dotnet run --project apps/Scaffolder.Mcp` exits with clear message when `SCAFFOLDER_API_KEY` is unset
3. With the API running locally, an MCP client can call `project_list` and receive structured output
4. `run_watch` produces progress notifications while a run is in-progress
5. `dotnet build` succeeds with no errors and `apps/Scaffolder.Cli` is gone from the solution

---

## Out of Scope

- Any new API endpoints — spec explicitly states MCP is a thin proxy only
- MCP server authentication (clients are trusted at the stdio boundary)
- HTTP transport variant — stdio only for this feature
- Test project for `Scaffolder.Mcp` — integration tests require a live API; unit testing thin proxies has low ROI; covered by acceptance scenarios in the spec
