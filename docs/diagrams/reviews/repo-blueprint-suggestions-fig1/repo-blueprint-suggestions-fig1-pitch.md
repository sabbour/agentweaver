# Repository suggestions are heuristic — pitch

Audience: Agentweaver implementers and operators.

Takeaway: Anonymous metadata feeds deterministic matching; suggestions never invoke a model.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/repo-blueprint-suggestions-fig1.png. Target documentation: docs/deep-dive/repo-blueprint-suggestions.md.

## Actual PNG inspection

Opened repo-blueprint-suggestions-fig1-pitch.png at enlarged export resolution and repo-blueprint-suggestions-fig1-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| picker: Blueprint picker: Suggested | native:uml (umlActor) | apps/web/src/components/BlueprintPicker.tsx:487-517 |
| metadata: GitHub metadata requests | native:cloud (cloud) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:55-68 |
| parse: Suggestion service | native:flowchart (process) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:40-48 |
| match: Deterministic catalog matching | native:flowchart (process) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:70-84 |
| boundary: Ambient credential boundary | native:flowchart (hexagon) | apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs:38-39 |
| response: Suggested blueprint response | native:flowchart (process) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:78-88 |
| fallback: Recoverable failure | native:flowchart (process) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-108 |
| cancel: Caller cancellation | native:flowchart (doubleEllipse) | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-94 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| picker-to-parse | picker | parse | suggest | apps/web/src/components/BlueprintPicker.tsx:487-517 |
| parse-to-boundary | parse | boundary | resolve | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:51-53 |
| boundary-to-metadata | boundary | metadata | null token | apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs:38-39 |
| metadata-to-match | metadata | match | signals | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:70-74 |
| match-to-response | match | response | recommend | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:78-88 |
| parse-to-fallback | parse | fallback | invalid input | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:46-48 |
| metadata-to-cancel | metadata | cancel | caller canceled | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-94 |

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
