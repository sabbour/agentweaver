# Feature Specification: Blueprints (Reusable, Versioned Project Definitions)

**Feature Branch**: `012-blueprint`

**Created**: 2026-06-22

**Status**: Draft

**Input**: User description: "Author a feature specification for a new first-class construct called a Blueprint. A Blueprint is a higher-level, reusable, versioned, shippable definition that is instantiated into a Project (a Project is an instance of a Blueprint). It generalizes and absorbs today's 'team template' concept (which currently only captures the cast). Predefined Blueprints ship with the product; users can author or fork their own. A Blueprint CONTAINS: (1) Cast as ROLE definitions only — role name, responsibilities, optional model preference — NOT concrete character/universe persona names, which are allocated by the existing casting algorithm at instantiation time; (2) one or more Workflows (the YAML workflows from Feature 010) with a designated default; (3) Policies — the Review Policies and the Sandbox Policy from Feature 010 / YamlSandboxPolicyStore — composed/referenced, not re-specified. A Blueprint EXCLUDES Ceremonies and Seed memories. The same Sync pattern used by the Team page and the Workflows page (Feature 010) applies to Blueprints."

## Overview

Today the closest thing Agentweaver has to a reusable starting point for a project is the **team template** — a catalog entry, loaded by `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`, that captures only the **cast** (the team composition) and nothing else. Everything else a project needs to run — its workflow(s), its review behavior, its sandbox boundaries — is either hardcoded (the per-run graph in `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs`), introduced as a separate per-project concern (the Review Policies and YAML workflows of Feature 010), or stored as a project-local file (the sandbox policy in `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs`). There is no single, shippable, versioned object that says "this is what a backend-service team is" or "this is what a docs team is" as a complete, reusable package.

This feature introduces the **Blueprint**: a higher-level, reusable, versioned, shippable definition that is **instantiated into a Project**. The relationship is deliberately simple and explicit: **a Project is an instance of a Blueprint.** A Blueprint generalizes and **absorbs** today's team-template concept — a team template becomes the cast facet of a Blueprint — and extends it to carry the rest of what defines how a team works: its workflows and its governing policies. Predefined Blueprints ship with the product (for example, a backend-service team Blueprint and a docs team Blueprint); users can author their own or **fork** a predefined one.

A Blueprint is **universe-agnostic**. Its cast is expressed as **role definitions only** — role name, responsibilities, and (optionally) a per-role model preference — and explicitly does **not** carry the concrete character/universe persona names. Those named agents are produced by the **existing casting algorithm at instantiation time**: when a Blueprint is instantiated into a Project, casting (`apps/Agentweaver.Api/Casting/CastingService.cs`) assigns universe names to the Blueprint's roles, turning abstract roles into named cast members. This separation is the heart of the model: **Blueprint = roles; instantiation = casting roles into named agents.**

A Blueprint **composes** rather than re-specifies the constructs other features own. Its workflows are the YAML workflows defined in **Feature 010** (`specs/010-yaml-workflows-review-policies`); the Blueprint references one or more of them and designates a **default**, and the materialization of that default workflow into a project's `.scaffolders/workflows/` at instantiation is already specified by Feature 010. Its policies are the **Review Policies** (RAI / Rubber-duck / Human-review) and the **Sandbox Policy** (shell / network / destructive-command gating) from Feature 010 and `YamlSandboxPolicyStore`; the Blueprint references them, it does not redefine their internals. A Blueprint explicitly **excludes Ceremonies** (out of scope for now) and **Seed memories** (a Blueprint is not a memory seed).

Consistent with the constitution, a Blueprint is an **API-first** construct: authoring, listing, reading, forking, instantiating, and syncing Blueprints are server-side capabilities with no business logic in any client (Principles III). Every Blueprint read/list/sync capability MUST be reachable from the **MCP server at parity** with the Web UI (Principle IV), and a **Blueprints management page** with a **Sync** affordance — mirroring the existing Team page and the Feature 010 Workflows page — surfaces them in the web app. The runtime/governance layer (Microsoft Agent Framework, .NET 10) remains the enforcement point: a Blueprint cannot weaken sandbox boundaries, the human-approval gate for irreversible actions, or the audit trail (Principles X, XI). Every run remains attributable to an accountable human (Principle IX). No shipped surface contains emojis, and no part of the implementation uses mocks, fakes, or placeholders (Principles VII, VIII).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse and instantiate a predefined Blueprint into a Project (Priority: P1)

A user opens the product and browses the catalog of **predefined Blueprints** that ship with it — for example a backend-service team Blueprint and a docs team Blueprint. Each Blueprint advertises what it is: its cast (as roles), its workflow(s) and which is default, and its governing policies. The user picks one and **instantiates** it. The system creates a **Project** that is an instance of that Blueprint: it runs the **casting algorithm over the Blueprint's roles** to produce named cast members, materializes the Blueprint's **default workflow** into the project's `.scaffolders/workflows/` (per Feature 010), and applies the Blueprint's **Review Policy and Sandbox Policy** to the project. The resulting project is immediately ready to run.

**Why this priority**: This is the core value of the feature and the lowest-risk slice — it proves the central relationship (a Project is an instance of a Blueprint) end to end using machinery that already exists (casting, project creation, workflow/policy materialization). Until a Blueprint can be instantiated into a working project, nothing else in the feature delivers value.

**Independent Test**: From the Blueprints catalog, select a predefined Blueprint and instantiate it; verify a new project is created whose cast is the result of casting the Blueprint's roles (named agents, one per role), whose effective default workflow is the Blueprint's default workflow materialized into `.scaffolders/workflows/`, and whose Review Policy and Sandbox Policy match the Blueprint's. Confirm the project can execute a run.

**Acceptance Scenarios**:

1. **Given** a predefined Blueprint in the catalog, **When** the user instantiates it, **Then** a new Project is created and recorded as an instance of that Blueprint (the project tracks which Blueprint, and which Blueprint version, it came from).
2. **Given** a Blueprint whose cast defines N roles, **When** it is instantiated, **Then** the casting algorithm (`CastingService`) runs over those N roles and the project's cast contains N named agents, one per role, with the universe/persona names allocated at instantiation (not stored in the Blueprint).
3. **Given** a Blueprint with one or more workflows and a designated default, **When** it is instantiated, **Then** the default workflow is materialized into the project's `.scaffolders/workflows/` per Feature 010 and becomes the project's effective default workflow.
4. **Given** a Blueprint that references a Review Policy and a Sandbox Policy, **When** it is instantiated, **Then** the project is bound to those policies (Review Policy per Feature 010; Sandbox Policy per the existing `YamlSandboxPolicyStore` model).
5. **Given** a Blueprint that defines no role with a model preference, **When** it is instantiated, **Then** the project still casts successfully using the system's default model selection (Principle II), with no Blueprint-level failure.

---

### User Story 2 - Author a new Blueprint or fork an existing one (Priority: P1)

A user wants a reusable definition the product does not ship out of the box — for example a "platform SRE team" Blueprint. They **author** a new Blueprint by defining its cast as **roles** (role name, responsibilities, optional model preference), choosing one or more **workflows** and designating a default, and referencing the **Review Policy** and **Sandbox Policy** the Blueprint should carry. Alternatively, they **fork** an existing predefined Blueprint and adjust it (add a role, swap the default workflow, tighten the sandbox policy) to produce their own. The authored or forked Blueprint becomes available in the catalog for instantiation, exactly like a predefined one.

**Why this priority**: Authoring and forking are what make Blueprints a reusable, user-owned construct rather than a fixed set of presets. They are P1 because the feature's premise — that users can capture and reshare "how a team works" — depends on it; a catalog of only built-in Blueprints would be a much smaller feature.

**Independent Test**: Author a Blueprint by specifying roles, workflows (with a default), and referenced policies; confirm it validates and appears in the catalog as instantiable. Separately, fork a predefined Blueprint, change at least one facet (a role, the default workflow, or a policy reference), and confirm the fork is an independent Blueprint that does not alter the original and can itself be instantiated.

**Acceptance Scenarios**:

1. **Given** the authoring surface, **When** a user defines a Blueprint with at least one role, at least one workflow, a designated default workflow, and referenced policies, **Then** the Blueprint validates and becomes available in the catalog as instantiable.
2. **Given** a Blueprint whose cast omits concrete persona/universe names (roles only), **When** it is authored, **Then** validation accepts it; **and** a Blueprint that attempts to hardcode concrete persona names instead of roles MUST be rejected with a clear message (persona names are allocated by casting at instantiation, not stored in the Blueprint).
3. **Given** a Blueprint that designates more than one workflow, **When** it is authored, **Then** exactly one MUST be marked default; zero or multiple defaults MUST fail validation.
4. **Given** a predefined Blueprint, **When** a user forks it and changes a facet, **Then** the fork is an independent Blueprint, the original is unchanged, and both are independently instantiable.
5. **Given** a Blueprint that references a Review Policy or Sandbox Policy or workflow that cannot be resolved, **When** it is validated, **Then** validation fails with a specific, actionable message naming the unresolved reference, and the Blueprint is not made instantiable.

---

### User Story 3 - Sync a Blueprint and re-materialize it (Priority: P2)

The set of available Blueprints, and the contents of an individual Blueprint, can change — a predefined Blueprint ships an update, or a user edits an authored Blueprint's source. A user opens the **Blueprints management page** and triggers a **Sync** — the same affordance the Team page and the Feature 010 Workflows page expose — which re-reads the Blueprint source and refreshes the loaded/available set, reporting validation status per Blueprint. Syncing mirrors the established load-on-start/explicit-sync model: it is not file-watch and not per-heartbeat.

**Why this priority**: Sync keeps the Blueprint catalog consistent with its source and matches the interaction model users already know from Team and Workflows. It is P2 because instantiation (US1) and authoring (US2) deliver the primary value first; Sync is the maintenance/refresh layer on top.

**Independent Test**: With the Blueprints page open, change a Blueprint's source (edit an authored Blueprint, or introduce a new/updated predefined Blueprint), trigger Sync, and confirm the available set and each Blueprint's validation status refresh accordingly; confirm an invalid Blueprint is reported with a clear error and excluded without taking down the rest. Confirm the same list/sync is reachable from the MCP server.

**Acceptance Scenarios**:

1. **Given** the Blueprints management page, **When** the user triggers Sync, **Then** the Blueprint source is re-read and the available set plus per-Blueprint validation status are refreshed, following the existing Team/Workflows Sync pattern (load-on-start / explicit-sync, not file-watch, not per-heartbeat).
2. **Given** a Blueprint that became invalid (an unresolved policy or workflow reference, a missing/duplicate default workflow, or hardcoded persona names), **When** Sync runs, **Then** that Blueprint is reported with a clear, actionable error and excluded, while the remaining valid Blueprints stay available.
3. **Given** both clients, **When** the available Blueprints (with validation status) are listed from the MCP server and from the Web UI, **Then** both present the same set with the same status.
4. **Given** an in-flight instantiation, **When** a Sync changes a Blueprint mid-operation, **Then** the in-flight instantiation completes on the Blueprint definition it started with (consistent with Feature 010's "in-flight unaffected by later Sync" rule).

---

### User Story 4 - Today's team template is a Blueprint (Priority: P1)

A user who relied on the existing catalog **team templates** (cast-only) continues to get at least the same result, because each team template now surfaces **as the cast facet of a Blueprint**. Instantiating that Blueprint produces a project whose cast is identical to what the team template would have produced (same roles, casting allocates the names), and additionally carries the Blueprint's default workflow and policies. No prior capability is lost; the team-template concept is generalized, not removed.

**Why this priority**: This is the migration/compatibility guarantee that makes the generalization safe. It is P1 because the Blueprint explicitly "absorbs" the team-template concept; if existing templates did not map cleanly onto Blueprints, the feature would be a breaking change rather than a generalization.

**Independent Test**: Take an existing catalog team template, confirm it is represented as the cast (roles) of a corresponding Blueprint, instantiate that Blueprint, and verify the resulting project's cast matches what the team template produced today (same roles → named via casting), now accompanied by the Blueprint's default workflow and policies.

**Acceptance Scenarios**:

1. **Given** an existing catalog team template, **When** Blueprints are loaded, **Then** the template is represented as the cast (role definitions) of a Blueprint, preserving its roles and responsibilities.
2. **Given** such a Blueprint, **When** it is instantiated, **Then** the project's cast matches what the team template produced today (the same roles, with persona names allocated by casting at instantiation).
3. **Given** the generalization, **When** a user instantiates the Blueprint corresponding to a former team template, **Then** the project additionally receives the Blueprint's default workflow and policies, with no loss of the prior cast behavior.

---

### Edge Cases

- **Empty cast**: A Blueprint with zero roles MUST fail validation (there is nothing to cast); it MUST NOT produce an empty, unrunnable project.
- **Default workflow ambiguity**: A Blueprint declaring multiple workflows with zero or more than one marked default MUST fail validation (exactly one default required).
- **Unresolved references**: A Blueprint referencing a workflow, Review Policy, or Sandbox Policy that cannot be resolved at validation or instantiation time MUST fail with a specific, file/reference-scoped message and MUST NOT silently instantiate a partial project.
- **Persona leakage**: A Blueprint that attempts to encode concrete universe/persona names (rather than abstract roles) MUST be rejected — persona allocation is the casting algorithm's responsibility at instantiation only.
- **Excluded facets present**: A Blueprint source that attempts to declare Ceremonies or Seed memories MUST have those facets ignored or rejected (they are explicitly out of scope), without partially honoring them.
- **Instantiation casting failure**: If casting over the Blueprint's roles cannot complete (e.g., name-allocation exhaustion), instantiation MUST fail cleanly without leaving a half-created project.
- **Sync during instantiation**: An instantiation already in progress when a Sync changes the Blueprint MUST complete on the definition it started with.
- **Predefined Blueprint edit**: A user MUST NOT be able to mutate a shipped predefined Blueprint in place; changes happen via fork (US2).

## Requirements *(mandatory)*

### Functional Requirements

**Blueprint as a first-class construct**

- **FR-001**: The system MUST define a **Blueprint** as a first-class, reusable, versioned, shippable definition that is **instantiated into a Project**, such that a Project is recorded as an **instance of a Blueprint**.
- **FR-002**: A Blueprint MUST contain exactly three facets: a **Cast** expressed as role definitions, one or more **Workflows** with a designated default, and **Policies** (a Review Policy reference and a Sandbox Policy reference). It MUST NOT contain Ceremonies or Seed memories.
- **FR-003**: The Blueprint construct MUST **generalize and absorb** today's team-template concept (the cast-only catalog templates loaded by `CatalogReader.cs`): an existing team template MUST be representable as the cast facet of a Blueprint, with no loss of its roles or responsibilities (US4).
- **FR-004**: The product MUST ship a catalog of **predefined Blueprints** (for example a backend-service team Blueprint and a docs team Blueprint) that are instantiable out of the box.
- **FR-005**: Users MUST be able to **author** their own Blueprints and to **fork** an existing (predefined or authored) Blueprint into an independent Blueprint; forking MUST NOT mutate the source Blueprint, and predefined Blueprints MUST NOT be editable in place.

**Cast — roles only, casting at instantiation**

- **FR-006**: A Blueprint's **Cast** MUST be expressed as **role definitions only** — each role carries a role name, responsibilities, and an OPTIONAL per-role model preference — and MUST NOT carry concrete character/universe persona names.
- **FR-007**: At **instantiation**, the system MUST run the existing **casting algorithm** (`apps/Agentweaver.Api/Casting/CastingService.cs`) over the Blueprint's roles to allocate universe/persona names, producing one named cast member per role; the Blueprint itself remains universe-agnostic.
- **FR-008**: A Blueprint that hardcodes concrete persona/universe names instead of abstract roles MUST fail validation with a clear message (persona allocation is the casting algorithm's responsibility at instantiation, FR-007).
- **FR-009**: A Blueprint with zero roles MUST fail validation and MUST NOT be instantiable.

**Workflows — compose Feature 010**

- **FR-010**: A Blueprint MUST reference **one or more Workflows** — the YAML workflows defined in Feature 010 (`specs/010-yaml-workflows-review-policies`) — and MUST designate exactly **one default** workflow; zero or multiple defaults MUST fail validation.
- **FR-011**: At instantiation, the Blueprint's **default workflow** MUST be materialized into the project's `.scaffolders/workflows/` per Feature 010 (the in-code DefaultWorkflowTemplate / materialization path), becoming the project's effective default workflow.
- **FR-012**: The Blueprint MUST **compose/reference** workflow definitions rather than re-specify their internals; workflow schema, semantics, triggers, and validation remain owned by Feature 010.

**Policies — compose Review and Sandbox policies**

- **FR-013**: A Blueprint MUST reference a **Review Policy** (the RAI / Rubber-duck / Human-review composition from Feature 010), and at instantiation the project MUST be bound to that Review Policy (by name, per Feature 010's policy-binding model).
- **FR-014**: A Blueprint MUST reference a **Sandbox Policy** (the shell / network / destructive-command gating modeled by the existing `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs`), and at instantiation the project MUST be bound to that Sandbox Policy.
- **FR-015**: The Blueprint MUST **compose/reference** these policies rather than re-specify their internals; the Review Policy and Sandbox Policy definitions, semantics, and enforcement remain owned by Feature 010 and the existing sandbox-policy store/runtime.
- **FR-016**: A Blueprint referencing a workflow, Review Policy, or Sandbox Policy that cannot be resolved MUST fail validation (or instantiation) with a specific, actionable, reference-scoped message and MUST NOT produce a partial project.

**Instantiation — a Project is an instance of a Blueprint**

- **FR-017**: Instantiating a Blueprint MUST create a **Project** via the existing project-creation path (`apps/Agentweaver.Api/Projects/ProjectService.cs`, e.g. `CreateBlankAsync` / `CreateFromGitHubAsync`) whose cast is the casting result over the Blueprint's roles (FR-007), whose default workflow and policies are materialized/bound per FR-011, FR-013, FR-014.
- **FR-018**: The created Project MUST record the **identity and version** of the Blueprint it was instantiated from, so the project's provenance is traceable.
- **FR-019**: If casting (FR-007) or any reference resolution (FR-016) fails during instantiation, the instantiation MUST fail cleanly without leaving a half-created project.
- **FR-020**: Ceremonies and Seed memories MUST NOT be produced by Blueprint instantiation; if a Blueprint source declares them, those facets MUST be ignored/rejected rather than partially honored.

**Discovery, Sync & management surface**

- **FR-021**: The Web UI MUST provide a **Blueprints management page** that lists available Blueprints (predefined and authored) with their validation status, supports instantiation, and provides a **Sync** action — modeled on the existing Team page and the Feature 010 Workflows page Sync affordance.
- **FR-022**: Blueprint **Sync** MUST follow the established load-on-start / explicit-sync model (NOT file-watch, NOT per coordinator heartbeat): Sync re-reads the Blueprint source and refreshes the available set and per-Blueprint validation status; an in-flight instantiation MUST complete on the definition it started with.
- **FR-023**: On an invalid Blueprint (unresolved reference, missing/duplicate default workflow, zero roles, hardcoded persona names), the system MUST surface a clear, Blueprint-scoped error and exclude that Blueprint without affecting the remaining valid Blueprints.

**Governance, safety & parity (cross-cutting)**

- **FR-024**: Listing, reading, forking, instantiating, and syncing Blueprints MUST be reachable identically from the **MCP server and the Web UI**, with all validation, casting, materialization, and policy resolution performed server-side and no business logic in either client (Principles III, IV).
- **FR-025**: A Blueprint MUST NOT be able to weaken sandbox boundaries, step/time limits, the human-approval-for-irreversible-action gate, or the audit trail; these remain enforced by the runtime/governance layer (Microsoft Agent Framework, .NET 10) regardless of Blueprint content (Principles X, XI). A Blueprint's referenced Sandbox Policy MAY only constrain within those guarantees, never relax them.
- **FR-026**: Every run of a project instantiated from a Blueprint MUST remain attributable to an accountable human (Principle IX); Blueprints do not remove human accountability or the human-approval gate for irreversible actions.
- **FR-027**: The model provider MUST remain GitHub Copilot (Principle II); a Blueprint's optional per-role model preference MAY influence model selection within the allowed provider but MUST NOT select a different provider. [NEEDS CLARIFICATION: see Open Questions — whether model defaults belong at Blueprint level at all.]
- **FR-028**: No shipped surface produced by this feature (Blueprint definitions, catalog listings, validation messages, logs, UI) may contain emojis (Principle VIII), and no part of the implementation may use mocks, fakes, stubs, or placeholders (Principle VII).

**Tentative / under clarification (see Open Questions)**

- **FR-029**: A Blueprint MAY OPTIONALLY associate a set of **skills and tool grants per role**, materialized to the role's named agent at instantiation. [NEEDS CLARIFICATION: should each role in the cast carry skills and tool grants, and if so how are they defined and materialized — referenced like workflows/policies, inlined, or granted by the runtime? This facet is "maybe" per the requester and is not confirmed in scope.]
- **FR-030**: Blueprint **versioning** semantics — how versions are numbered/identified, how a Project tracks the Blueprint version it came from, and what **Sync does when the Blueprint changes after instantiation** (drift detection / re-materialization / leave-existing-projects-untouched) — MUST be defined. [NEEDS CLARIFICATION: versioning and post-instantiation drift handling are unspecified; FR-018 records provenance but the drift/re-sync behavior for already-created projects is an open decision.]
- **FR-031**: Blueprint **storage and discovery** — whether Blueprints are stored as files (e.g. under `.scaffolders/`) and/or via an API + store, and how the catalog is discovered. [NEEDS CLARIFICATION: file-based vs. API-stored vs. both (mirroring Feature 010's Review Policy dual-source model), and where predefined vs. authored Blueprints live.]
- **FR-032**: Whether a Project can **switch Blueprints** after creation or **compose multiple** Blueprints. [NEEDS CLARIFICATION: this spec assumes a single source Blueprint per project (FR-001/FR-018); switching or multi-Blueprint composition is an open decision.]
- **FR-033**: The **scope of the predefined-Blueprint catalog for v1** — which Blueprints ship (beyond the example backend-service and docs teams) and the minimum viable catalog. [NEEDS CLARIFICATION: v1 catalog scope not fixed.]

### Key Entities *(include if feature involves data)*

- **Blueprint**: A first-class, reusable, versioned, shippable definition that is instantiated into a Project (a Project is an instance of a Blueprint). Generalizes and absorbs today's team template. Contains exactly three facets — **Cast** (roles), **Workflows** (one or more, with a designated default), and **Policies** (a Review Policy reference and a Sandbox Policy reference) — and excludes Ceremonies and Seed memories. May be predefined (shipped) or authored/forked by a user. Universe-agnostic: it never stores concrete persona names. Optional, under clarification: per-role skills/tool grants (FR-029), versioning/drift semantics (FR-030), storage/discovery model (FR-031).
- **Cast Role**: An abstract member of a Blueprint's cast: a role name, responsibilities, and an OPTIONAL per-role model preference. Carries no persona/universe name. At instantiation, the casting algorithm (`CastingService`) allocates a universe name to each role to produce a named cast member. The unit that absorbs today's team-template composition.
- **Workflow (referenced)**: A Feature 010 YAML workflow that a Blueprint references; the Blueprint designates one as **default**. Composed/referenced by the Blueprint, not redefined. The default is materialized into the project's `.scaffolders/workflows/` at instantiation.
- **Review Policy (referenced)**: The Feature 010 per-project Review Policy (RAI / Rubber-duck / Human-review) that a Blueprint references by name; bound to the project at instantiation. Composed/referenced, not redefined.
- **Sandbox Policy (referenced)**: The shell / network / destructive-command gating policy modeled by the existing `YamlSandboxPolicyStore`, referenced by a Blueprint and bound to the project at instantiation. Composed/referenced, not redefined; may only constrain within the runtime's mandatory guarantees, never relax them.
- **Project (instance)**: The Feature 003 project container, now understood as an **instance of a Blueprint**. Its cast is the casting result over the Blueprint's roles; its default workflow and policies are materialized/bound from the Blueprint. Records the identity and version of the source Blueprint for provenance.
- **Blueprints Management Page / Sync**: A Web UI surface (modeled on the existing Team page and the Feature 010 Workflows page) that lists available Blueprints with validation status, supports instantiation, and exposes a **Sync** action to re-read the Blueprint source. The same list/sync/read capability is exposed via the MCP server at parity.
- **Validation Result**: The per-Blueprint outcome of discovery and validation (valid / invalid with a specific, actionable, Blueprint-scoped message — e.g., unresolved reference, missing or duplicate default workflow, zero roles, hardcoded persona names), surfaced identically to both clients.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Instantiating any predefined Blueprint produces a runnable project whose cast has exactly one named agent per Blueprint role, whose effective default workflow is the Blueprint's default, and whose Review and Sandbox policies match the Blueprint — verified for 100% of shipped predefined Blueprints.
- **SC-002**: For every existing catalog team template, the project produced by instantiating its corresponding Blueprint has the same cast (same roles, names allocated by casting) the team template produced before this feature — 0 regressions in cast composition.
- **SC-003**: A user can author a new Blueprint and fork an existing one, and both become instantiable, with the fork leaving the original unchanged — demonstrated end to end without any source-code change.
- **SC-004**: Every invalid Blueprint case tested (zero roles, missing/duplicate default workflow, unresolved policy/workflow reference, hardcoded persona names) is reported with a clear Blueprint-scoped error and excluded, without affecting other valid Blueprints — 100% of cases.
- **SC-005**: No Blueprint can cause a run to execute with weaker safety than the runtime guarantees: in 100% of tested instantiations, RAI content-safety failure and human approval for irreversible actions still apply, and the referenced Sandbox Policy never relaxes a mandatory boundary.
- **SC-006**: Every Blueprint capability in scope (list, read, fork, instantiate, sync) is performed successfully and yields identical resulting state from both the MCP server and the Web UI — 0 capabilities reachable from only one client.
- **SC-007**: A Sync that changes the Blueprint source refreshes the available set and per-Blueprint validation status, while any instantiation already in flight completes on the definition it started with — verified across the tested edit/sync cases.

## Assumptions

- "Team template" refers to the existing cast-only catalog templates loaded by `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`; this feature generalizes them into the cast facet of a Blueprint without removing their behavior (US4, FR-003).
- The casting algorithm referenced for instantiation is the existing `apps/Agentweaver.Api/Casting/CastingService.cs` (which proposes/assigns universe persona names to roles); this feature reuses it at instantiation and does not redefine how casting allocates names.
- Project creation reuses the existing `apps/Agentweaver.Api/Projects/ProjectService.cs` path (`CreateBlankAsync` / `CreateFromGitHubAsync`), which already materializes project state; instantiation extends this path to drive casting from Blueprint roles and to materialize the Blueprint's default workflow and policies.
- Workflows, Review Policies, and the materialization of a default workflow into `.scaffolders/workflows/` are owned by Feature 010 (`specs/010-yaml-workflows-review-policies`); this Blueprint feature composes/references them and does not re-specify their internals.
- The Sandbox Policy is the existing model behind `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs` (shell / network / destructive-command gating); the Blueprint references it rather than redefining it.
- The Sync interaction model mirrors the existing Team page and the Feature 010 Workflows page (load-on-start / explicit Sync, not file-watch, not per-heartbeat), including the Feature 011 web-app-shell navigation conventions for where such a management page lives.
- Ceremonies and Seed memories are explicitly out of scope for a Blueprint (locked decisions), and a Blueprint is explicitly not a memory seed.
- The model provider remains fixed to GitHub Copilot (Principle II); any per-role model preference is a within-provider influence at most and is itself under clarification (FR-027).
- "Project" is the Feature 003 project container; provenance (which Blueprint/version a project came from) is new state introduced by this feature.

## Open Questions (for the clarify pass)

The following are genuine ambiguities flagged with [NEEDS CLARIFICATION] markers in the requirements above. They are NOT resolved in this draft and should be addressed in `/speckit.clarify` before planning:

1. **Per-role skills & tools (FR-029)**: Should each cast role carry a set of skills and tool grants, and if so how are they defined and materialized (referenced, inlined, runtime-granted)? Marked "maybe" by the requester; not confirmed in scope.
2. **Model defaults level (FR-027)**: Do provider/model defaults belong at the Blueprint level (per role) at all, or should they stay strictly project-level?
3. **Versioning & drift (FR-030)**: How do Blueprint versions work, how does a Project track the Blueprint version it came from, and what does Sync do when a Blueprint changes after instantiation (drift detection, re-materialization, or leave existing projects untouched)?
4. **Storage & discovery (FR-031)**: Are Blueprints stored as files (e.g. under `.scaffolders/`), via an API + store, or both (mirroring Feature 010's dual-source Review Policy model)? Where do predefined vs. authored Blueprints live, and how is the catalog discovered?
5. **Switch / compose (FR-032)**: Can a Project switch its Blueprint after creation, or compose multiple Blueprints? This draft assumes a single source Blueprint per project.
6. **v1 catalog scope (FR-033)**: Which predefined Blueprints ship in v1 beyond the example backend-service and docs team Blueprints, and what is the minimum viable catalog?
