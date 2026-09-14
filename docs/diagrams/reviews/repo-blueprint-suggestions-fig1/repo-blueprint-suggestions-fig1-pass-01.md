# Repository suggestions are heuristic — pass-01

Audience: Agentweaver implementers and operators.

Takeaway: Anonymous metadata feeds deterministic matching; suggestions never invoke a model.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/repo-blueprint-suggestions-fig1.png. Target documentation: docs/deep-dive/repo-blueprint-suggestions.md.

## Actual PNG inspection

Opened repo-blueprint-suggestions-fig1-pass-01.png at enlarged export resolution and repo-blueprint-suggestions-fig1-pass-01-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. The expanded hierarchy is legible at A5 and enlarged. Native Azure identity and database symbols render correctly; accents remain 5 units. Directed arrowheads are visible after spacing/label corrections. The route declaration and ownership diagrams have the specific correction-only handoffs recorded below; remaining diagrams have no observed orientation, overlap or arrow defect.

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

## Growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2210,
  "result_meaningful_xml": 23958,
  "baseline_visible_structures": 8,
  "result_visible_structures": 86,
  "growth_ratio": 10.840724,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\repo-blueprint-suggestions-fig1\\repo-blueprint-suggestions-fig1-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\repo-blueprint-suggestions-fig1\\repo-blueprint-suggestions-fig1-pass-01.drawio"
}
```

Expansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.
