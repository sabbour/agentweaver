# Specification Quality Checklist: Blueprints (Reusable, Versioned Project Definitions)

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-06-22
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs) — code references (`CastingService`, `ProjectService`, `YamlSandboxPolicyStore`, `CatalogReader`) are grounding citations for existing seams, not prescriptions for the new design
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain — all 6 resolved in the clarify pass (2026-06-22) and encoded as firm requirements (FR-027, FR-029, FR-030, FR-031, FR-032, FR-033)
- [x] Requirements are testable and unambiguous (except the 6 explicitly-flagged open questions)
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded (locked inclusions/exclusions: Cast-as-roles, Workflows, Policies in; Ceremonies and Seed memories out)
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows (browse + instantiate, author/fork, sync, team-template-as-Blueprint, instantiate-from-file)
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- The 6 previously-open questions were resolved in the clarify pass (2026-06-22) and encoded as firm requirements: per-role skills/tools deferred (FR-029), per-role optional model preference with project override (FR-027), one-time-copy instantiation with provenance and no post-instantiation drift re-sync (FR-030), dual-source storage/discovery (FR-031), single Blueprint per Project with no switch/compose (FR-032), and a v1 catalog of exactly three predefined Blueprints — Content authoring, Product management, Software Development (FR-033). The spec's "Resolved Clarifications" section summarizes these; a grep for `NEEDS CLARIFICATION` over spec.md returns zero.
- LOCKED decisions encoded as firm requirements (no clarification markers): Blueprint is instantiated into a Project / a Project is an instance of a Blueprint (FR-001); three facets Cast/Workflows/Policies, excluding Ceremonies and Seed memories (FR-002); generalizes/absorbs team templates (FR-003, US4); predefined catalog ships + author/fork (FR-004, FR-005); Cast = roles only, casting allocates persona names at instantiation (FR-006–FR-009); Workflows composed/referenced with one default, materialized per Feature 010 (FR-010–FR-012); Review + Sandbox policies composed/referenced (FR-013–FR-016); Sync mirrors Team/Workflows pattern (FR-021–FR-023); MCP+Web parity, runtime governance, human accountability, Copilot provider, no emojis/mocks (FR-024–FR-028).
- Code citations describe EXISTING seams the feature builds on (casting in `apps/Agentweaver.Api/Casting/`, project creation in `apps/Agentweaver.Api/Projects/ProjectService.cs`, sandbox policy in `YamlSandboxPolicyStore`, catalog in `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`) and cross-reference Feature 010 (workflows + review policies) and Feature 011 (web app shell); they do not prescribe the new implementation.
- File-based extensibility (added 2026-06-23): Blueprints are instantiatable from a user-provided JSON file using the same schema as the embedded catalog (US5, FR-034–FR-041). FR-039 fixes the file schema (id, name, version, `cast.roles` = catalog role ids, `workflows.ids`+`default`, `policies.review`/`policies.sandbox`); FR-040 requires a file roster to reference ONLY catalog roles (no bespoke roles, unknown roles rejected with a role-scoped error); FR-041 routes file instantiation through the shared validate-persist-instantiate pipeline, additive to the four predefined Blueprints (FR-033). SC-009 covers file instantiation. The "four predefined Blueprints" set is unchanged by this addition.
