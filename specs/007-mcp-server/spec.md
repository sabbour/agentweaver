# Feature Specification: MCP Server

**Feature Branch**: `007-mcp-server`

**Created**: 2026-06-15

**Status**: Draft

**Input**: User description: "Replace apps/Scaffolder.Cli with apps/Scaffolder.Mcp — a Model Context Protocol (MCP) server using stdio transport. This allows any MCP-capable AI client (GitHub Copilot CLI, Claude, Cursor, Windsurf, etc.) to interact with Agentweaver natively via structured tool calls instead of shell commands."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - AI Client Manages Projects and Submits Runs (Priority: P1)

An AI developer assistant (Copilot CLI, Claude, Cursor, etc.) uses the MCP server to manage Agentweaver projects and run agentic tasks without executing shell commands. The user instructs the AI client in natural language; the client translates the intent into structured MCP tool calls and the server relays them to the REST API. The user sees live progress as the run executes, then reviews and approves the results.

**Why this priority**: This is the core value proposition of the entire feature — replacing the CLI with a structured, AI-native interface. Without the ability to list projects, submit a run, watch it live, and review the outcome, there is nothing else to ship.

**Independent Test**: Configure an MCP host with the `.mcp.json` file, start the MCP server, and instruct any MCP-capable AI client to: (1) list projects, (2) submit a run against one project, (3) watch it stream live progress, and (4) approve or decline the result — all via tool calls with no shell commands.

**Acceptance Scenarios**:

1. **Given** the MCP server is registered via `.mcp.json` and `SCAFFOLDER_API_KEY` is set, **When** an AI client calls `project_list`, **Then** the tool returns all projects from the API in structured form within a normal response time.
2. **Given** a project exists, **When** an AI client calls `run_submit` with a task description, **Then** the API creates a run and the tool returns the run ID and initial state.
3. **Given** a run is in progress, **When** an AI client calls `run_watch`, **Then** the tool streams live agent messages and tool call events as MCP progress notifications until the run reaches a terminal state, then returns the final run state.
4. **Given** a run has completed, **When** an AI client calls `run_review` with an approval or decline decision, **Then** the API records the decision and the tool confirms the outcome.
5. **Given** a run has been reviewed, **When** an AI client calls `run_show_artifacts` and then `run_get_file` for a specific file, **Then** the tools return the list of changed files and the diff or content of the requested file respectively.

---

### User Story 2 - AI Client Manages Agent Teams (Priority: P2)

An AI developer assistant uses the MCP server to view, modify, and cast agent teams for a project. The user can ask the AI client to add team members, retire existing ones, and generate a casting proposal for a given goal — all through tool calls.

**Why this priority**: Team management is a second-tier capability — the system has value without it during initial adoption, but parity with the CLI requires it.

**Independent Test**: Use an MCP client to call `team_get` on an existing project, verify the roster is returned, then call `team_cast` with a goal description and confirm a casting proposal is returned with roster changes.

**Acceptance Scenarios**:

1. **Given** a project with an existing team, **When** an AI client calls `team_get`, **Then** the tool returns the current team roster.
2. **Given** a project, **When** an AI client calls `team_cast` with a goal, **Then** the tool returns a casting proposal; when the client confirms, the new roster is committed.
3. **Given** a team roster, **When** an AI client calls `team_member_add` with a role, **Then** the new member appears in subsequent `team_get` results.
4. **Given** an existing team member, **When** an AI client calls `team_member_retire`, **Then** the member no longer appears in subsequent `team_get` results.
5. **Given** a team member exists, **When** an AI client calls `team_member_get_charter`, **Then** the tool returns that member's charter document.

---

### User Story 3 - AI Client Handles GitHub Authentication (Priority: P2)

An AI developer assistant uses the MCP server to authenticate with GitHub through the device flow, check auth status, and sign out — all via tool calls, with no credentials embedded in tool schemas.

**Why this priority**: GitHub auth is a prerequisite for creating projects from GitHub repositories. It must exist for full feature parity but can be deferred until after the core run flow works.

**Independent Test**: Call `github_status` to confirm unauthenticated state, then call `github_signin` and follow the device-flow instructions returned by the tool to complete authentication, then call `github_status` again to confirm authenticated state.

**Acceptance Scenarios**:

1. **Given** the user is not signed in, **When** an AI client calls `github_signin`, **Then** the tool initiates the GitHub device flow and returns the user code and verification URL; after the user completes the flow in a browser, the tool polls to completion and returns a success confirmation.
2. **Given** the user is signed in, **When** an AI client calls `github_status`, **Then** the tool returns the authenticated GitHub identity.
3. **Given** the user is signed in, **When** an AI client calls `github_signout`, **Then** the session is ended and subsequent `github_status` calls return unauthenticated.

---

### User Story 4 - MCP Host Auto-Discovers the Server via .mcp.json (Priority: P1)

A developer adds the repository to an MCP-capable host (Copilot CLI, Claude Desktop, Cursor, etc.) and the host automatically discovers and registers the Agentweaver MCP server from the `.mcp.json` file at the repository root, requiring no manual configuration beyond setting the API key environment variable.

**Why this priority**: Auto-discovery is what makes the MCP server self-service and removes all the friction of manual registration. Without it, the server is harder to adopt than the CLI it replaces.

**Independent Test**: Clone the repository, set `SCAFFOLDER_API_KEY`, open the repo in an MCP-capable host, and verify that Agentweaver tools appear in the host's tool list without any manual registration steps.

**Acceptance Scenarios**:

1. **Given** the `.mcp.json` file exists at the repository root with the correct server configuration, **When** an MCP host with auto-discovery support opens the repository, **Then** the Agentweaver tools are automatically registered and available.
2. **Given** `SCAFFOLDER_API_URL` is not set, **When** the MCP server starts, **Then** it defaults to `http://localhost:5000` and operates normally against a local API instance.
3. **Given** `SCAFFOLDER_API_KEY` is not set, **When** the MCP server starts or a tool is called, **Then** the server returns a clear error message indicating the missing key.

---

### User Story 5 - AI Client Manages Sandbox Policies and Catalog (Priority: P3)

An AI developer assistant uses the MCP server to inspect available agent roles, explore casting scenario templates, and configure sandbox policies for repositories — all via tool calls.

**Why this priority**: These are ancillary management capabilities. The system delivers its primary value without them; they are included for full parity with the API.

**Independent Test**: Call `catalog_list_roles` and verify a list of available agent roles is returned. Call `sandbox_policy_get` for a known repository and verify the policy is returned. Call `sandbox_policy_set` to update the policy and verify the change persists.

**Acceptance Scenarios**:

1. **Given** the server is running, **When** an AI client calls `catalog_list_roles`, **Then** the tool returns all available agent roles from the API.
2. **Given** the server is running, **When** an AI client calls `catalog_list_scenarios`, **Then** the tool returns all casting scenario templates.
3. **Given** a repository identifier, **When** an AI client calls `sandbox_policy_get`, **Then** the current sandbox policy for that repository is returned.
4. **Given** a repository identifier and a new policy value, **When** an AI client calls `sandbox_policy_set`, **Then** the policy is updated and subsequent `sandbox_policy_get` calls reflect the change.

---

### User Story 6 - Agentweaver-Aware Squad Coordinator Initialized into Managed Repos (Priority: P2)

When a project team is confirmed through the casting wizard, Agentweaver writes a custom Squad coordinator agent definition (`squad-agentweaver.agent.md`) into the managed repository's `.github/agents/` directory. This coordinator knows how to dispatch work through the Agentweaver MCP server — submitting runs, watching live progress, and routing review decisions — instead of using the generic `task`/`runSubagent` dispatch mechanism. Developers who open the managed repo in an MCP-capable host (Copilot CLI, Claude, Cursor) get an AI team coordinator that is natively wired into Agentweaver's run lifecycle.

**Why this priority**: Without this, developers in managed repos must manually wire up run dispatch. With it, saying "Ripley, refactor the auth module" automatically submits a run against Ripley's charter, streams the result live, and asks for review — no extra configuration.

**Independent Test**: Cast a team for a project via the wizard. Open the managed repository in an MCP-capable host. Send a message targeting a team member (e.g., "Ripley, add error handling to the export function"). Verify the coordinator resolves the project ID dynamically, calls `run_submit` with `agent_name: "ripley"`, surfaces live progress via `run_watch`, and prompts for `run_review` when the run reaches `awaiting_review`.

**Acceptance Scenarios**:

1. **Given** a team has been cast for a project, **When** the cast is confirmed, **Then** `.github/agents/squad-agentweaver.agent.md` is written to the managed repository.
2. **Given** the coordinator is active in a repo, **When** the session starts, **Then** the coordinator calls `project_list` and matches the current working directory against each project's repository path to resolve its project ID — no hardcoded ID in the coordinator file.
3. **Given** a user addresses a team member by name, **When** the coordinator routes the work, **Then** it calls `run_submit` with the member's `agent_name` and the task description, returning a run ID.
4. **Given** a run has been submitted, **When** the coordinator calls `run_watch`, **Then** live agent messages and tool call events are surfaced to the user as progress until the run completes.
5. **Given** a run has reached `awaiting_review`, **When** the coordinator detects this terminal state, **Then** it presents the diff summary and asks the user to approve or decline, then calls `run_review` with the decision.
6. **Given** a project is on-boarded from an existing GitHub repository, **When** on-boarding completes, **Then** the coordinator is written to `.github/agents/squad-agentweaver.agent.md` using the same template as new projects.

---

### Edge Cases

- What happens when the REST API is unreachable when a tool is called? The tool returns a human-readable MCP error describing the connection failure, not a raw exception.
- What happens when the API returns an HTTP 4xx error (e.g., 404 project not found, 401 unauthorized)? The tool returns a structured MCP error with the API's error message surfaced clearly.
- What happens when the API returns an HTTP 5xx error? The tool returns a human-readable MCP error distinguishing a server-side failure from a client-side one.
- What happens when `run_watch` is called but the run ends before the client connects? The tool returns the final run state immediately without attempting to stream.
- What happens when the SSE stream for `run_watch` is interrupted mid-run? The tool surfaces a partial-result error and returns whatever final state is available.
- What happens when an AI client passes invalid parameters to a tool? The MCP server rejects the call with a schema validation error before forwarding anything to the API.
- What happens when `SCAFFOLDER_API_KEY` contains an incorrect value? The API returns 401 and the tool returns a clear authentication-failure error to the client.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The MCP server MUST expose all Agentweaver operations as MCP tools over a stdio transport so that any MCP-capable host can call them without shell commands.
- **FR-002**: The MCP server MUST expose the following tool groups: Projects (`project_create`, `project_list`, `project_get`, `project_rename`, `project_relink`, `project_delete`, `project_configure`), Runs (`run_submit`, `run_status`, `run_watch`, `run_review`, `run_show_artifacts`, `run_get_file`), Team (`team_get`, `team_cast`, `team_member_add`, `team_member_retire`, `team_member_get_charter`), GitHub Auth (`github_signin`, `github_signout`, `github_status`), Sandbox Policy (`sandbox_policy_get`, `sandbox_policy_set`), and Catalog (`catalog_list_roles`, `catalog_list_scenarios`).
- **FR-003**: Each MCP tool MUST be a thin proxy to the corresponding REST API endpoint; the MCP server MUST contain no business logic of its own.
- **FR-004**: The MCP server MUST read the API base URL from the `SCAFFOLDER_API_URL` environment variable (defaulting to `http://localhost:5000`) and the API key from `SCAFFOLDER_API_KEY`. Credentials MUST NOT appear in tool schemas or tool call arguments.
- **FR-005**: The `run_watch` tool MUST connect to the API's Server-Sent Events run stream and forward agent messages and tool call events as MCP progress notifications so the AI client can display live progress. The tool MUST return the final run state when the stream ends.
- **FR-006**: HTTP 4xx and 5xx responses from the REST API MUST be mapped to MCP tool errors with human-readable messages that surface the API's error detail.
- **FR-007**: A `.mcp.json` registration file MUST be placed at the repository root. It MUST define the `agentweaver` server entry with the `dotnet run` command, project path, and the `env` block for `SCAFFOLDER_API_URL` and `SCAFFOLDER_API_KEY`.
- **FR-008**: The MCP server project (`apps/Scaffolder.Mcp`) MUST be a .NET 9 console application using the same SDK version as the rest of the solution.
- **FR-009**: The existing `apps/Scaffolder.Cli` project MUST be retired: removed from the solution and deleted or archived from the repository.
- **FR-010**: The CLI command reference documentation (`docs/reference/cli.md`) MUST be replaced with an MCP tool reference (`docs/reference/mcp.md`) describing each tool, its inputs, and its expected outputs.
- **FR-011**: The `run_submit` tool MUST support an `agent_name` parameter to target a specific team member for team-based runs.
- **FR-012**: The `team_cast` tool MUST support both a single-call flow (goal → proposal → confirm) and a two-step flow (separate proposal and confirm calls) to accommodate different AI client interaction patterns.
- **FR-013**: The MCP server MUST start up and be ready to receive tool calls within a time that does not degrade the MCP host's startup experience.
- **FR-014**: Invalid tool parameters MUST be rejected with a schema validation error before any request is forwarded to the REST API.
- **FR-015**: When a project team is confirmed, `SquadWriter` MUST write the Agentweaver Squad coordinator template to `.github/agents/squad-agentweaver.agent.md` inside the managed repository.
- **FR-016**: The coordinator template MUST resolve its Agentweaver project ID dynamically at session start by calling `project_list` and matching the current working directory against each project's `repository_path`. It MUST NOT contain a hardcoded project ID.
- **FR-017**: The coordinator template MUST route all team-member work dispatches to `run_submit` (with `agent_name` identifying the target member) instead of the generic `task` or `runSubagent` tool.
- **FR-018**: After calling `run_submit`, the coordinator MUST call `run_watch` and surface live progress (agent messages, tool call events) to the user.
- **FR-019**: When `run_watch` returns a run in `awaiting_review` state, the coordinator MUST present the diff summary to the user, ask for approval or decline, and call `run_review` with the decision.
- **FR-020**: The same coordinator template MUST be written during on-boarding of existing repositories (GitHub import or local path) as during new project creation.

### Key Entities

- **MCP Tool**: A named, schema-described callable that an MCP host discovers and invokes. Each tool maps one-to-one to a REST API operation group.
- **MCP Progress Notification**: An in-flight update sent from the MCP server to the client during a long-running tool call (e.g., `run_watch`), carrying agent message text or tool call event summaries.
- **Tool Schema**: The JSON Schema definition of a tool's input parameters, used by the MCP host to validate calls and by AI clients to understand what arguments to provide.
- **Registration File (`.mcp.json`)**: A JSON file at the repository root that declares how MCP hosts should launch and configure the MCP server, including command, arguments, and environment variables.
- **API Key**: A credential injected via the `SCAFFOLDER_API_KEY` environment variable. It is forwarded as a bearer token (or equivalent) on every REST API request and MUST NOT appear in any tool schema.
- **Agentweaver Squad Coordinator (`squad-agentweaver.agent.md`)**: A customized Squad coordinator agent definition written by Agentweaver into managed repositories. It co-exists with any generic `squad.agent.md` and overrides dispatch behavior to use Agentweaver MCP tools for run submission, watching, and review.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Any MCP-capable AI client can complete the full workflow — list projects, submit a run, watch it stream live, and review the result — using only MCP tool calls, with no shell commands required.
- **SC-002**: Every capability that was available in `Scaffolder.Cli` is reachable as an MCP tool call, with no regression in coverage.
- **SC-003**: The MCP server passes zero authentication credentials through tool schemas or tool arguments; all credentials flow exclusively through environment variables.
- **SC-004**: During a `run_watch` call, the AI client receives live progress updates (agent messages, tool call events) as they are emitted by the API, with no buffering until the run completes.
- **SC-005**: A developer can add the repository to any MCP-capable host and have Agentweaver tools available with no configuration steps beyond setting the `SCAFFOLDER_API_KEY` environment variable.
- **SC-006**: The MCP server contains no business logic; all business outcomes are determined solely by the REST API responses.
- **SC-007**: All HTTP error responses from the REST API are surfaced to the AI client as human-readable MCP tool errors, with enough detail for the client to understand and describe the failure.
- **SC-008**: When a team is cast and confirmed, `squad-agentweaver.agent.md` is present in `.github/agents/` of the managed repository and contains a coordinator that routes work via Agentweaver MCP tools.
- **SC-009**: The Agentweaver coordinator resolves its project ID dynamically; no project ID appears as a literal value inside the coordinator file.

## Assumptions

- The REST API (Scaffolder.Api) is already running and accessible at the configured `SCAFFOLDER_API_URL` when the MCP server starts; the MCP server does not start or manage the API process.
- The REST API already exposes all the endpoints required by the tool groups listed above; this feature does not add new API capabilities, only a new client surface.
- MCP hosts that support `.mcp.json` auto-discovery (Copilot CLI ≥1.0.59 and equivalents) are the primary target; hosts that require manual registration are a secondary concern and are not in scope for this specification.
- The `github_signin` device flow requires the user to open a browser and complete authentication manually; the MCP server cannot automate the browser step and returns the verification URL and user code for the user to act on.
- The `.NET` toolchain (version 9) is already installed on the developer machine or CI environment where the MCP server is launched via `dotnet run`.
- Run streaming relies on the API's existing SSE endpoint; no changes to the streaming protocol or the API's SSE implementation are in scope for this feature.
- The `apps/Scaffolder.Cli` project has no dependents outside the solution (no external packages or scripts depend on it); removing it does not break any external consumers.
- Documentation for the retired CLI (`docs/reference/cli.md`) has no inbound links from external sources that would become broken; internal cross-references will be updated as part of this feature.
