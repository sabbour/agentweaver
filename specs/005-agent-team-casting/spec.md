# Feature Specification: Agent Team Casting

**Feature Branch**: `005-agent-team-casting`

**Created**: 2026-06-12

**Status**: Draft

**Input**: User description: "The Agentweaver app is a multi-agent orchestrator. For each project, I should be able to cast a team of agents specialized for working on tasks. The casting can look at an existing project, analyze it, and suggest a team of certain roles and charters. The casting can also cast a team of roles from well known groupings of roles by scenarios (ex: content authoring, software development, etc.). The casting can also ask for a prompt of what the user is trying to accomplish then use it to create the right set of agents. Look at GitHub.com/Sabbour/squadboard and github.com/bradygaster/squad for details about casting, universes, roles, charters. This uses the Squad project configuration, so agent charters follows the same convention (stored under the .squad folder, using the same configuration convention). I should be able to read definitions of existing agents in my project's folder and update them. Everything is Git file based here, so we should find a way to sync settings by committing. You need to create a new branch for this work."

## Overview

Casting is how a user assembles a team of specialized AI agents for a project. Building on Projects (feature 003), each project gains a team whose definition lives entirely in the project's `.squad/` folder, following the established Squad configuration convention: a roster (`team.md`), per-agent charters (`.squad/agents/{name}/charter.md`), and casting bookkeeping (`.squad/casting/policy.json`, `registry.json`, `history.json`). Because the team is just files inside the project's git working directory, a team persists, travels with the repository, and is shared with anyone who clones it.

A user can cast a team three ways: (1) pick a well-known scenario grouping of roles (for example, software development or content authoring) and get a ready-made team; (2) describe in free text what they are trying to accomplish and have the system propose the right set of roles; or (3) point the system at the project's existing contents so it analyzes the codebase and suggests roles and charters tailored to what it finds. In all three modes the system follows a discovery/proposal/confirmation/creation flow: it proposes a roster the user can accept, amend, or reject before any files are written.

Beyond initial casting, a user can read the definitions of the agents already cast into a project, edit their charters, add or remove members, and re-role existing members. Every change is a file change under `.squad/`; the user syncs those changes by committing them to the project's git repository, which is the single mechanism for persisting and sharing a team.

Consistent with the constitution: everything a user can do to a team MUST be available identically from the CLI (TUI) and the Web UI because the backend API is the single source of truth (Principles III and IV); any team proposal generated with model assistance MUST use GitHub Copilot, the fixed model provider, with each role's default model overridable at runtime (Principle II); and the casting flow MUST avoid emitting names, charters, or content that violate Responsible AI expectations (Principle IX).

## Clarifications

### Session 2026-06-12

- Q: When casting from a free-text prompt or by analyzing an existing project, does the system use an AI model to generate the proposal and charters? → A: Yes. Prompt-based and analysis-based casting are model-assisted and MUST use GitHub Copilot as the fixed model provider (the provider is not selectable, per Principle II); each run uses the relevant role's default model, which is overridable at runtime. Scenario-grouping casting is deterministic and does not require a model call.
- Q: How does a user "sync" a team? → A: Syncing is an explicit user action that commits the project's `.squad/` changes to the project's git repository. The system never auto-commits casting changes; it stages/presents them and the user confirms the commit.
- Q: What happens when a user casts a team into a project that already has one? → A: The system MUST detect the existing team and require the user to choose between augmenting the existing team (add/re-role members, preserving existing names and history) or recasting (a new cast snapshot). It MUST NOT silently overwrite existing charters or rename existing agents.
- Q: Where do scenario groupings and role definitions come from? → A: The app ships and maintains its own catalog, kept schema-compatible with the Squad role catalog so that projects already initialized with a `.squad/` folder round-trip cleanly. The catalog is bundled with the app and versioned alongside it; it is consulted only when generating a new cast or adding a member, and is NOT written into the project. Only the generated team (roster, charters, `casting/` files) lands in the project's `.squad/` folder.
- Q: How is the naming universe for a new cast chosen? → A: The app auto-proposes a universe from an allowlist; the user can override the proposed universe before confirming.
- Q: What happens when a proposed team needs more members than the chosen universe can name (capacity exceeded)? → A: The system falls back to generic, unnamed members for the overflow rather than rejecting the cast or forcing a universe change.
- Q: When the project's working directory is missing or unavailable, how do casting and sync respond? → A: The system reuses feature 003's project-unavailable remedy - it blocks the team's operations and offers the user the choice to relink the project to a new working directory or remove the project record, preserving any existing files.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Cast a team from a scenario grouping (Priority: P1)

A user opens a project that has no team yet, chooses to cast a team, and selects a well-known scenario grouping (for example, "software development" or "content authoring"). The system proposes a roster of roles drawn from that scenario, each member given a name from a single naming universe and a starter charter. The user reviews the proposed roster, accepts it, and the system writes the team into the project's `.squad/` folder following the Squad convention.

**Why this priority**: This is the foundational, fully deterministic slice of casting. It proves the end-to-end path - choose a team, confirm it, and produce a valid `.squad/` team on disk - without depending on model-assisted analysis. Without it, there is no team to read, edit, or sync.

**Independent Test**: From a single client, open a project with no `.squad/` team, cast from the "software development" scenario, accept the proposal, and confirm that a valid `.squad/` team is created (roster in `team.md`, a charter per member under `.squad/agents/{name}/charter.md`, and casting files under `.squad/casting/`) - with no other casting mode required.

**Acceptance Scenarios**:

1. **Given** a project with no existing team, **When** the user selects a scenario grouping and requests a cast, **Then** the system presents a proposed roster of roles with member names from one universe and a starter charter for each, before writing any files.
2. **Given** a proposed roster, **When** the user accepts it, **Then** the system creates `.squad/team.md` listing the members, a `.squad/agents/{name}/charter.md` for each member, and the casting bookkeeping files (`policy.json`, `registry.json`, `history.json`), all following the Squad convention.
3. **Given** a proposed roster, **When** the user removes a role, adds a role, or renames a member before accepting, **Then** the created team reflects those edits.
4. **Given** a proposed roster, **When** the user rejects it, **Then** no files are written under `.squad/`.
5. **Given** the list of available scenario groupings, **When** the user views them, **Then** at least the "software development" and "content authoring" groupings are offered, each describing the roles it includes.

---

### User Story 2 - Cast a team from a free-text goal (Priority: P2)

A user describes, in their own words, what they are trying to accomplish (for example, "I want to build and launch a marketing site with a blog and email capture"). The system interprets the goal, proposes a set of roles suited to it with names from one universe and starter charters, and the user confirms to create the team.

**Why this priority**: Free-text casting is the most flexible entry point and a core promise of the product, but it builds on the proposal/confirmation/creation machinery proven in Story 1 and adds model-assisted role selection on top.

**Independent Test**: From a single client, provide a free-text goal to a project with no team, confirm the proposed roster, and verify a valid `.squad/` team is created whose roles plausibly match the stated goal.

**Acceptance Scenarios**:

1. **Given** a project with no existing team, **When** the user submits a free-text description of their goal, **Then** the system proposes a roster of roles, member names from one universe, and starter charters derived from that goal, before writing any files.
2. **Given** a free-text casting request, **When** the proposal is generated, **Then** it is produced using GitHub Copilot as the fixed model provider with the relevant role's default model (overridable at runtime), and the run is observable as steps in the same way other runs are.
3. **Given** a proposed roster from a free-text goal, **When** the user accepts, amends, or rejects it, **Then** the same create/edit/no-write behavior as Story 1 applies.
4. **Given** a free-text goal that is empty or whitespace-only, **When** the user submits, **Then** the request is rejected with a clear reason and no proposal is generated.

---

### User Story 3 - Cast a team by analyzing the existing project (Priority: P3)

A user with an existing codebase asks the system to look at the project and suggest a team. The system analyzes the project's working directory - languages, frameworks, presence of tests, documentation, and overall structure - and proposes roles and charters tailored to what it finds (for example, including a frontend role when a web UI framework is detected and a tester role when tests are present).

**Why this priority**: Analysis-based casting delivers the most context-aware teams but depends on both the proposal machinery (Story 1) and model-assisted reasoning (Story 2), and requires reading project contents, so it layers on last.

**Independent Test**: From a single client, point the system at a project that contains recognizable signals (for example, a web UI framework and a test suite), request an analysis-based cast, and confirm the proposed roster includes roles justified by those detected signals before any files are written.

**Acceptance Scenarios**:

1. **Given** a project whose working directory contains detectable signals, **When** the user requests an analysis-based cast, **Then** the system analyzes the project and proposes a roster whose roles and charters reference the signals it detected.
2. **Given** the analysis proposal, **When** it is presented, **Then** the system explains why each suggested role was included (which signal led to it).
3. **Given** an analysis-based proposal, **When** the user accepts, amends, or rejects it, **Then** the same create/edit/no-write behavior as Story 1 applies.
4. **Given** a project working directory that is empty or has no recognizable signals, **When** the user requests an analysis-based cast, **Then** the system MUST still return a usable default proposal and indicate that it fell back to defaults.
5. **Given** analysis reads project files, **When** it runs, **Then** it MUST stay within the project's working directory and MUST NOT read or write outside that boundary.

---

### User Story 4 - Read and update existing agent definitions (Priority: P2)

A user opens a project that already has a team and inspects the cast: the roster, each member's role, and each member's charter. The user edits a charter, adds a new member, removes a member, or changes a member's role. The system applies the change as file edits under `.squad/` following the convention, without disturbing unrelated members.

**Why this priority**: Reading and updating definitions is essential for living with a team over time and is independently valuable for projects that already have a `.squad/` team (including ones not created by this app). It does not depend on the generative casting modes.

**Independent Test**: From a single client, open a project with an existing `.squad/` team, view a member's charter, edit it, and confirm the edit is persisted to that member's `charter.md` while other members' files are unchanged.

**Acceptance Scenarios**:

1. **Given** a project with an existing `.squad/` team, **When** the user lists the team, **Then** the system reads the roster and shows each member's name, role, and charter, including teams created outside this app as long as they follow the convention.
2. **Given** a member's charter, **When** the user edits and saves it, **Then** the change is written to that member's `charter.md` and no other member's files are modified.
3. **Given** an existing team, **When** the user adds a member, **Then** a new charter is created, a name is allocated from the team's universe, and the roster and casting registry are updated to include the new member.
4. **Given** an existing team, **When** the user removes a member, **Then** the member is retired (its name remains reserved in the registry and its charter is archived rather than destroyed), and the roster is updated.
5. **Given** an existing team, **When** the user changes a member's role, **Then** the roster and that member's charter are updated to reflect the new role while the member's name is preserved.
6. **Given** a project whose `.squad/` files are malformed or do not follow the convention, **When** the user opens the team, **Then** the system reports what is invalid rather than silently discarding or corrupting the existing files.

---

### User Story 5 - Sync the team by committing to git (Priority: P3)

After casting or editing a team, the user syncs the changes by committing the project's `.squad/` folder to the project's git repository. The system shows the user which `.squad/` files changed and lets them commit those changes, so the team persists and can be shared with anyone who clones the project.

**Why this priority**: Persistence and sharing are what make a team durable, but a team is usable within a session before it is committed, so syncing layers on after the team exists.

**Independent Test**: From a single client, make a casting change in a project, use the sync action to review the changed `.squad/` files and commit them, then confirm the project's git repository contains a commit that includes those `.squad/` changes.

**Acceptance Scenarios**:

1. **Given** uncommitted `.squad/` changes in a project, **When** the user opens the sync action, **Then** the system lists the `.squad/` files that were added, modified, or removed.
2. **Given** listed `.squad/` changes, **When** the user confirms the sync, **Then** the system commits exactly those `.squad/` changes to the project's git repository and reports the resulting commit.
3. **Given** a project with no uncommitted `.squad/` changes, **When** the user opens the sync action, **Then** the system reports there is nothing to sync and creates no commit.
4. **Given** the system at any point during casting or editing, **When** changes are made, **Then** the system MUST NOT commit `.squad/` changes automatically; committing only happens through the explicit sync action.

---

### Edge Cases

- What happens when a user casts a team into a project that already has one? The system MUST require the user to choose augment vs recast and MUST NOT silently overwrite charters or rename existing agents.
- What happens when the proposed team needs more members than the chosen universe can name (universe capacity exceeded)? The system MUST fall back to generic, unnamed members for the overflow and MUST NOT produce duplicate or empty names.
- How does the system handle a member name that would collide with an existing or retired member in the registry? Names MUST remain unique; a retired name stays reserved and is not reassigned.
- What happens if the project's working directory is missing or unavailable when casting or syncing? The operation MUST be blocked and the system MUST offer feature 003's remedy: relink the project to a new working directory or remove the project record, preserving any existing files.
- What happens if a model-assisted proposal (free-text or analysis) fails or times out? The system MUST surface the failure and write no `.squad/` files, leaving the project unchanged.
- What happens when analysis encounters a very large project? Analysis MUST remain bounded and still return a usable proposal.
- What happens if a charter edit produces content that fails Responsible AI checks? The system MUST flag it and MUST NOT silently persist content that violates the project's RAI expectations.
- What happens when two people edit the same team on different branches and both commit? Because team files are git-based and append-only logs use union merge semantics, syncing MUST combine their changes without losing either side's roster entries or history.

## Requirements *(mandatory)*

### Functional Requirements

#### Casting modes and proposal flow

- **FR-001**: The system MUST let a user cast a team of agents for a specific project, where the team's definition is stored entirely within that project's `.squad/` folder.
- **FR-002**: The system MUST offer casting from a set of well-known scenario groupings of roles. Each grouping is a named preset that carries a set of placeholder roles (and starter charters) that are re-cast with project-specific names from the chosen universe when applied. The set MUST include at least "software development" and "content authoring", and SHOULD cover the other common groupings demonstrated by the referenced Squad projects (for example AI agent development, open-source maintenance, research spike, library/SDK, ops/incident runbook, bug bash, and product/PM feature delivery). Each grouping MUST describe the roles it provides.
- **FR-003**: The system MUST offer casting from a free-text description of what the user is trying to accomplish, and MUST derive a proposed set of roles from that description.
- **FR-004**: The system MUST offer casting by analyzing the project's existing contents (languages, frameworks, tests, documentation, structure) and proposing roles and charters tailored to detected signals.
- **FR-005**: For every casting mode, the system MUST follow a discovery/proposal/confirmation/creation flow: it MUST present a proposed roster and MUST NOT write any `.squad/` files until the user confirms.
- **FR-006**: Before confirmation, the system MUST let the user amend the proposed roster - add a role, remove a role, rename a member, or change a member's role - and the created team MUST reflect those amendments.
- **FR-007**: When the user rejects a proposal, the system MUST write no `.squad/` files and leave the project unchanged.
- **FR-008**: Model-assisted casting (free-text and analysis modes) MUST use GitHub Copilot as the fixed model provider (the provider is not a user choice, per Principle II); each run MUST use the relevant role's default model, which MUST be overridable at runtime; and the system MUST expose the proposal generation as observable run steps (Principle V).
- **FR-009**: Scenario-grouping casting MUST be deterministic and MUST NOT require a model call.
- **FR-010**: For analysis-based casting, the system MUST explain why each proposed role was included by referencing the signal that justified it, and MUST return a usable default proposal when no recognizable signals are found.
- **FR-011**: The system MUST source its scenario groupings and role definitions from a catalog that the app ships and maintains. The catalog MUST be kept schema-compatible with the Squad role catalog (role identifiers and charter structure) so that projects already initialized with a `.squad/` folder round-trip cleanly. The catalog MUST be bundled with the app (not written into any project's `.squad/` folder) and MUST be consulted only when generating a new cast or adding a member.

#### Squad convention conformance

- **FR-012**: Created teams MUST follow the Squad configuration convention: a roster in `.squad/team.md`, one charter per member at `.squad/agents/{name}/charter.md`, and casting bookkeeping in `.squad/casting/policy.json`, `.squad/casting/registry.json`, and `.squad/casting/history.json`.
- **FR-013**: Each member's name MUST be allocated from a single naming universe per cast assignment; the system MUST NOT mix universes within one cast.
- **FR-014**: The system MUST auto-propose a naming universe for a new cast from an allowlist and MUST let the user override the proposed universe before confirming.
- **FR-015**: The system MUST record each cast as a snapshot in casting history (universe used, members, and timestamp) and MUST record each member in the casting registry with a persistent name and status.
- **FR-016**: Member names within a project MUST be unique; a retired member's name MUST remain reserved and MUST NOT be reassigned.
- **FR-017**: When a proposed team exceeds the chosen universe's capacity, the system MUST fall back to generic, unnamed members for the overflow rather than producing duplicate, empty, or out-of-universe names, and MUST NOT reject the cast solely for exceeding capacity.
- **FR-018**: Each generated charter MUST contain the conventional charter content (at least the member's identity/role, what it owns, how it works, and its boundaries) so that it is a valid Squad charter.

#### Reading and updating existing definitions

- **FR-019**: The system MUST read and present the definitions of agents already cast into a project, including teams that follow the convention but were created outside this app.
- **FR-020**: The system MUST let a user edit an existing member's charter and persist the change to that member's `charter.md` without modifying unrelated members' files.
- **FR-021**: The system MUST let a user add a member to an existing team (allocating a name from the team's universe and updating the roster and registry), remove a member (retiring it: archiving its charter and reserving its name rather than destroying it), and change a member's role (updating roster and charter while preserving the name).
- **FR-022**: When a project already has a team, any new casting request MUST require the user to choose between augmenting the existing team and recasting, and MUST NOT silently overwrite charters or rename existing agents.
- **FR-023**: When a project's `.squad/` files are malformed or do not conform to the convention, the system MUST report what is invalid and MUST NOT silently discard or corrupt the existing files.

#### Sync (git-based persistence)

- **FR-024**: The system MUST provide an explicit sync action that lists the project's added, modified, and removed `.squad/` files and commits exactly those changes to the project's git repository on user confirmation. The system MUST stage the specific changed `.squad/` files individually rather than bulk-adding the working tree, so the commit never includes files outside `.squad/`.
- **FR-025**: The system MUST NOT commit `.squad/` changes automatically; committing MUST only occur through the explicit sync action.
- **FR-026**: When there are no uncommitted `.squad/` changes, the sync action MUST report that there is nothing to sync and create no commit.
- **FR-027**: The system MUST configure the project so that append-only team state files merge without conflict (union merge) so that teams edited on different branches combine on merge without losing roster entries or history.

#### Cross-cutting (constitution)

- **FR-028**: Every casting and team-management capability MUST be available identically from both the CLI (TUI) and the Web UI, with the backend API as the single source of truth and clients holding no business logic.
- **FR-029**: All file reads and writes performed during casting, analysis, editing, and syncing MUST stay within the project's working directory boundary and MUST NOT touch files outside it.
- **FR-030**: The system MUST apply Responsible AI checks to generated and edited team content (names, charters, proposals) and MUST flag rather than silently persist content that violates the project's RAI expectations.
- **FR-031**: When the project's working directory is missing or unavailable, casting and sync operations MUST be blocked, and the system MUST reuse feature 003's project-unavailable remedy: offer the user the choice to relink the project to a new working directory or remove the project record, while preserving any existing files.

#### Model selection (fixed provider, per-role default model)

- **FR-032**: GitHub Copilot MUST be the fixed model provider for all model-assisted casting and for later agent execution. The provider MUST NOT be presented as a user choice and MUST NOT be selectable per run, per role, or per project (Principle II).
- **FR-033**: Casting MUST assign each cast role a default model recorded in the role's definition and charter, so that every cast member carries a default model in addition to its name, role, and charter.
- **FR-034**: A role's default model MUST be overridable at runtime - both for model-assisted casting runs (free-text and analysis modes) and for later agent execution - without changing the fixed GitHub Copilot provider. The model actually used for a run MUST be observable in the run's steps (Principle V).

### Key Entities *(include if feature involves data)*

- **Project**: The top-level container (from feature 003) with a working directory; its model provider is fixed to GitHub Copilot and is not a user choice (Principle II). A project has at most one active team at a time. The team lives inside the project's working directory under `.squad/`.
- **Team (Cast)**: The set of agents cast for a project, expressed by the roster (`team.md`) plus the casting bookkeeping. Belongs to exactly one project and is tied to one naming universe per cast assignment.
- **Agent (Cast Member)**: A single team member with a persistent name, a role, and a charter. Has a status (for example active or retired). Belongs to one team.
- **Role**: A specialization that determines a member's responsibilities, the content of its starter charter, and a default model used when the member runs (the provider is always GitHub Copilot; the default model is overridable at runtime). Roles span engineering (lead, frontend, backend, tester, devops, SDK/platform integrator), AI engineering (agent architect, prompt engineer, AI safety reviewer, evaluator), product and research (PM, customer researcher, quality reviewer), design (UX, prototype), content (writer, editor), developer relations/documentation, and business (sales, marketing, customer success), plus the always-present operational roles (Scribe, Work Monitor).
- **Charter**: A member's identity document at `.squad/agents/{name}/charter.md` describing who the member is, what it owns, how it works, its boundaries, and its default model (overridable at runtime).
- **Scenario Grouping**: A named, predefined set of roles for a common kind of work (for example software development, content authoring) used by deterministic casting. Defined in the app-bundled catalog.
- **Role/Scenario Catalog**: The app-bundled, app-maintained source of scenario groupings and role definitions, kept schema-compatible with the Squad role catalog. Consulted only when generating a cast or adding a member; never written into a project's `.squad/` folder.
- **Naming Universe**: A themed pool of names from which member names are drawn; one universe per cast assignment, governed by an allowlist and a per-universe capacity.
- **Casting Policy**: The configuration governing casting (`.squad/casting/policy.json`): which universes are allowed and each universe's capacity.
- **Casting Registry**: The persistent record of every member's name, universe, default model, and status (`.squad/casting/registry.json`), including retired members whose names stay reserved.
- **Cast History**: The append-only record of cast snapshots and universe usage over time (`.squad/casting/history.json`).
- **Sync (Commit)**: The act of committing the project's `.squad/` changes to its git repository, persisting and sharing the team.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From either client, a user can cast a team from a scenario grouping and end with a valid, convention-conformant `.squad/` team on disk, in under 2 minutes and with no manual file editing.
- **SC-002**: For all three casting modes, 100% of confirmed casts produce a roster, a charter for every member, and complete casting bookkeeping files; 0% of rejected proposals write any `.squad/` file.
- **SC-003**: A user can read an existing team's roster and any member's charter, and an edit to one charter never alters another member's files (verified by file-level diff).
- **SC-004**: After a sync, the project's git history contains a commit that includes exactly the intended `.squad/` changes and nothing outside `.squad/`; with no pending changes, no commit is created.
- **SC-005**: Casting never produces duplicate or out-of-universe member names, and never reassigns a retired member's name, across repeated casts and edits.
- **SC-006**: The CLI and Web UI expose the identical set of casting and team-management capabilities (every capability reachable from one is reachable from the other).
- **SC-007**: Analysis-based casting on a project with recognizable signals proposes at least one role that is demonstrably justified by a detected signal, and on a signal-less project still returns a usable default proposal.

## Assumptions

- This feature builds on Projects (feature 003): a team is always cast within an existing project, and the team's files live in that project's working directory, which is the run/operation boundary.
- The `.squad/` folder is part of the project's git repository, so team definitions are versioned and shared through normal git operations; "sync" means commit (push/pull and remotes are handled by the project's normal git workflow and are out of scope here).
- Model-assisted casting uses GitHub Copilot as the fixed model provider (Principle II) and the existing observable-run mechanism; the model used is the relevant role's default model, overridable at runtime; no new model source is introduced.
- The Squad configuration convention referenced by the user is the one used by the projects at github.com/bradygaster/squad and github.com/Sabbour/squadboard (roster `team.md`, per-agent `charter.md`, and `casting/` policy/registry/history files); this feature conforms to that convention rather than inventing a new one, and the app's role/scenario catalog is kept schema-compatible with the Squad role catalog so existing `.squad/` folders round-trip cleanly.
- Coordinator-style orchestration, work routing, running agents on tasks, and ceremonies are separate concerns; this feature covers casting and managing the team definition only, not executing the team.
- A scenario grouping is modeled as a preset bundle of placeholder roles plus starter charters, re-cast with project-specific names and a universe at apply time (see FR-002 for the groupings the catalog ships). New groupings can be added later without changing the casting flow.
