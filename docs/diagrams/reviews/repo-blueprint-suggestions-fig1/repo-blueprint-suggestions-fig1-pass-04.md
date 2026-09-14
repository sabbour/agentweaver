# Repository suggestions are heuristic — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: Anonymous metadata feeds deterministic matching; suggestions never invoke a model.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/repo-blueprint-suggestions-fig1.png. Target documentation: docs/deep-dive/repo-blueprint-suggestions.md.

## Actual PNG inspection

Opened repo-blueprint-suggestions-fig1-pass-04.png at enlarged export resolution and repo-blueprint-suggestions-fig1-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

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
| parse-to-fallback | parse | fallback | invalid | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:46-48 |
| metadata-to-cancel | metadata | cancel | cancel | apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-94 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| picker-to-parse: picker → parse | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| parse-to-boundary: parse → boundary | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| boundary-to-metadata: boundary → metadata | (1, 0.5) → (0, 0.5) | (413, 364) → (413, 144) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| metadata-to-match: metadata → match | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| match-to-response: match → response | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| parse-to-fallback: parse → fallback | (0, 0.5) → (0, 0.5) | (19, 254) → (19, 474) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| metadata-to-cancel: metadata → cancel | (1, 0.5) → (1, 0.5) | (808, 144) → (808, 474) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
