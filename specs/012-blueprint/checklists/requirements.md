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

- [ ] No [NEEDS CLARIFICATION] markers remain — 6 intentional markers remain (FR-027, FR-029, FR-030, FR-031, FR-032, FR-033), preserved by design for the `/speckit.clarify` pass per the requester's explicit instruction to flag these as genuine ambiguities
- [x] Requirements are testable and unambiguous (except the 6 explicitly-flagged open questions)
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded (locked inclusions/exclusions: Cast-as-roles, Workflows, Policies in; Ceremonies and Seed memories out)
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows (browse + instantiate, author/fork, sync, team-template-as-Blueprint)
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- The 6 `[NEEDS CLARIFICATION]` markers are intentional and required: the requester explicitly asked to flag per-role skills/tools (FR-029), model-defaults level (FR-027), versioning/drift (FR-030), storage/discovery (FR-031), switch/compose (FR-032), and v1 catalog scope (FR-033) as genuine open questions rather than inventing answers. They are consolidated in the spec's "Open Questions (for the clarify pass)" section.
- LOCKED decisions encoded as firm requirements (no clarification markers): Blueprint is instantiated into a Project / a Project is an instance of a Blueprint (FR-001); three facets Cast/Workflows/Policies, excluding Ceremonies and Seed memories (FR-002); generalizes/absorbs team templates (FR-003, US4); predefined catalog ships + author/fork (FR-004, FR-005); Cast = roles only, casting allocates persona names at instantiation (FR-006–FR-009); Workflows composed/referenced with one default, materialized per Feature 010 (FR-010–FR-012); Review + Sandbox policies composed/referenced (FR-013–FR-016); Sync mirrors Team/Workflows pattern (FR-021–FR-023); MCP+Web parity, runtime governance, human accountability, Copilot provider, no emojis/mocks (FR-024–FR-028).
- Code citations describe EXISTING seams the feature builds on (casting in `apps/Agentweaver.Api/Casting/`, project creation in `apps/Agentweaver.Api/Projects/ProjectService.cs`, sandbox policy in `YamlSandboxPolicyStore`, catalog in `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`) and cross-reference Feature 010 (workflows + review policies) and Feature 011 (web app shell); they do not prescribe the new implementation.
