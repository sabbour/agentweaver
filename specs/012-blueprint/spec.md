# Feature Specification: Blueprints (Reusable, Versioned Project Definitions)

**Feature Branch**: `012-blueprint`

**Created**: 2026-06-22

**Status**: Draft

**Input**: User description: "Author a feature specification for a new first-class construct called a Blueprint. A Blueprint is a higher-level, reusable, versioned, shippable definition that is instantiated into a Project (a Project is an instance of a Blueprint). It generalizes and absorbs today's 'team template' concept (which currently only captures the cast). Predefined Blueprints ship with the product; users can author or fork their own. A Blueprint CONTAINS: (1) Cast as ROLE definitions only — role name, responsibilities, optional model preference — NOT concrete character/universe persona names, which are allocated by the existing casting algorithm at instantiation time; (2) one or more Workflows (the YAML workflows from Feature 010) with a designated default; (3) Policies — the Review Policies and the Sandbox Policy from Feature 010 / YamlSandboxPolicyStore — composed/referenced, not re-specified. A Blueprint EXCLUDES Ceremonies and Seed memories. The same Sync pattern used by the Team page and the Workflows page (Feature 010) applies to Blueprints."

## Overview

Today the closest thing Agentweaver has to a reusable starting point for a project is the **team template** — a catalog entry, loaded by `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`, that captures only the **cast** (the team composition) and nothing else. Everything else a project needs to run — its workflow(s), its review behavior, its sandbox boundaries — is either hardcoded (the per-run graph in `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs`), introduced as a separate per-project concern (the Review Policies and YAML workflows of Feature 010), or stored as a project-local file (the sandbox policy in `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs`). There is no single, shippable, versioned object that says "this is what a backend-service team is" or "this is what a docs team is" as a complete, reusable package.

This feature introduces the **Blueprint**: a higher-level, reusable, versioned, shippable definition that is **instantiated into a Project**. The relationship is deliberately simple and explicit: **a Project is an instance of a Blueprint.** A Blueprint generalizes and **absorbs** today's team-template concept — a team template becomes the cast facet of a Blueprint — and extends it to carry the rest of what defines how a team works: its workflows and its governing policies. Predefined Blueprints ship with the product (v1 ships four: Content authoring, Product management, Software Development, and Product & Software Delivery); users can author their own or **fork** a predefined one.

A Blueprint is **universe-agnostic**. Its cast is expressed as **role definitions only** — role name, responsibilities, and (optionally) a per-role model preference — and explicitly does **not** carry the concrete character/universe persona names. Those named agents are produced by the **existing casting algorithm at instantiation time**: when a Blueprint is instantiated into a Project, casting (`apps/Agentweaver.Api/Casting/CastingService.cs`) assigns universe names to the Blueprint's roles, turning abstract roles into named cast members. This separation is the heart of the model: **Blueprint = roles; instantiation = casting roles into named agents.**

A Blueprint **composes** rather than re-specifies the constructs other features own. Its workflows are the YAML workflows defined in **Feature 010** (`specs/010-yaml-workflows-review-policies`); the Blueprint references one or more of them and designates a **default**, and the materialization of that default workflow into a project's `.agentweaver/workflows/` at instantiation is already specified by Feature 010. Its policies are the **Review Policies** (RAI / Rubber-duck / Human-review) and the **Sandbox Policy** (shell / network / destructive-command gating) from Feature 010 and `YamlSandboxPolicyStore`; the Blueprint references them, it does not redefine their internals. A Blueprint explicitly **excludes Ceremonies** (out of scope for now) and **Seed memories** (a Blueprint is not a memory seed).

Consistent with the constitution, a Blueprint is an **API-first** construct: authoring, listing, reading, forking, instantiating, and syncing Blueprints are server-side capabilities with no business logic in any client (Principles III). Every Blueprint read/list/sync capability MUST be reachable from the **MCP server at parity** with the Web UI (Principle IV), and a **Blueprints management page** with a **Sync** affordance — mirroring the existing Team page and the Feature 010 Workflows page — surfaces them in the web app. The runtime/governance layer (Microsoft Agent Framework, .NET 10) remains the enforcement point: a Blueprint cannot weaken sandbox boundaries, the human-approval gate for irreversible actions, or the audit trail (Principles X, XI). Every run remains attributable to an accountable human (Principle IX). No shipped surface contains emojis, and no part of the implementation uses mocks, fakes, or placeholders (Principles VII, VIII).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse and instantiate a predefined Blueprint into a Project (Priority: P1)

A user opens the product and browses the catalog of **predefined Blueprints** that ship with it — in v1 the Content authoring, Product management, Software Development, and Product & Software Delivery Blueprints. Each Blueprint advertises what it is: its cast (as roles), its workflow(s) and which is default, and its governing policies. The user picks one and **instantiates** it. The system creates a **Project** that is an instance of that Blueprint: it runs the **casting algorithm over the Blueprint's roles** to produce named cast members, materializes the Blueprint's **default workflow** into the project's `.agentweaver/workflows/` (per Feature 010), and applies the Blueprint's **Review Policy and Sandbox Policy** to the project. The resulting project is immediately ready to run.

**Why this priority**: This is the core value of the feature and the lowest-risk slice — it proves the central relationship (a Project is an instance of a Blueprint) end to end using machinery that already exists (casting, project creation, workflow/policy materialization). Until a Blueprint can be instantiated into a working project, nothing else in the feature delivers value.

**Independent Test**: From the Blueprints catalog, select a predefined Blueprint and instantiate it; verify a new project is created whose cast is the result of casting the Blueprint's roles (named agents, one per role), whose effective default workflow is the Blueprint's default workflow materialized into `.agentweaver/workflows/`, and whose Review Policy and Sandbox Policy match the Blueprint's. Confirm the project can execute a run.

**Acceptance Scenarios**:

1. **Given** a predefined Blueprint in the catalog, **When** the user instantiates it, **Then** a new Project is created and recorded as an instance of that Blueprint (the project tracks which Blueprint, and which Blueprint version, it came from).
2. **Given** a Blueprint whose cast defines N roles, **When** it is instantiated, **Then** the casting algorithm (`CastingService`) runs over those N roles and the project's cast contains N named agents, one per role, with the universe/persona names allocated at instantiation (not stored in the Blueprint).
3. **Given** a Blueprint with one or more workflows and a designated default, **When** it is instantiated, **Then** the default workflow is materialized into the project's `.agentweaver/workflows/` per Feature 010 and becomes the project's effective default workflow.
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

### User Story 5 - Instantiate a Project from a user-provided Blueprint file (Priority: P1)

A user has a Blueprint descriptor file (for example one exported from a teammate, kept in version control, or hand-authored) and wants to spin up a Project from it without it first being a predefined catalog entry. They supply the file's contents to the system, which parses it against the Blueprint schema, validates every reference against the current environment (workflow, review policy, sandbox profile, and each roster role), and then instantiates a Project exactly as it would from a predefined Blueprint: casting allocates persona names over the roster roles, the default workflow is materialized, and the policies are bound. The supplied document is persisted into the authored store as part of instantiation so the project's provenance always resolves (FR-036). File-sourced Blueprints are additive: the four predefined Blueprints remain available, and a file simply adds another input surface at instantiation time.

**Why this priority**: This is the extensibility the feature exists to enable — a user can capture a complete team definition as a portable file and instantiate it anywhere, with the same guarantees as a predefined Blueprint. It is P1 because "Blueprints must be instantiatable from a file" is the explicit request, and because it exercises the one-schema/multi-source model end to end.

**Independent Test**: Author a Blueprint descriptor file whose roster references only catalog roles and whose workflow/review/sandbox references resolve; supply it and instantiate; verify a Project is created with one named agent per roster role, the default workflow materialized, and the policies bound, and that the four predefined Blueprints are still listed. Then supply a file whose roster references an unknown/non-castable role and confirm instantiation fails closed with a clear, reference-scoped error and no partial Project.

**Acceptance Scenarios**:

1. **Given** a valid Blueprint descriptor file (schema-conformant, all references resolvable, roster roles all present in the catalog groupings), **When** the user instantiates a Project from the file's contents, **Then** a Project is created identically to instantiating a stored Blueprint — casting over the roster roles, default workflow materialized, Review and Sandbox policies bound — and the supplied document is persisted so the project's `source_blueprint_id` + version resolve (FR-036).
2. **Given** a Blueprint file whose roster references a role id that is not present in the catalog (a bespoke/unknown role), **When** it is supplied for import or instantiation, **Then** validation fails closed with a clear, reference-scoped error naming the unknown role, no Blueprint row is written, and no Project is created.
3. **Given** a Blueprint file that references an unknown workflow id, an unresolved Review Policy name, or an unresolved Sandbox profile, **When** it is supplied, **Then** validation fails closed with a reference-scoped error listing every unresolved reference, all-or-nothing (FR-037).
4. **Given** a Blueprint file is instantiated successfully, **When** the catalog is listed, **Then** the four predefined Blueprints remain available and unchanged; the file-sourced Blueprint is additive (resolved into the authored store), not a replacement.
5. **Given** a malformed Blueprint file (unparseable, or missing a required field such as the default workflow or a non-empty roster), **When** it is supplied, **Then** parsing/validation fails with a specific, file-scoped message and nothing is persisted or instantiated.

---

### User Story 6 - Apply a Blueprint when creating a Project (blank or from GitHub) (Priority: P1)

A user creating a new Project — either a blank project or one cloned from a GitHub repository — wants it pre-equipped rather than empty. At create time they choose a Blueprint (a predefined one, a file/generated one, or a stored authored one). The system applies it: it seeds the project roster by casting the Blueprint's roles into named agents, sets the project's default workflow from the Blueprint, and binds the project's review and sandbox policies from the Blueprint. A user who creates a project without choosing a Blueprint gets today's default behavior unchanged.

**Why this priority**: Applying a Blueprint at creation is the most direct way the construct delivers value — a new project arrives ready to run with a team, a workflow, and policies, on both creation paths. It is P1 because it connects Blueprints to the primary project-creation entry points users already use.

**Independent Test**: Create a blank project with a chosen Blueprint and confirm the project has one named agent per Blueprint role, the Blueprint's default workflow materialized, and its review/sandbox policies bound; repeat on the from-GitHub path. Then create a project with no Blueprint and confirm behavior matches today's default (no Blueprint-seeded roster; existing default materialization).

**Acceptance Scenarios**:

1. **Given** the blank-project create path, **When** a user applies a Blueprint by `blueprint_id`, **Then** the project is created with a roster cast from the Blueprint's roles, the Blueprint's default workflow materialized, and its review and sandbox policies bound (FR-042).
2. **Given** the from-GitHub create path, **When** a user applies a Blueprint, **Then** the same seeding occurs (roster cast, default workflow materialized, policies bound) after the repository is cloned (FR-042).
3. **Given** either create path, **When** a user supplies an inline Blueprint document instead of a `blueprint_id`, **Then** the document is validated, persisted so provenance resolves, and applied identically (FR-043).
4. **Given** either create path, **When** a user creates a project WITHOUT a Blueprint, **Then** the project is created with today's default behavior and no Blueprint-seeded roster (FR-042).
5. **Given** an applied Blueprint that fails validation or whose casting fails, **When** project creation runs, **Then** it fails closed and leaves no half-created project (FR-019/FR-043).

---

### User Story 7 - Generate a Blueprint from a natural-language description (Priority: P2)

A user describes the project they want in plain language — for example "documentation reviewer" or "bug triager" — and the system generates a Blueprint for it via the LLM. The generator assembles a roster, a default workflow, and policy references, selecting only roles that already exist in the catalog and reusing the closest-fitting ones. The generator MUST NOT create roles. When no catalog role covers a described need, the generated output is rejected by validation rather than introducing a bespoke role. The user receives a validated Blueprint and can then apply or instantiate it like any other Blueprint.

**Why this priority**: Generation lowers the authoring barrier by assembling Blueprints from the curated catalog from intent rather than hand-authoring. It is P2 because applying and instantiating Blueprints (US1, US6) deliver the primary value first; generation is an authoring convenience layered on top, and it reuses the same validation and role-constraint guarantees.

**Independent Test**: Call generate with a description that maps cleanly onto existing catalog roles and confirm the returned Blueprint reuses those roles and validates; call generate with a description that needs a capability no catalog role covers and confirm the operation fails closed with a clear, role-scoped error and persists nothing — no role is created.

**Acceptance Scenarios**:

1. **Given** a description that maps onto existing catalog roles, **When** the user calls `POST /api/blueprints/generate { description }`, **Then** the response is a validated Blueprint whose roster reuses existing catalog roles (FR-044/FR-045).
2. **Given** a description that needs a capability no catalog role covers, **When** generation runs, **Then** the operation fails closed with a clear, role-scoped error and creates no role and persists no partial Blueprint (FR-045/FR-046).
3. **Given** a generated Blueprint, **When** it is returned, **Then** it has been validated against the same schema and role constraint as predefined/file Blueprints (FR-037/FR-039/FR-040), so it can be applied or instantiated with no further conversion.
4. **Given** generation where validation fails, **When** the operation runs, **Then** it fails closed and persists no partial Blueprint (FR-046).

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
- **Bespoke role in a file Blueprint**: A supplied Blueprint file whose roster references a role id not present in the catalog groupings MUST be rejected with a clear, role-scoped error; the system MUST NOT cast a bespoke/unknown role. A new role must be added to the catalog first (FR-040).
- **Project creation with and without a Blueprint**: Applying a Blueprint at create time seeds roster + default workflow + policies on both the blank and from-GitHub paths (FR-042); omitting a Blueprint MUST leave today's default create behavior unchanged.
- **Generation selects only catalog roles**: The generator MUST assemble rosters from existing catalog roles only and MUST NOT create roles. When a fitting catalog role exists it MUST be reused; when no catalog role covers a described need, generation MUST fail closed with a clear, role-scoped error (FR-045).
- **Generation failure**: If a generated Blueprint fails validation, the operation MUST fail closed, persisting no partial Blueprint and creating no role (FR-046).

## Requirements *(mandatory)*

### Functional Requirements

**Blueprint as a first-class construct**

- **FR-001**: The system MUST define a **Blueprint** as a first-class, reusable, versioned, shippable definition that is **instantiated into a Project**, such that a Project is recorded as an **instance of a Blueprint**.
- **FR-002**: A Blueprint MUST contain exactly three facets: a **Cast** expressed as role definitions, one or more **Workflows** with a designated default, and **Policies** (a Review Policy reference and a Sandbox Policy reference). It MUST NOT contain Ceremonies or Seed memories.
- **FR-003**: The Blueprint construct MUST **generalize and absorb** today's team-template concept (the cast-only catalog templates loaded by `CatalogReader.cs`): an existing team template MUST be representable as the cast facet of a Blueprint, with no loss of its roles or responsibilities (US4).
- **FR-004**: The product MUST ship a catalog of **predefined Blueprints** (the four named in FR-033: Content authoring, Product management, Software Development, Product & Software Delivery) that are instantiable out of the box.
- **FR-005**: Users MUST be able to **author** their own Blueprints and to **fork** an existing (predefined or authored) Blueprint into an independent Blueprint; forking MUST NOT mutate the source Blueprint, and predefined Blueprints MUST NOT be editable in place.

**Cast — roles only, casting at instantiation**

- **FR-006**: A Blueprint's **Cast** MUST be expressed as **role definitions only** — each role carries a role name, responsibilities, and an OPTIONAL per-role model preference — and MUST NOT carry concrete character/universe persona names.
- **FR-007**: At **instantiation**, the system MUST run the existing **casting algorithm** (`apps/Agentweaver.Api/Casting/CastingService.cs`) over the Blueprint's roles to allocate universe/persona names, producing one named cast member per role; the Blueprint itself remains universe-agnostic.
- **FR-008**: A Blueprint that hardcodes concrete persona/universe names instead of abstract roles MUST fail validation with a clear message (persona allocation is the casting algorithm's responsibility at instantiation, FR-007).
- **FR-009**: A Blueprint with zero roles MUST fail validation and MUST NOT be instantiable.

**Workflows — compose Feature 010**

- **FR-010**: A Blueprint MUST reference **one or more Workflows** — the YAML workflows defined in Feature 010 (`specs/010-yaml-workflows-review-policies`) — and MUST designate exactly **one default** workflow; zero or multiple defaults MUST fail validation.
- **FR-011**: At instantiation, the Blueprint's **default workflow** MUST be materialized into the project's `.agentweaver/workflows/` per Feature 010 (the in-code DefaultWorkflowTemplate / materialization path), becoming the project's effective default workflow.
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
- **FR-027**: A Blueprint cast role MAY carry an OPTIONAL **model preference**; a project MAY override that preference at instantiation or at runtime. The model provider MUST remain GitHub Copilot (Principle II): a per-role model preference MAY influence model selection within the allowed provider and MUST NOT select a different provider.
- **FR-028**: No shipped surface produced by this feature (Blueprint definitions, catalog listings, validation messages, logs, UI) may contain emojis (Principle VIII), and no part of the implementation may use mocks, fakes, stubs, or placeholders (Principle VII).

**Scope, versioning & storage (resolved clarifications)**

- **FR-029**: A v1 Blueprint MUST contain exactly the three facets defined in FR-002 (Cast, Workflows, Policies). **Per-role skills and tool grants are OUT OF SCOPE for v1** and MUST NOT be treated as a Blueprint facet. (Per-role skills/tool grants are noted as a possible future extension only; they are not a v1 requirement.)
- **FR-030**: Blueprint instantiation MUST be a **one-time copy**. The created Project records the identity and version of its source Blueprint for provenance (FR-018). When a Blueprint changes after instantiation, existing Projects MUST be left untouched: there MUST be no automatic re-sync and no drift re-materialization in v1. A Blueprint MAY carry a simple version identifier for provenance, but a Blueprint Sync (FR-022) MUST NOT propagate Blueprint edits into already-created Projects.
- **FR-031**: Blueprint storage and discovery MUST be **dual-source**, mirroring Feature 010's Review Policy model: **predefined** Blueprints ship as files in a discoverable catalog, and **user-authored/forked** Blueprints are persisted via an API + store. The catalog MUST merge both sources for discovery and listing. (This is extended by FR-034–FR-041 with a third input surface — a supplied Blueprint file — which resolves into the authored store on import/instantiate; the schema is shared across all sources.)
- **FR-032**: A Project MUST have a **single** source Blueprint. v1 MUST NOT support switching a Project's Blueprint after creation, and MUST NOT support composing multiple Blueprints into one Project (reinforcing FR-001 and FR-018).
- **FR-033**: v1 MUST ship exactly **four** predefined Blueprints: **Content authoring**, **Product management**, **Software Development**, and **Product & Software Delivery** (a combination of the Product management and Software Development rosters). Each predefined Blueprint MUST define its own Cast (roles), reference one or more Workflows with a designated default, and reference a Review Policy and a Sandbox Policy, such that each ships as a complete, instantiable Cast plus default Workflow plus Policy references. (The exact role roster of each Blueprint is a planning detail and is not enumerated here.)

**File-based extensibility (portable, shareable Blueprints)**

- **FR-034**: A Blueprint MUST be expressible as a portable, self-contained **Blueprint file** so it can be shared, version-controlled, and handed to another user who can then instantiate a Project from it. The file format MUST be the **same schema** used by the predefined catalog source (one schema, three sources: embedded catalog, authored store, and supplied file); a predefined or authored Blueprint exported to a file MUST round-trip back in (export then import MUST reproduce an equivalent Blueprint, modulo storage identity).
- **FR-035**: The system MUST support **importing a Blueprint from a file** (file content supplied to the API): the imported Blueprint MUST be validated (FR-016, FR-023) and persisted into the authored Blueprint store (FR-031), after which it is discoverable and instantiable like any authored Blueprint.
- **FR-036**: The system MUST support **instantiating a Project directly from a supplied Blueprint document** (a file's contents) in addition to instantiating from a stored Blueprint id. To preserve provenance and the one-time-copy / no-drift guarantee (FR-018, FR-030), a direct instantiation MUST result in the project tracing to a stored Blueprint record: the supplied document MUST be persisted (or upserted) into the authored store as part of instantiation so the project's `source_blueprint_id` + version always resolve to an existing Blueprint record. There MUST NOT be a project whose source Blueprint cannot be resolved after instantiation.
- **FR-037**: On import or instantiate-from-file, the system MUST validate the supplied document against the Blueprint schema AND verify that every reference resolves **in the current environment**: each referenced workflow id exists in the workflow catalog/registry, the referenced Review Policy name resolves, the referenced Sandbox Policy preset/name resolves, and **every roster role id resolves to a known, castable catalog role** (FR-039). Validation MUST **fail closed** with a clear, reference-scoped error listing every unresolved reference, and MUST NOT produce a partial Blueprint or a partial Project (all-or-nothing, consistent with FR-016 and FR-019).
- **FR-038**: **Exporting** a Blueprint to a file, **importing** a Blueprint from a file, and **instantiating from a supplied document** MUST be reachable identically from the **MCP server and the Web UI** (Principle IV), with all parsing, validation, persistence, and instantiation performed server-side and no business logic in either client (Principle III). The Web Blueprints management page MUST allow importing a Blueprint file (upload or paste), exporting an existing Blueprint to a file, and creating a Project from an uploaded/pasted Blueprint document.
- **FR-039**: A Blueprint file MUST be a JSON document consistent with the existing catalog grouping/role JSON shape (`packages/Agentweaver.Squad/Catalog/Resources/groupings/*.json` and `roles/*.json`), with the following schema:
  - `id` (string, required) — stable Blueprint identifier (kebab-case, as catalog grouping ids).
  - `name` (string, required) — human-readable Blueprint name (the catalog grouping `title` equivalent).
  - `description` (string, optional) — short summary.
  - `version` (string, required) — provenance version identifier (FR-018/FR-030).
  - `cast` (object, required) — the roster: `cast.roles` (array, required, length >= 1) whose entries are **catalog role ids** (the same role-id strings used in `groupings/*.json` `roles[]`, e.g. `"backend-engineer"`). A roster entry MAY instead be an object `{ "role": "<catalog-role-id>", "model_preference": "<model>" }` to carry an optional per-role model preference (FR-027); `role` MUST still be a catalog role id.
  - `workflows` (object, required) — `workflows.ids` (array, required, length >= 1) of workflow ids that resolve in the workflow catalog/registry, and `workflows.default` (string, required) which MUST be a member of `workflows.ids` (FR-010).
  - `policies` (object, required) — `policies.review` (string, required) a Review Policy name that resolves (Feature 010), and `policies.sandbox` (string, required) a Sandbox Policy profile/preset name that resolves (`YamlSandboxPolicyStore` model).
  The file MUST NOT contain concrete persona/universe names (FR-006/FR-008), Ceremonies, or Seed memories (FR-002). This is the **same schema** the embedded catalog and authored store use (FR-034); only the input surface differs.
- **FR-040**: A file-sourced Blueprint MUST obey the **same constraints as predefined Blueprints**: its `cast.roles` roster MUST reference ONLY roles already present in the catalog groupings (`Catalog/Resources/groupings/*.json` role union, resolved via `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`), and MUST introduce no bespoke roles. A roster role that is unknown or not castable MUST be rejected with a clear, role-scoped error (FR-037). No Blueprint capability — predefined, file, authored, or LLM-generated — creates roles; the catalog is a fixed, curated role library (FR-049). A genuinely new role is added only by an out-of-band catalog change (so it is both castable by `CastingService` and usable by Blueprints) before any Blueprint may reference it.
- **FR-041**: File instantiation MUST plug into the **same instantiation entry point** as catalog/stored Blueprints: a supplied document is parsed and validated (FR-037), persisted/upserted into the authored store (FR-031/FR-036), and then instantiated through the existing Blueprint-to-Project flow (the `ProjectService` create-from-Blueprint path that runs casting over the roster and materializes the default workflow and policies, FR-017). File-sourced Blueprints are **additive at instantiation time**: the four predefined Blueprints (FR-033) remain available and unchanged; a file adds a third input source (embedded catalog, authored store, supplied file) feeding one shared validation + instantiation pipeline.

**Apply a Blueprint at project creation**

- **FR-042**: Project creation MUST optionally accept a Blueprint to apply, on BOTH the **blank** project path and the **from-GitHub** project path (`apps/Agentweaver.Api/Projects/ProjectService.cs` `CreateBlankAsync` / `CreateFromGitHubAsync`). Applying a Blueprint MUST: seed the project roster by running casting over the Blueprint's roster roles (FR-007), set the project's **default workflow** by materializing the Blueprint's default workflow (FR-011), and bind the project's **Review Policy** and **Sandbox Policy** from the Blueprint (FR-013, FR-014). Creating a project WITHOUT a Blueprint MUST preserve the current default behavior (no roster seeded by a Blueprint; existing default workflow/review/sandbox materialization unchanged).
- **FR-043**: The Blueprint applied at project creation MAY be specified either by **`blueprint_id`** (a predefined or authored/stored Blueprint) OR by an **inline Blueprint document** (the same schema as FR-039). The two are mutually exclusive on a single create call. An applied Blueprint MUST be validated and its references resolved exactly as for any other source (FR-037, FR-040); an inline document MUST be persisted/upserted into the authored store so the created project's provenance resolves (FR-036). On any validation or casting failure the create MUST fail closed, leaving no half-created project (FR-019).

**LLM-generated Blueprints**

- **FR-044**: The system MUST support **generating a Blueprint from a natural-language description** via `POST /api/blueprints/generate { description }` (for example "documentation reviewer" or "bug triager"). The response MUST be a **validated Blueprint** (the FR-039 schema). Generation MUST be a server-side capability with all LLM interaction and validation performed in the API (Principle III); the model provider remains GitHub Copilot (Principle II).
- **FR-045**: The Blueprint generator MUST assemble rosters from **existing catalog roles only** and reuse the closest-fitting catalog role for each described need. The generator MUST NOT create roles. When no catalog role covers a described need, generation MUST fail closed with a clear, role-scoped error; it MUST NOT introduce a bespoke role or write to the catalog. A generated Blueprint MUST be validated against the **same schema and role constraint** as predefined/file Blueprints (FR-037, FR-039, FR-040): its roster references only catalog role ids, satisfying FR-040.
- **FR-046**: A generated Blueprint MUST NOT be applied or instantiated until it is validated; if validation fails, the system MUST fail closed and persist no partial Blueprint and create no role (all-or-nothing, consistent with FR-019).

**API endpoint surface & parity**

- **FR-047**: The Blueprint API surface MUST include at least: **`GET /api/blueprints`** (list available Blueprints across all sources with validation status, FR-021/FR-031), **`POST /api/blueprints/generate`** (FR-044), and **`POST /api/blueprints/validate`** (validate a supplied Blueprint document against the schema + reference/role constraints without persisting, returning the same Validation Result as import, FR-037). Project creation MUST accept an OPTIONAL Blueprint field (`blueprint_id` OR an inline blueprint document, FR-043).
- **FR-048**: All Blueprint capabilities added here — applying a Blueprint at project creation (both paths), generating a Blueprint from a description, validating a Blueprint document, and listing Blueprints — MUST be reachable identically from the **MCP server and the Web UI** (Principle IV), with all generation, validation, casting, and materialization performed server-side and no business logic in either client (Principle III).

**Catalog role library**

- **FR-049**: Because no Blueprint capability creates roles (FR-040, FR-045), the catalog MUST maintain a **curated role library broad enough to cover the common project archetypes** the product targets, so that predefined, file, authored, and generated Blueprints can be assembled from existing catalog roles without minting. Expanding the role library is an out-of-band catalog change (adding a role definition JSON under `Catalog/Resources/roles/` plus a charter under `Catalog/Resources/charters/`, and listing the role in the relevant grouping under `Catalog/Resources/groupings/`), independent of any Blueprint operation.

### Key Entities *(include if feature involves data)*

- **Blueprint**: A first-class, reusable, versioned, shippable definition that is instantiated into a Project (a Project is an instance of a Blueprint). Generalizes and absorbs today's team template. Contains exactly three facets — **Cast** (roles), **Workflows** (one or more, with a designated default), and **Policies** (a Review Policy reference and a Sandbox Policy reference) — and excludes Ceremonies and Seed memories. Per-role skills/tool grants are out of scope for v1 (possible future extension). May be predefined (shipped) or authored/forked by a user; storage is dual-source (predefined Blueprints ship as catalog files, authored/forked Blueprints persist via API + store). Universe-agnostic: it never stores concrete persona names. Carries a simple version identifier for provenance; instantiation is a one-time copy and Blueprint edits do not propagate into already-created Projects. v1 ships four predefined Blueprints: **Content authoring**, **Product management**, **Software Development**, and **Product & Software Delivery**.
- **Cast Role**: An abstract member of a Blueprint's cast: a role name, responsibilities, and an OPTIONAL per-role model preference. Carries no persona/universe name. At instantiation, the casting algorithm (`CastingService`) allocates a universe name to each role to produce a named cast member. The unit that absorbs today's team-template composition.
- **Workflow (referenced)**: A Feature 010 YAML workflow that a Blueprint references; the Blueprint designates one as **default**. Composed/referenced by the Blueprint, not redefined. The default is materialized into the project's `.agentweaver/workflows/` at instantiation.
- **Review Policy (referenced)**: The Feature 010 per-project Review Policy (RAI / Rubber-duck / Human-review) that a Blueprint references by name; bound to the project at instantiation. Composed/referenced, not redefined.
- **Sandbox Policy (referenced)**: The shell / network / destructive-command gating policy modeled by the existing `YamlSandboxPolicyStore`, referenced by a Blueprint and bound to the project at instantiation. Composed/referenced, not redefined; may only constrain within the runtime's mandatory guarantees, never relax them.
- **Project (instance)**: The Feature 003 project container, now understood as an **instance of a Blueprint**. Its cast is the casting result over the Blueprint's roles; its default workflow and policies are materialized/bound from the Blueprint. Records the identity and version of the source Blueprint for provenance.
- **Blueprints Management Page / Sync**: A Web UI surface (modeled on the existing Team page and the Feature 010 Workflows page) that lists available Blueprints with validation status, supports instantiation, and exposes a **Sync** action to re-read the Blueprint source. The same list/sync/read capability is exposed via the MCP server at parity.
- **Validation Result**: The per-Blueprint outcome of discovery and validation (valid / invalid with a specific, actionable, Blueprint-scoped message — e.g., unresolved reference, missing or duplicate default workflow, zero roles, hardcoded persona names), surfaced identically to both clients.
- **Blueprint File**: A portable, self-contained on-disk representation of a Blueprint using the **same JSON schema** as the embedded catalog source, consistent with the catalog grouping/role JSON shape (one schema across embedded / store / file). Its fields are `id`, `name`, optional `description`, `version`, `cast.roles` (a roster of catalog role ids, optionally `{ role, model_preference }`), `workflows` (`ids[]` + `default`), and `policies` (`review`, `sandbox`) — see FR-039. Its roster may reference ONLY catalog roles; bespoke roles are rejected (FR-040). It can be **exported** from any Blueprint and **imported** (persisted into the authored store) or supplied **inline at instantiation**; a supplied document is validated and resolved into a stored Blueprint record so project provenance always resolves (FR-034–FR-037, FR-041).
- **Generated Blueprint**: A Blueprint produced by the LLM from a natural-language description via `POST /api/blueprints/generate` (FR-044). It conforms to the FR-039 schema and assembles its roster from existing catalog roles only. It is validated against the same schema and role constraint as predefined/file Blueprints before it can be applied or instantiated; a description that no catalog role set covers is rejected and creates no role (FR-045/FR-046).
- **Project Creation Blueprint Option**: An OPTIONAL input on both project-creation paths (blank and from-GitHub). It is either a `blueprint_id` (predefined/authored/stored) OR an inline Blueprint document (mutually exclusive). When present, it seeds the project roster (casting), default workflow, and review/sandbox policies; when absent, creation keeps today's default behavior (FR-042/FR-043).
- **Catalog Role Library**: The fixed, curated set of roles in the catalog (`Catalog/Resources/roles/` + `charters/`, grouped under `Catalog/Resources/groupings/`). Every Blueprint roster — predefined, file, authored, or generated — references only these roles; no Blueprint operation creates roles. The library is maintained broad enough to cover the common project archetypes, and is expanded only by an out-of-band catalog change (FR-049).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Instantiating any predefined Blueprint produces a runnable project whose cast has exactly one named agent per Blueprint role, whose effective default workflow is the Blueprint's default, and whose Review and Sandbox policies match the Blueprint — verified for 100% of shipped predefined Blueprints.
- **SC-002**: For every existing catalog team template, the project produced by instantiating its corresponding Blueprint has the same cast (same roles, names allocated by casting) the team template produced before this feature — 0 regressions in cast composition.
- **SC-003**: A user can author a new Blueprint and fork an existing one, and both become instantiable, with the fork leaving the original unchanged — demonstrated end to end without any source-code change.
- **SC-004**: Every invalid Blueprint case tested (zero roles, missing/duplicate default workflow, unresolved policy/workflow reference, hardcoded persona names) is reported with a clear Blueprint-scoped error and excluded, without affecting other valid Blueprints — 100% of cases.
- **SC-005**: No Blueprint can cause a run to execute with weaker safety than the runtime guarantees: in 100% of tested instantiations, RAI content-safety failure and human approval for irreversible actions still apply, and the referenced Sandbox Policy never relaxes a mandatory boundary.
- **SC-006**: Every Blueprint capability in scope (list, read, fork, instantiate, sync) is performed successfully and yields identical resulting state from both the MCP server and the Web UI — 0 capabilities reachable from only one client.
- **SC-007**: A Sync that changes the Blueprint source refreshes the available set and per-Blueprint validation status, while any instantiation already in flight completes on the definition it started with — verified across the tested edit/sync cases.
- **SC-008**: A Blueprint exported to a file and re-imported produces an equivalent Blueprint (same facets, same refs) — round-trip equality verified for predefined and authored Blueprints; an imported or supplied-inline Blueprint with an unresolved reference is rejected with a reference-scoped error and leaves no partial Blueprint or Project — 100% of tested file cases.
- **SC-009**: A Project instantiated from a valid supplied Blueprint file is created identically to one from a stored Blueprint (one named agent per roster role, default workflow materialized, policies bound, provenance resolvable), while a file whose roster references a bespoke/unknown role is rejected with a role-scoped error and creates nothing — verified for 100% of tested file-instantiation cases, with the four predefined Blueprints remaining available and unchanged.
- **SC-010**: Applying a Blueprint at project creation on both the blank and from-GitHub paths produces a project seeded with one named agent per Blueprint role, the Blueprint's default workflow materialized, and its review/sandbox policies bound; creating without a Blueprint reproduces today's default behavior — verified for both paths, with and without a Blueprint.
- **SC-011**: Blueprint generation from a description returns a Blueprint that validates against the schema and role constraint in 100% of accepted cases, with a roster drawn entirely from existing catalog roles; a description that no catalog role set covers is rejected with a role-scoped error and persists nothing and creates no role.
- **SC-012**: The endpoint surface (`GET /api/blueprints`, `POST /api/blueprints/generate`, `POST /api/blueprints/validate`, and the optional Blueprint field on project creation) yields identical resulting state from the MCP server and the Web UI — 0 capabilities reachable from only one client.

## Assumptions

- "Team template" refers to the existing cast-only catalog templates loaded by `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`; this feature generalizes them into the cast facet of a Blueprint without removing their behavior (US4, FR-003).
- The casting algorithm referenced for instantiation is the existing `apps/Agentweaver.Api/Casting/CastingService.cs` (which proposes/assigns universe persona names to roles); this feature reuses it at instantiation and does not redefine how casting allocates names.
- Project creation reuses the existing `apps/Agentweaver.Api/Projects/ProjectService.cs` path (`CreateBlankAsync` / `CreateFromGitHubAsync`), which already materializes project state; instantiation extends this path to drive casting from Blueprint roles and to materialize the Blueprint's default workflow and policies.
- Workflows, Review Policies, and the materialization of a default workflow into `.agentweaver/workflows/` are owned by Feature 010 (`specs/010-yaml-workflows-review-policies`); this Blueprint feature composes/references them and does not re-specify their internals.
- The Sandbox Policy is the existing model behind `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs` (shell / network / destructive-command gating); the Blueprint references it rather than redefining it.
- The Sync interaction model mirrors the existing Team page and the Feature 010 Workflows page (load-on-start / explicit Sync, not file-watch, not per-heartbeat), including the Feature 011 web-app-shell navigation conventions for where such a management page lives.
- Ceremonies and Seed memories are explicitly out of scope for a Blueprint (locked decisions), and a Blueprint is explicitly not a memory seed.
- The model provider remains fixed to GitHub Copilot (Principle II); a Blueprint cast role's optional model preference is a within-provider influence at most and a project MAY override it at instantiation or runtime (FR-027).
- "Project" is the Feature 003 project container; provenance (which Blueprint/version a project came from) is new state introduced by this feature.

## Resolved Clarifications

### Session 2026-06-22 (clarify pass)

All six previously-open questions were resolved and encoded as firm requirements. No clarification markers remain.

1. **Per-role skills & tools** → RESOLVED (FR-029): Deferred. A v1 Blueprint has exactly three facets (Cast, Workflows, Policies); per-role skills/tool grants are out of scope for v1 and noted only as a possible future extension.
2. **Model defaults level** → RESOLVED (FR-027): A cast role MAY carry an optional model preference; a project MAY override it at instantiation or runtime; provider stays GitHub Copilot.
3. **Versioning & drift** → RESOLVED (FR-030): Instantiation is a one-time copy; Projects record source Blueprint identity + version for provenance; Blueprint changes after instantiation leave existing Projects untouched (no auto re-sync, no drift re-materialization in v1).
4. **Storage & discovery** → RESOLVED (FR-031): Dual-source — predefined Blueprints ship as catalog files, authored/forked Blueprints persist via API + store; the catalog merges both.
5. **Switch / compose** → RESOLVED (FR-032): One Blueprint per Project; no switching after creation and no multi-Blueprint composition in v1.
6. **v1 catalog scope** → RESOLVED (FR-033): v1 ships exactly four predefined Blueprints — Content authoring, Product management, Software Development, Product & Software Delivery — each with a complete Cast, a default Workflow, and Review + Sandbox policy references.
