# Feature Specification: Projects

**Feature Branch**: `003-projects`

**Created**: 2026-06-11

**Status**: Draft

**Input**: User description: "I want to be able to create 'Projects'. A project has settings (AI Provider settings, default model per provider). A project can be created as a blank project or from an existing GitHub repository. It gets cloned/created in a local directory. The landing page of the app should show a list of projects in the form of cards, or offer to create a new one (blank) or from a repo."

## Overview

A Project is the top-level, persistent container a user works in. It pairs a working directory with the project's AI configuration — which model provider is the project default and the default model to use for each provider — and acts as the home for the agent runs defined in earlier features (001 single-agent run, 002 sandboxed execution). A user either starts a project blank or creates it from an existing GitHub repository; in both cases the project is materialized as a local directory that becomes the boundary within which that project's runs operate.

The application's landing page is a gallery of project cards that lets a user open an existing project or start a new one — blank or from a repository. Everything a user can do to a project MUST be available identically from the CLI (TUI) and the Web UI, because the backend API is the single source of truth and the clients hold no business logic of their own.

The provider and model a project stores are defaults only: consistent with the two-permitted-providers rule, the provider and model remain selectable per run, and a run may override the project defaults. No model source other than GitHub Copilot CLI and Microsoft Foundry is ever offered.

## Clarifications

### Session 2026-06-11

- Q: GitHub authentication scope and Copilot authorization (FR-005, FR-016) → A: A single "Sign in with GitHub" (OAuth device flow), reachable identically from both the CLI/TUI and the Web UI, grants both repository access — including cloning private repositories — and authorization to use the GitHub Copilot provider in place of a separately entered Copilot API key. The GitHub sign-in MUST NOT authorize Microsoft Foundry, which continues to use its own separate credentials.
- Q: AI provider credential storage scope (FR-016) → A: Provider credentials are stored globally / installation-wide, shared across all projects (not per-project).
- Q: Local project working-directory location (FR-006) → A: The user chooses the working directory for each project at creation time; there is no single managed workspace root.
- Q: What a blank project initializes (FR-003) → A: A blank project initializes the chosen working directory as an empty directory initialized as a git repository (git init).
- Q: Project delete scope (FR-019) → A: Deleting a project removes only the project record; the on-disk working directory and any cloned contents are always left on disk.
- Q: Deleting a project that has in-flight or active runs (FR-019) → A: Deletion MUST require explicit user confirmation; if in-flight runs exist, the system MUST first cancel them to a visible terminal state and THEN remove the project record. Working-directory files are always preserved (record-only delete).
- Q: Chosen working directory already exists and is non-empty (FR-003, FR-004) → A: For both a blank project and one created from a GitHub repository, the chosen directory MUST be empty or non-existent; if it already exists and is non-empty, the system MUST reject creation with a clear reason and MUST NOT overwrite or adopt the existing content. (Importing a pre-existing local folder remains out of scope.)
- Q: Recorded working directory missing or inaccessible at list or open time (FR-022) → A: The system MUST still list the project but mark it unavailable, MUST block its runs because the sandbox boundary is invalid, and MUST offer the user a choice to relink the project to a new working directory or remove the project record; any existing files MUST be preserved.
- Q: Source of the accountable human owner (FR-024) → A: The accountable human MUST be the GitHub-signed-in user when a GitHub sign-in is present; otherwise it MUST be the local operating-system/installation user identity, and this identity MUST be recorded on the project for accountability and audit.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create a blank project (Priority: P1)

A user starts a new, empty project by giving it a name, selecting "blank", and choosing a working directory. The system initializes the chosen directory as an empty git repository, records the project, and adds it to the project list.

**Why this priority**: Creating a project is the foundational capability this feature exists to provide. Without it there are no projects to list, configure, or run inside. It is the smallest slice that delivers standalone value.

**Independent Test**: From a single client, create a blank project with a name and a chosen working directory, then confirm the project is persisted, is listed, and that the chosen directory exists as an empty git repository — with no other feature required.

**Acceptance Scenarios**:

1. **Given** the create flow, **When** the user names a project, selects "blank", and chooses a working directory, **Then** a project is created with that name and the chosen directory is initialized as an empty git repository.
2. **Given** a newly created blank project, **When** the user returns to the project list, **Then** the project appears with its name and an indication that its origin is "blank".
3. **Given** an attempt to create a project with an empty or whitespace-only name, **When** the user submits, **Then** creation is rejected with a clear reason and no project or directory is created.

---

### User Story 2 - Browse projects on the landing page as cards (Priority: P1)

When the user opens the application, the landing page shows existing projects as a set of cards, each summarizing a project (at least its name and origin), and offers clear actions to create a new project — either blank or from a repository. With no projects yet, the page shows an empty state that still offers both create actions.

**Why this priority**: The landing page is the primary entry point described for the feature and the place every other project action begins. It is core to the described experience, so it sits alongside Story 1 at P1.

**Independent Test**: With several projects present, open the landing page and confirm one card per project plus visible create-blank and create-from-repo entry points; with zero projects, confirm the empty state still offers both create actions.

**Acceptance Scenarios**:

1. **Given** one or more existing projects, **When** the user opens the landing page, **Then** each project is shown as a card summarizing at least its name and its origin (blank or GitHub).
2. **Given** the landing page, **When** the user looks for ways to add a project, **Then** actions to create a blank project and to create a project from a GitHub repository are both available.
3. **Given** no projects exist, **When** the user opens the landing page, **Then** an empty state is shown that still offers both create actions.
4. **Given** a project card, **When** the user selects it, **Then** the user opens (enters) that project.

---

### User Story 3 - Create a project from a GitHub repository (Priority: P2)

A user creates a project by pointing at an existing GitHub repository and choosing a working directory. The system clones the repository into the chosen directory and records the project, including its GitHub origin. Cloning uses the same single GitHub sign-in (OAuth device flow) defined in FR-005, which grants both repository access — including private repositories — and authorization to use the GitHub Copilot provider; tokens or other credentials MUST never appear in any output, log, or telemetry.

**Why this priority**: This is a high-value path, but it builds on the create and list capabilities; blank creation already proves the core, so cloning layers on at P2.

**Independent Test**: Provide a public GitHub repository, choose a working directory, create a project from it, and confirm the repository contents are present in the chosen directory and the project is listed with its GitHub origin; then provide a private repository and confirm the system initiates an OAuth device flow, and on success the clone completes without exposing the credential.

**Acceptance Scenarios**:

1. **Given** the create-from-repo flow, **When** the user supplies a GitHub repository reference, chooses a working directory, and confirms, **Then** the repository is cloned into the chosen directory and a project is created recording the GitHub origin (the source repository reference).
2. **Given** a public repository, **When** the clone completes, **Then** the project's working directory contains the repository contents and the project appears in the list.
3. **Given** a private repository, **When** the user initiates the clone, **Then** the system initiates an OAuth device flow to authenticate the user; on success the clone completes.
4. **Given** any clone that uses credentials, **When** events, logs, or telemetry are produced, **Then** no secret or credential value appears in them.
5. **Given** a clone that fails (for example an invalid reference, no network, or no access), **When** the failure occurs, **Then** a clear reason is surfaced and no partially-created or inconsistent project is left behind.

---

### User Story 4 - Configure a project's AI provider settings and default model (Priority: P2)

Each project stores AI provider settings: which of the two permitted providers is the project's default and a default model for each provider. The user can view and change these settings. Exactly two providers are ever offered — GitHub Copilot CLI and Microsoft Foundry — and no other provider can be configured. When GitHub Copilot is the selected provider, the GitHub sign-in (FR-005) authorizes it; no separate API key is required or stored for that provider. Microsoft Foundry uses its own separate credentials and is never authorized by the GitHub sign-in. The stored values are defaults for runs started in the project; the provider and model remain selectable per run, and a run may override the project defaults.

**Why this priority**: Settings make a project useful for running agents and encode the project-level form of the two-providers rule, but a project can exist and be listed before it is configured, so this follows the core create/list slices.

**Independent Test**: Open a project's settings, confirm exactly the two permitted providers are selectable, set a default model for each provider and choose a default provider, then confirm the values persist and are presented as the pre-selected defaults when a run is started in that project.

**Acceptance Scenarios**:

1. **Given** a project's settings, **When** the user views available AI providers, **Then** exactly two providers are offered — GitHub Copilot CLI and Microsoft Foundry — and no other.
2. **Given** the settings, **When** the user selects a default model for a provider, **Then** that model is stored as the project's default model for that provider.
3. **Given** the settings, **When** the user chooses the project's default provider, **Then** that provider is stored as the project default.
4. **Given** configured defaults, **When** a run is started inside the project, **Then** the project's default provider and model are presented as the pre-selected choice, and the user may override them per run using one of the two permitted providers.
5. **Given** an attempt to configure any provider other than the two permitted, **When** submitted, **Then** it is rejected.

---

### User Story 5 - Use every project capability from both the CLI and the Web UI (Priority: P2)

Everything a user can do with projects — list them, create a blank one, create one from a GitHub repository, view and edit settings, rename, and delete — is available identically from the CLI (TUI) and the Web UI, because both are thin clients over the same authoritative API.

**Why this priority**: Required by the two-front-ends-at-parity and API-first principles. It does not block proving the core create/list flows, but the capability is not complete until both clients reach it equally.

**Independent Test**: Perform each project operation once from the CLI and once from the Web UI and confirm identical outcomes, and that neither client embeds project business logic of its own.

**Acceptance Scenarios**:

1. **Given** the project capabilities, **When** exercised from the CLI (TUI), **Then** list, create-blank, create-from-repo, view/edit settings, rename, and delete are all available.
2. **Given** the same capabilities, **When** exercised from the Web UI, **Then** the same set is available and produces the same results.
3. **Given** any project operation, **When** performed, **Then** it is carried out through the authoritative API and behaves identically regardless of which client initiated it.

---

### User Story 6 - Manage a project: rename, edit, and delete (Priority: P3)

A user can update a project after creating it — change its name, change its AI provider settings, and delete it when it is no longer needed.

**Why this priority**: Lifecycle management is important but lower priority than creating, listing, and configuring projects, so it sits at P3.

**Independent Test**: Rename a project and confirm the new name shows on its card; edit its settings and confirm persistence under the two-provider constraint; delete a project and confirm it is removed from the list and that its on-disk working directory and any cloned contents remain intact.

**Acceptance Scenarios**:

1. **Given** an existing project, **When** the user renames it, **Then** the new name is stored and shown on its card and in the list.
2. **Given** an existing project, **When** the user edits its AI provider settings, **Then** the updated settings persist, subject to the two-provider constraint.
3. **Given** an existing project, **When** the user deletes it, **Then** it no longer appears in the project list, and its on-disk working directory and any cloned contents are left intact on disk.

---

### Edge Cases

- **Empty or duplicate name**: An empty or whitespace-only name is rejected. Project names are user-facing labels and need not be unique; each project is addressed by a stable internal identifier, so duplicate display names are allowed but remain distinguishable.
- **Target directory already exists or is non-empty**: For both blank and GitHub-repository projects, the chosen working directory MUST be empty or non-existent. When it already exists and is non-empty, creation MUST be rejected with a clear reason; existing files MUST NOT be overwritten or adopted (FR-003, FR-004).
- **Invalid or unreachable GitHub reference**: A bad reference, missing network, or lack of access produces a clear failure and leaves no partially-created project; credentials are not leaked in the error.
- **Private repository without usable authentication**: When authentication for a private repository is unavailable or rejected, the clone is denied with a clear reason and no secret is written to logs or telemetry.
- **Clone interrupted mid-way**: A network loss during cloning does not leave a half-created project; the partial clone is cleaned up or the project is clearly marked failed.
- **Unsupported provider**: Any attempt to select or configure a provider outside the two permitted is rejected.
- **Default model no longer available**: When a project's stored default model is no longer offered by its provider, the user is prompted to pick an available model rather than a run silently using an unintended model.
- **Deleting a project with active runs**: Deletion always requires explicit user confirmation. When the project has in-flight runs, the system MUST first cancel them to a visible terminal state before removing the project record, so no run is orphaned (FR-019).
- **Run attempts to escape the project directory**: A project's run that attempts a file or process operation outside the project's working directory is rejected, not merely warned.
- **Recorded working directory missing or inaccessible**: When a project's recorded working directory is missing or inaccessible at list or open time, the project MUST still be listed but marked unavailable, and its runs MUST be blocked because the sandbox boundary (FR-022) is invalid. The user MUST be offered a choice to relink the project to a new working directory or remove the project record (FR-019, FR-026); any existing files MUST be preserved.
- **Large number of projects**: The landing page remains usable as the number of project cards grows (for example through scrolling or paging).
- **Hosted-cloud deployment with no local user machine**: In a hosted-cloud deployment there is no local developer directory; the project's storage location is resolved by the deployment rather than assuming a local path.

## Requirements *(mandatory)*

### Functional Requirements

#### Project creation

- **FR-001**: System MUST allow a user to create a new project either as a blank project or from an existing GitHub repository.
- **FR-002**: System MUST materialize every project as a working directory and persist a project record so the project can be listed and reopened later, including after an application restart.
- **FR-003**: For a blank project, the chosen working directory MUST be empty or non-existent; if it already exists and is non-empty, the system MUST reject creation with a clear reason and MUST NOT overwrite or adopt the existing content. When the directory is empty or newly created, the system MUST initialize it as an empty git repository (`git init`) with no scaffold content added.
- **FR-004**: For a project created from a GitHub repository, the chosen working directory MUST be empty or non-existent; if it already exists and is non-empty, the system MUST reject creation with a clear reason and MUST NOT overwrite or adopt the existing content. When the directory is empty or newly created, the system MUST clone the specified repository into the project's working directory and record the GitHub origin (the source repository reference) on the project.
- **FR-005**: The system MUST allow the user to sign in with GitHub via an OAuth device flow, and this sign-in MUST be initiable and usable identically from both the CLI (TUI) and the Web UI (Principle IV). A single successful GitHub sign-in MUST grant both (a) access to GitHub repositories sufficient to clone them, including private repositories the signed-in user is permitted to access, and (b) authorization to use the GitHub Copilot provider (Principle II). The system MUST authorize the GitHub Copilot provider via this GitHub sign-in in place of a separately entered API key, and MUST NOT require, prompt for, or store a Copilot-specific API key while a valid GitHub sign-in is present. The GitHub sign-in MUST NOT authorize, satisfy, or substitute for Microsoft Foundry credentials, which remain separate (FR-013, FR-016). Any token, device code, or other secret produced by the sign-in MUST NOT appear in any output, log, or telemetry (Principle IX).
- **FR-006**: The user MUST choose the working directory for each project at creation time; there is no single managed workspace root. The chosen path MUST be recorded on the project record and MUST serve as the sandbox boundary for all of that project's runs (FR-022).
- **FR-007**: System MUST reject creation when the project name is empty or only whitespace, and MUST surface a clear reason when any creation fails without leaving a partially-created or inconsistent project.

#### Listing and the landing page

- **FR-008**: System MUST provide a way to list all existing projects.
- **FR-009**: The landing page MUST present existing projects as cards, each summarizing at least the project's name and its origin (blank or GitHub), and MUST offer entry points to create a new project both blank and from a repository.
- **FR-010**: When no projects exist, the landing page MUST show an empty state that still offers both create actions.
- **FR-011**: Selecting a project MUST open (enter) that project.

#### AI provider settings and defaults

- **FR-012**: Each project MUST store AI provider settings consisting of the project's default provider and a default model for each provider.
- **FR-013**: System MUST offer exactly two AI providers — GitHub Copilot CLI and Microsoft Foundry — wherever a project's provider is selected or configured, and MUST reject any attempt to configure a provider other than these two (Principle II). When GitHub Copilot is the selected provider, the system MUST authorize it via the GitHub sign-in (FR-005) and MUST NOT require or store a separate API key for that provider. Microsoft Foundry MUST use its own provider credentials; the GitHub sign-in MUST NOT authorize Microsoft Foundry.
- **FR-014**: System MUST allow a default model to be selected per provider and MUST store it as that provider's default for the project.
- **FR-015**: A project's stored provider and model MUST act as defaults only; the provider and model MUST remain selectable per run, and a run MUST be able to override the project defaults using one of the two permitted providers.
- **FR-016**: Provider credentials MUST be stored globally / installation-wide and shared across all projects; they MUST NOT be stored on the project record, and they MUST NOT appear in any output, log, or telemetry (Principle IX). For the GitHub Copilot provider, the stored credential is the authorization established by the GitHub sign-in (FR-005); no separate Copilot API key is entered or stored. For Microsoft Foundry, the stored credentials are its own provider credentials, which the GitHub sign-in neither provides nor replaces. Both providers' credentials follow this same global, installation-wide rule and MUST NOT be persisted on any individual project record.

#### Project management (rename, edit, delete)

- **FR-017**: System MUST allow a user to rename an existing project, with the new name reflected in the list and on the project's card.
- **FR-018**: System MUST allow a user to edit a project's AI provider settings after creation, subject to the two-provider constraint in FR-013.
- **FR-019**: System MUST allow a user to delete a project; deletion MUST require explicit user confirmation. If the project has in-flight or active runs at deletion time, the system MUST first cancel those runs to a visible terminal state, and only THEN remove the project record and remove the project from the list (Principle X). The on-disk working directory and any cloned contents MUST always be left intact — no files are deleted from disk.

#### API-first and front-end parity

- **FR-020**: All project capabilities (create blank, create from GitHub repository, list, view and configure settings, rename, delete) MUST be exposed through the authoritative backend API, which is the single source of truth; clients MUST contain no project business logic of their own.
- **FR-021**: Every project capability MUST be reachable identically from both the CLI (TUI) and the Web UI, producing the same results regardless of which client initiated it.

#### Sandbox boundary, privacy, and accountability

- **FR-022**: A project's working directory MUST serve as the boundary for that project's runs: file and process operations performed by a project's runs MUST stay within the project's working directory and MUST be rejected if they attempt to escape it.
- **FR-023**: Credentials or secrets used to clone a private GitHub repository MUST NOT be written to the project record, event logs, client-facing outputs, or telemetry, and MUST NOT be sent to any party beyond what is required to perform the clone.
- **FR-024**: Each project MUST have a named human accountable for it and for the runs started within it. The accountable human MUST be the GitHub-signed-in user when a GitHub sign-in is present (FR-005); otherwise it MUST be the local operating-system/installation user identity. This identity MUST be recorded on the project record for accountability and audit (Principles IX, X).
- **FR-026**: If a project's recorded working directory is missing or inaccessible when the project is listed or opened, the system MUST still list the project but MUST mark it as unavailable, and MUST block all runs for it because the sandbox boundary (FR-022) is invalid. The system MUST offer the user a choice to either relink the project to a new working directory or remove the project record (FR-019). Any files that exist MUST be preserved (consistent with record-only delete).

#### Deployment parity

- **FR-025**: The same build MUST support projects in both the local-developer environment and a hosted-cloud deployment; the local-directory model MUST NOT preclude hosted-cloud execution. In a hosted-cloud deployment, project storage MUST be located at [NEEDS CLARIFICATION: cloud project-storage location when there is no local developer machine].

### Key Entities *(include if feature involves data)*

- **Project**: The top-level container a user works in. Key attributes: a user-facing name (label), a stable internal identifier, an origin (blank or from a GitHub repository), the user-chosen working-directory path (selected at creation time; serves as the run sandbox boundary), AI provider settings (the project's default provider and a default model per provider; provider credentials are not stored here — they are global), the accountable human owner, and creation/update metadata.
- **Project origin**: Discriminates how a project was created — "blank" or "from GitHub repository". For the GitHub case it holds the source repository reference.
- **AI provider settings**: A project's default provider (one of the two permitted) together with a default model for each provider. For the GitHub Copilot provider, authorization comes from the GitHub sign-in (FR-005); no provider-specific API key is entered or stored. Microsoft Foundry uses its own provider credentials, which the GitHub sign-in does not provide.
- **Permitted provider**: The closed set of exactly two model sources — GitHub Copilot CLI and Microsoft Foundry. No other provider is valid anywhere a provider is selected. GitHub Copilot is authorized via the GitHub sign-in (no stored key); Microsoft Foundry uses its own credentials and is never authorized by the GitHub sign-in.
- **Project gallery (landing view)**: The collection of projects presented as cards, with entry points to create a new project blank or from a repository, including an empty state.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can create a blank project and see it in the project list in 100% of attempts, completing the create flow in a small number of steps (for example under one minute).
- **SC-002**: The landing page displays one card per existing project and exposes both create entry points (blank and from repository) in 100% of loads, and a newly created project appears in the list within a few seconds.
- **SC-003**: Exactly two AI providers (GitHub Copilot CLI and Microsoft Foundry) are offered everywhere a provider is selectable, and any attempt to configure a different provider is rejected, in 100% of cases.
- **SC-004**: A default model can be set per provider and a default provider chosen; those defaults are presented as the pre-selected choice when a run is started in the project, while remaining overridable per run, in 100% of runs started in the project.
- **SC-005**: Creating a project from a GitHub repository results in the repository contents being present in the project's working directory in 100% of successful clones; clone failures surface a clear reason and leave no inconsistent project.
- **SC-006**: No credential or secret used to clone a private repository appears in any output, log, or telemetry — zero occurrences across all clones.
- **SC-007**: Every project capability (create blank, create from repository, list, configure, rename, delete) is performable from both the CLI and the Web UI through the API, verified by exercising each capability from each client with identical results.
- **SC-008**: Operations by a project's runs that attempt to act outside the project's working directory are rejected (not merely warned) in 100% of attempts.
- **SC-009**: Created projects and their configured defaults persist across application restarts, so 100% of created projects remain listed and retain their settings after a restart.
- **SC-010**: With a valid GitHub sign-in present (FR-005), a user can use the GitHub Copilot provider with zero prompts for a Copilot-specific API key in 100% of attempts; and that same GitHub sign-in alone never authorizes Microsoft Foundry — a Foundry run still requires Foundry's own credentials in 100% of attempts.

## Assumptions

- Projects build on the run and sandbox model established in features 001 (single-agent run) and 002 (sandboxed execution): a project supplies the working directory and the provider/model defaults that runs use, and a project's directory bounds its runs.
- The two permitted providers and their per-run selection already exist in the platform (feature 001) and are reused; this feature adds project-level defaults layered over that existing selection.
- "AI Provider settings" for this feature means the project's default provider and a default model per provider; deeper provider-specific tuning beyond the default model is out of scope for now.
- Project names are user-facing labels and need not be globally unique; each project has a stable internal identifier used to address it.
- A single named human owns and is accountable for each project; multi-user sharing and per-project access control are not part of this phase.
- Only GitHub is supported as an external repository source for "create from repo"; other hosts and arbitrary git URLs are out of scope.
- The local-developer environment is the primary target for this phase; hosted-cloud behavior must remain possible, but its storage specifics are pending clarification (FR-025).

## Dependencies

- The authoritative backend API hosts all project capabilities; the CLI (TUI) and Web UI are thin clients over it.
- The model-source capability from feature 001 (the two permitted providers and per-run selection) is reused to provide and enforce project-level provider/model defaults.
- The sandbox / working-directory model from features 001 and 002 governs how a project's directory bounds the file and process operations of its runs.
- Access to GitHub and the ability to clone repositories, including an OAuth device flow for authenticating private repository clones (FR-005).
- The shared governance/runtime layer enforces the permitted-provider allowlist and the project-directory sandbox boundary so the rules are not reimplemented in client code.

## Out of Scope

- The mechanics of running agents inside a project (covered by features 001 and 002); this feature provides the project context, not the run loop or its streaming.
- Multi-user collaboration, sharing, or per-project access control.
- Repository sources other than GitHub, and importing a pre-existing local folder that was not created blank or cloned through these flows.
- Provider configuration beyond choosing the project's default provider and a default model per provider.
- Project templates or scaffolds beyond the blank and from-GitHub-repository options.
